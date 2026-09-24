using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Fleet.Agent.Services.HostedProviders;
using Fleet.Agent.Tests.Harness;
using Fleet.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Tests;

/// <summary>
/// #335 "Adapter contract": the per-vendor field and item policy, tool flattening, the exact-match
/// response rewrite, and request handling (AC5–AC7).
/// </summary>
/// <remarks>
/// The OpenRouter SSE fixtures under <c>Fixtures/HostedProviders/</c> are scrubbed Phase 0 captures:
/// the vendor's real answers to a real Codex 0.153.4 request sent through this adapter. Scrubbing
/// replaced ids, the cache key, the echoed Codex instructions and the echoed tool schemas, and
/// changed nothing else. The DeepSeek fixture is still <b>synthetic</b>, built from the documented
/// event shapes, because its probe has not run yet; the file name says so.
/// </remarks>
public class ResponsesNamespaceAdapterTests
{
    private const string StoredKey = "stored-adapter-key-value";

    // ── Request policy (AC6) ────────────────────────────────────────────────

    /// <summary>A Codex-shaped request carrying every F5 field, plus fields Codex never sends.</summary>
    private static JsonObject CodexRequest(string model = "deepseek-v4-pro") => new()
    {
        ["model"] = model,
        ["instructions"] = "system prompt",
        ["input"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = "hi" } },
            },
            new JsonObject
            {
                ["type"] = "reasoning", ["id"] = "rs_1", ["summary"] = new JsonArray(),
                ["content"] = new JsonArray { new JsonObject { ["type"] = "reasoning_text", ["text"] = "thought" } },
                ["encrypted_content"] = "opaque",
            },
            new JsonObject
            {
                ["type"] = "function_call", ["namespace"] = "mcp__memory", ["name"] = "memory_get",
                ["arguments"] = "{}", ["call_id"] = "call_1",
            },
            new JsonObject { ["type"] = "function_call_output", ["call_id"] = "call_1", ["output"] = "ok" },
            new JsonObject { ["type"] = "custom_tool_call", ["call_id"] = "call_2", ["name"] = "apply_patch", ["input"] = "x" },
        },
        ["tools"] = new JsonArray
        {
            Namespace("mcp__memory", "memory_get"),
            Namespace("mcp__memory", "memory_stats"),
            PlainFunction("exec_command"),
            new JsonObject { ["type"] = "web_search" },
            new JsonObject { ["type"] = "custom", ["name"] = "apply_patch", ["description"] = "d" },
        },
        ["tool_choice"] = "auto",
        ["parallel_tool_calls"] = true,
        ["reasoning"] = new JsonObject { ["effort"] = "high", ["summary"] = "auto", ["context"] = "x" },
        ["store"] = false,
        ["stream"] = true,
        ["include"] = new JsonArray { "reasoning.encrypted_content" },
        ["prompt_cache_key"] = "cache-key",
        ["text"] = new JsonObject { ["verbosity"] = "low", ["format"] = new JsonObject { ["type"] = "text" } },
        ["client_metadata"] = new JsonObject { ["k"] = "v" },
        ["service_tier"] = "flex",
        ["stream_options"] = new JsonObject { ["include_obfuscation"] = false },
        ["access_programs"] = new JsonObject(),
        ["previous_response_id"] = "resp_0",
        ["max_output_tokens"] = 1000,
        ["something_new"] = 1,
    };

    private static JsonObject Namespace(string ns, params string[] members)
    {
        var tools = new JsonArray();
        foreach (var member in members)
            tools.Add(PlainFunction(member));
        return new JsonObject { ["type"] = "namespace", ["name"] = ns, ["description"] = "Tools", ["tools"] = tools };
    }

    private static JsonObject PlainFunction(string name) => new()
    {
        ["type"] = "function",
        ["name"] = name,
        ["description"] = "d",
        ["strict"] = false,
        ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
    };

    [Fact]
    public void DeepSeek_FieldPolicy_KeepsOnlyTheAllowlist()
    {
        var body = CodexRequest();
        var result = ResponsesNamespaceAdapter.RewriteRequest(body, HostedModelProviders.DeepSeek);

        Assert.Null(result.Error);
        Assert.Equal(
            ["input", "instructions", "max_output_tokens", "model", "reasoning", "stream", "text", "tool_choice", "tools"],
            body.Select(kv => kv.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["effort", "summary"], body["reasoning"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(["format"], body["text"]!.AsObject().Select(kv => kv.Key));
    }

    [Fact]
    public void OpenRouter_FieldPolicy_KeepsOnlyTheAllowlist()
    {
        var body = CodexRequest("z-ai/glm-5.3");
        var result = ResponsesNamespaceAdapter.RewriteRequest(body, HostedModelProviders.OpenRouter);

        Assert.Null(result.Error);
        Assert.Equal(
            ["include", "input", "instructions", "max_output_tokens", "model", "parallel_tool_calls",
             "prompt_cache_key", "reasoning", "store", "stream", "text", "tool_choice", "tools"],
            body.Select(kv => kv.Key).Order(StringComparer.Ordinal));
        Assert.False((bool)body["store"]!);
        Assert.Equal(["effort", "summary"], body["reasoning"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(["verbosity", "format"], body["text"]!.AsObject().Select(kv => kv.Key));
    }

    [Fact]
    public void NullReasoning_IsDroppedNotForwarded()
    {
        var body = CodexRequest();
        body["reasoning"] = null;

        ResponsesNamespaceAdapter.RewriteRequest(body, HostedModelProviders.DeepSeek);

        Assert.False(body.ContainsKey("reasoning"));
    }

    [Theory]
    [InlineData("deepseek", false)]
    [InlineData("openrouter", true)]
    public void InputItemPolicy_FlattensCallsStripsUnknownItems(string prefix, bool keepsEncryptedReasoning)
    {
        var provider = HostedModelProviders.All.Single(p => p.Prefix == prefix);
        var body = CodexRequest();

        ResponsesNamespaceAdapter.RewriteRequest(body, provider);
        var input = body["input"]!.AsArray().Select(n => n!.AsObject()).ToList();

        Assert.Equal(["message", "reasoning", "function_call", "function_call_output"],
            input.Select(i => (string)i["type"]!));

        var call = input[2];
        Assert.Equal("mcp__memory__memory_get", (string)call["name"]!);
        Assert.False(call.ContainsKey("namespace"));

        Assert.Equal(keepsEncryptedReasoning, input[1].ContainsKey("encrypted_content"));
        Assert.True(input[1].ContainsKey("content"));
    }

    [Fact]
    public void Tools_NamespacesFlattenFunctionsPassOthersDrop()
    {
        var body = CodexRequest();

        var result = ResponsesNamespaceAdapter.RewriteRequest(body, HostedModelProviders.DeepSeek);
        var tools = body["tools"]!.AsArray().Select(n => n!.AsObject()).ToList();

        Assert.All(tools, t => Assert.Equal("function", (string)t["type"]!));
        Assert.Equal(["mcp__memory__memory_get", "mcp__memory__memory_stats", "exec_command"],
            tools.Select(t => (string)t["name"]!));
        Assert.Equal(("mcp__memory", "memory_get"), result.Table.Flattened["mcp__memory__memory_get"]);
        Assert.Contains("exec_command", result.Table.Plain);
    }

    [Theory]
    [InlineData("mcp__memory", "memory_get", "mcp__memory__memory_get")]
    [InlineData("mcp__memory_", "_memory_get", "mcp__memory__memory_get")]
    [InlineData("mcp__fleet-memory", "memory_get", "mcp__fleet-memory__memory_get")]
    public void JoinToolName_MatchesCodexJoinRule(string ns, string member, string expected) =>
        Assert.Equal(expected, ResponsesNamespaceAdapter.JoinToolName(ns, member));

    [Fact]
    public void ToolChoiceObjectNamingANamespacedTool_IsFlattened()
    {
        var body = CodexRequest();
        body["tool_choice"] = new JsonObject { ["type"] = "function", ["namespace"] = "mcp__memory", ["name"] = "memory_get" };

        ResponsesNamespaceAdapter.RewriteRequest(body, HostedModelProviders.DeepSeek);

        Assert.Equal("mcp__memory__memory_get", (string)body["tool_choice"]!["name"]!);
        Assert.False(body["tool_choice"]!.AsObject().ContainsKey("namespace"));
    }

    // ── Namespace round trip (AC5) ──────────────────────────────────────────

    [Fact]
    public void RoundTrip_JsonResponse_RestoresExactMatchesOnly()
    {
        var body = CodexRequest();
        var table = ResponsesNamespaceAdapter.RewriteRequest(body, HostedModelProviders.DeepSeek).Table;

        var response = new JsonObject
        {
            ["output"] = new JsonArray
            {
                new JsonObject { ["type"] = "function_call", ["name"] = "mcp__memory__memory_get", ["arguments"] = "{}", ["call_id"] = "a" },
                new JsonObject { ["type"] = "function_call", ["name"] = "mcp__memory.memory_get", ["arguments"] = "{}", ["call_id"] = "b" },
                new JsonObject { ["type"] = "function_call", ["name"] = "exec_command", ["arguments"] = "{}", ["call_id"] = "c" },
            },
        };

        var unmatched = ResponsesNamespaceAdapter.RewriteResponseObject(response, table);
        var output = response["output"]!.AsArray().Select(n => n!.AsObject()).ToList();

        Assert.Equal("mcp__memory", (string)output[0]["namespace"]!);
        Assert.Equal("memory_get", (string)output[0]["name"]!);
        Assert.Equal("mcp__memory.memory_get", (string)output[1]["name"]!);
        Assert.False(output[1].ContainsKey("namespace"));
        Assert.Equal("exec_command", (string)output[2]["name"]!);
        Assert.False(output[2].ContainsKey("namespace"));
        Assert.Equal(1, unmatched);
    }

    [Theory]
    [InlineData("deepseek", "deepseek-function-call.synthetic.sse", "memory_get")]
    [InlineData("openrouter", "openrouter-function-call.sse", "memory_stats")]
    public async Task RoundTrip_Sse_UpstreamGetsFlatNames_CodexGetsNamespaceBack(string prefix, string fixture, string member)
    {
        var provider = HostedModelProviders.All.Single(p => p.Prefix == prefix);
        var sse = HostedProviderFixture(fixture);
        var upstream = new RecordingHandler((_, _) => Task.FromResult(SseResponse(sse)));
        var logger = new ListLogger();
        var adapter = NewAdapter(provider, upstream, logger);

        var context = NewContext($"/{prefix}/responses", CodexRequest());
        await adapter.HandleAsync(context);

        Assert.Equal(200, context.Response.StatusCode);

        // Outbound: the namespace member reached the vendor as one flat function name.
        var sent = JsonNode.Parse(Assert.Single(upstream.Bodies))!.AsObject();
        Assert.Contains(sent["tools"]!.AsArray(), t => (string?)t!["name"] == $"mcp__memory__{member}");
        Assert.DoesNotContain(sent["tools"]!.AsArray(), t => (string?)t!["type"] == "namespace");

        // Inbound: every function_call carries namespace + member again — in .added, in .done and
        // in the final response object.
        var events = ParseSse(ReadBody(context));
        var calls = FunctionCalls(events);
        Assert.NotEmpty(calls);
        Assert.Contains(events, e => (string?)e.Json?["type"] == "response.completed");
        Assert.All(calls, call =>
        {
            Assert.Equal("mcp__memory", (string?)call["namespace"]);
            Assert.Equal(member, (string?)call["name"]);
        });

        // Everything else passes through: the same events in the same order, and every event with
        // no function_call in it is JSON-equal to what the vendor sent — reasoning and message
        // items included. A non-JSON terminator such as [DONE] is forwarded as is.
        AssertForwardedUnchangedExceptCalls(ParseSse(sse), events);

        Assert.Contains(logger.Lines, l => l.Contains($"HostedProviderAdapter provider={prefix} status=200")
            && l.Contains("unmatchedCalls=0"));
    }

    [Fact]
    public async Task RealCapture_FollowUpMessage_IsForwardedUnchanged()
    {
        // Phase 0 (c): the vendor's answer after a function_call_output. No call, nothing to restore.
        var sse = HostedProviderFixture("openrouter-follow-up-message.sse");
        var logger = new ListLogger();
        var adapter = NewAdapter(HostedModelProviders.OpenRouter,
            new RecordingHandler((_, _) => Task.FromResult(SseResponse(sse))), logger);

        var context = NewContext("/openrouter/responses", CodexRequest("z-ai/glm-5.3"));
        await adapter.HandleAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        var original = ParseSse(sse);
        var events = ParseSse(ReadBody(context));
        Assert.Empty(FunctionCalls(original));
        AssertForwardedUnchangedExceptCalls(original, events);
        Assert.Equal("[DONE]", events[^1].Data);

        var deltas = string.Concat(events.Where(e => (string?)e.Json?["type"] == "response.output_text.delta")
            .Select(e => (string?)e.Json!["delta"]));
        var message = events.Single(e => (string?)e.Json?["type"] == "response.output_item.done").Json!["item"]!;
        Assert.Equal("message", (string?)message["type"]);
        Assert.Equal(deltas, (string?)message["content"]![0]!["text"]);
        Assert.Contains(logger.Lines, l => l.Contains("status=200") && l.Contains("unmatchedCalls=0"));
    }

    [Fact]
    public async Task Sse_NearMissName_IsForwardedExactlyAsSentAndCounted()
    {
        // A near-miss name is never guessed into a tool: it comes back exactly as sent, in the item
        // events and in the final response, and is counted once.
        const string call = "{\"type\":\"function_call\",\"id\":\"fc_3\",\"call_id\":\"call_3\",\"name\":\"mcp__memory.memory_get\",\"arguments\":\"{}\"}";
        var sse = $"data: {{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{call}}}\n\n"
                + $"data: {{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{call}}}\n\n"
                + $"data: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"resp_3\",\"status\":\"completed\",\"output\":[{call}]}}}}\n\n"
                + "data: [DONE]\n\n";
        var logger = new ListLogger();
        var adapter = NewAdapter(HostedModelProviders.OpenRouter,
            new RecordingHandler((_, _) => Task.FromResult(SseResponse(sse))), logger);

        var context = NewContext("/openrouter/responses", CodexRequest("z-ai/glm-5.3"));
        await adapter.HandleAsync(context);

        var calls = FunctionCalls(ParseSse(ReadBody(context)));
        Assert.Equal(3, calls.Count);
        Assert.All(calls, c =>
        {
            Assert.Equal("mcp__memory.memory_get", (string?)c["name"]);
            Assert.Null(c["namespace"]);
        });
        Assert.Contains(logger.Lines, l => l.Contains("status=200") && l.Contains("unmatchedCalls=1"));
    }

    private static string HostedProviderFixture(string name) =>
        File.ReadAllText(RepoPaths.Resolve($"tests/Fleet.Agent.Tests/Fixtures/HostedProviders/{name}"));

    /// <summary>Every function_call item in item events and in response objects' output.</summary>
    private static List<JsonObject> FunctionCalls(IEnumerable<SseEvent> events) =>
        events.Select(e => e.Json?["item"]).OfType<JsonObject>()
            .Concat(events.Select(e => e.Json?["response"]?["output"]).OfType<JsonArray>()
                .SelectMany(output => output).OfType<JsonObject>())
            .Where(item => (string?)item["type"] == "function_call")
            .ToList();

    private static void AssertForwardedUnchangedExceptCalls(List<SseEvent> original, List<SseEvent> forwarded)
    {
        Assert.Equal(
            original.Select(e => (string?)e.Json?["type"] ?? e.Data),
            forwarded.Select(e => (string?)e.Json?["type"] ?? e.Data));

        for (var i = 0; i < original.Count; i++)
        {
            if (FunctionCalls([original[i]]).Count > 0)
                continue;
            Assert.True(
                original[i].Json is null
                    ? original[i].Data == forwarded[i].Data
                    : JsonNode.DeepEquals(original[i].Json, forwarded[i].Json),
                $"event {i} ({(string?)original[i].Json?["type"] ?? original[i].Data}) changed in transit");
        }
    }

    [Fact]
    public async Task Sse_EventsAreFlushedOneAtATime()
    {
        var sse = "event: response.created\ndata: {\"type\":\"response.created\"}\n\n"
                + "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"a\"}\n\n";
        var adapter = NewAdapter(HostedModelProviders.DeepSeek, new RecordingHandler((_, _) => Task.FromResult(SseResponse(sse))));
        var downstream = new FlushCountingStream();

        await adapter.RewriteSseAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)), downstream,
            new ResponsesNamespaceAdapter.ToolNameTable(), new ResponsesNamespaceAdapter.RequestStats(), CancellationToken.None);

        Assert.Equal(2, downstream.Flushes);
        Assert.Equal(sse, Encoding.UTF8.GetString(downstream.ToArray()));
    }

    [Fact]
    public async Task Sse_UnparseableEventNeedingRewrite_IsForwardedRawWithWarning()
    {
        var sse = "event: response.output_item.done\ndata: {not json\n\n";
        var logger = new ListLogger();
        var adapter = NewAdapter(HostedModelProviders.DeepSeek, new RecordingHandler((_, _) => Task.FromResult(SseResponse(sse))), logger);
        var downstream = new MemoryStream();

        await adapter.RewriteSseAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)), downstream,
            new ResponsesNamespaceAdapter.ToolNameTable(), new ResponsesNamespaceAdapter.RequestStats(), CancellationToken.None);

        Assert.Equal(sse, Encoding.UTF8.GetString(downstream.ToArray()));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("did not parse"));
    }

    // ── Request handling (AC7) ──────────────────────────────────────────────

    [Fact]
    public async Task NameCollisionAfterFlattening_Returns400WithZeroUpstreamCalls()
    {
        var body = CodexRequest();
        body["tools"]!.AsArray().Add(PlainFunction("mcp__memory__memory_get"));
        var upstream = new RecordingHandler((_, _) => throw new InvalidOperationException("must not be called"));

        var context = NewContext("/deepseek/responses", body);
        await NewAdapter(HostedModelProviders.DeepSeek, upstream).HandleAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Contains("mcp__memory__memory_get", ReadBody(context));
        Assert.Equal(0, upstream.Calls);
    }

    [Theory]
    [InlineData("openrouter", 65)]
    [InlineData("deepseek", 129)]
    public async Task ToolNameOverTheProviderLimit_Returns400WithZeroUpstreamCalls(string prefix, int length)
    {
        var provider = HostedModelProviders.All.Single(p => p.Prefix == prefix);
        var body = CodexRequest();
        var member = new string('m', length - "mcp__x__".Length);
        body["tools"]!.AsArray().Add(Namespace("mcp__x", member));
        var upstream = new RecordingHandler((_, _) => throw new InvalidOperationException("must not be called"));

        var context = NewContext($"/{prefix}/responses", body);
        await NewAdapter(provider, upstream).HandleAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Contains("mcp__x__" + member, ReadBody(context));
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public void ToolNameAtTheProviderLimit_IsAccepted()
    {
        var body = CodexRequest();
        body["tools"]!.AsArray().Add(Namespace("mcp__x", new string('m', 64 - "mcp__x__".Length)));

        var result = ResponsesNamespaceAdapter.RewriteRequest(body, HostedModelProviders.OpenRouter);

        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ToolNameWithInvalidCharacters_Returns400()
    {
        var body = CodexRequest();
        body["tools"]!.AsArray().Add(Namespace("mcp__memory", "memory.get"));
        var upstream = new RecordingHandler((_, _) => throw new InvalidOperationException("must not be called"));

        var context = NewContext("/deepseek/responses", body);
        await NewAdapter(HostedModelProviders.DeepSeek, upstream).HandleAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task NamespaceMemberThatIsNotAFunction_Returns400NamingIt()
    {
        var body = CodexRequest();
        var ns = Namespace("mcp__patch");
        ns["tools"]!.AsArray().Add(new JsonObject { ["type"] = "custom", ["name"] = "apply_patch", ["description"] = "d" });
        body["tools"]!.AsArray().Add(ns);
        var upstream = new RecordingHandler((_, _) => throw new InvalidOperationException("must not be called"));

        var context = NewContext("/deepseek/responses", body);
        await NewAdapter(HostedModelProviders.DeepSeek, upstream).HandleAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Contains("mcp__patch.apply_patch (type custom)", ReadBody(context));
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task NamespaceWithNoToolsArray_Returns400NamingIt()
    {
        var body = CodexRequest();
        body["tools"]!.AsArray().Add(new JsonObject { ["type"] = "namespace", ["name"] = "mcp__empty", ["description"] = "d" });
        var upstream = new RecordingHandler((_, _) => throw new InvalidOperationException("must not be called"));

        var context = NewContext("/openrouter/responses", body);
        await NewAdapter(HostedModelProviders.OpenRouter, upstream).HandleAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Contains("mcp__empty (no tools array)", ReadBody(context));
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task OpenRouterStoreTrue_Returns400WithZeroUpstreamCalls()
    {
        var body = CodexRequest();
        body["store"] = true;
        var upstream = new RecordingHandler((_, _) => throw new InvalidOperationException("must not be called"));

        var context = NewContext("/openrouter/responses", body);
        await NewAdapter(HostedModelProviders.OpenRouter, upstream).HandleAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Contains("store:true", ReadBody(context));
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task NonJsonBody_Returns400WithZeroUpstreamCalls()
    {
        var upstream = new RecordingHandler((_, _) => throw new InvalidOperationException("must not be called"));
        var context = NewContext("/deepseek/responses", rawBody: "not json");

        await NewAdapter(HostedModelProviders.DeepSeek, upstream).HandleAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task InboundAuthorization_NeverReachesUpstream_StoredKeyDoes()
    {
        var upstream = new RecordingHandler((_, _) => Task.FromResult(JsonResponse("{\"output\":[]}")));
        var logger = new ListLogger();
        var context = NewContext("/deepseek/responses", CodexRequest());
        context.Request.Headers.Authorization = "Bearer inbound-codex-credential";

        await NewAdapter(HostedModelProviders.DeepSeek, upstream, logger).HandleAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        var auth = Assert.Single(upstream.Authorizations);
        Assert.Equal($"Bearer {StoredKey}", auth);
        Assert.Equal(new Uri("https://api.deepseek.com/responses"), Assert.Single(upstream.Uris));
        Assert.DoesNotContain(logger.Lines, l => l.Contains(StoredKey) || l.Contains("inbound-codex-credential"));
    }

    [Theory]
    [InlineData("GET", "/deepseek/models")]
    [InlineData("GET", "/deepseek/responses")]
    [InlineData("POST", "/openrouter/responses")]
    [InlineData("POST", "/deepseek/responses/extra")]
    public async Task AnyOtherMethodOrPath_Returns404WithZeroUpstreamCalls(string method, string path)
    {
        var upstream = new RecordingHandler((_, _) => throw new InvalidOperationException("must not be called"));
        var context = NewContext(path, CodexRequest());
        context.Request.Method = method;

        await NewAdapter(HostedModelProviders.DeepSeek, upstream).HandleAsync(context);

        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task ConnectFailure_Returns502WithTheDocumentedMessage()
    {
        var upstream = new RecordingHandler((_, _) =>
            throw new HttpRequestException("connect failed", new SocketException((int)SocketError.ConnectionRefused)));
        var context = NewContext("/deepseek/responses", CodexRequest());

        await NewAdapter(HostedModelProviders.DeepSeek, upstream).HandleAsync(context);

        Assert.Equal(502, context.Response.StatusCode);
        var error = JsonNode.Parse(ReadBody(context))!["error"]!["message"]!.GetValue<string>();
        Assert.Equal("phleet adapter: upstream deepseek unreachable: SocketException", error);
    }

    [Fact]
    public async Task UpstreamHttpError_StatusAndBodyPassThrough()
    {
        const string vendorBody = "{\"error\":{\"message\":\"invalid api key\"}}";
        var upstream = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(vendorBody, Encoding.UTF8, "application/json"),
            }));
        var context = NewContext("/deepseek/responses", CodexRequest());

        await NewAdapter(HostedModelProviders.DeepSeek, upstream).HandleAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal(vendorBody, ReadBody(context));
    }

    [Fact]
    public async Task ClientAbort_CancelsTheUpstreamRequestWithin2Seconds()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledAt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        var upstream = new RecordingHandler(async (_, ct) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelledAt.TrySetResult(clock.ElapsedMilliseconds);
                throw;
            }
            throw new InvalidOperationException("unreachable");
        });

        using var abort = new CancellationTokenSource();
        var context = NewContext("/deepseek/responses", CodexRequest());
        context.RequestAborted = abort.Token;

        var handling = NewAdapter(HostedModelProviders.DeepSeek, upstream).HandleAsync(context);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var abortedAt = clock.ElapsedMilliseconds;
        abort.Cancel();

        var observedAt = await cancelledAt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await handling.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(observedAt - abortedAt < 2000);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ResponsesNamespaceAdapter NewAdapter(
        HostedModelProvider provider, RecordingHandler upstream, ILogger? logger = null) =>
        new(provider, () => StoredKey, new HttpMessageInvoker(upstream), logger ?? new ListLogger());

    private static DefaultHttpContext NewContext(string path, JsonObject? body = null, string? rawBody = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(rawBody ?? body!.ToJsonString()));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context) =>
        Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static HttpResponseMessage SseResponse(string sse) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    internal sealed record SseEvent(string? Event, string Data, JsonObject? Json);

    internal static List<SseEvent> ParseSse(string text)
    {
        var events = new List<SseEvent>();
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? name = null;
            var data = new List<string>();
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event:", StringComparison.Ordinal)) name = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Add(line[5..].TrimStart(' '));
            }

            var payload = string.Join("\n", data);
            JsonObject? json = null;
            try { json = JsonNode.Parse(payload) as JsonObject; } catch (System.Text.Json.JsonException) { }
            events.Add(new SseEvent(name, payload, json));
        }
        return events;
    }

    internal sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        public List<string> Bodies { get; } = [];
        public List<string?> Authorizations { get; } = [];
        public List<Uri?> Uris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            lock (Bodies)
            {
                Authorizations.Add(request.Headers.Authorization?.ToString());
                Uris.Add(request.RequestUri);
            }
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Bodies) Bodies.Add(body);
            return await respond(request, ct);
        }
    }

    internal sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IEnumerable<string> Lines { get { lock (Entries) return Entries.Select(e => e.Message).ToList(); } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class FlushCountingStream : MemoryStream
    {
        public int Flushes { get; private set; }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return base.FlushAsync(cancellationToken);
        }
    }
}
