using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fleet.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services.HostedProviders;

/// <summary>
/// Rewrites one Codex Responses request so a hosted vendor can serve it, forwards it, and rewrites
/// the answer back (#335 "Adapter contract").
/// </summary>
/// <remarks>
/// <para>
/// Codex declares every MCP tool as a Responses <c>namespace</c> tool and routes a returned call
/// only when it carries a matching <c>namespace</c> + <c>name</c>. Neither vendor accepts namespace
/// tools. So the adapter flattens each namespace member into a plain <c>function</c> tool named by
/// Codex's own join rule, remembers the mapping for this request only, and restores
/// <c>namespace</c> + <c>name</c> on a returned call whose name matches a flattened name
/// <b>exactly</b>. It never guesses a mapping: a guessed name can dispatch a different, privileged
/// MCP tool (MUST NOT 4).
/// </para>
/// <para>
/// Top-level fields and <c>input[]</c> items go through a per-vendor allowlist. Anything not
/// listed is stripped and named in the per-request log line.
/// </para>
/// <para>
/// No request or response body, header or key value is ever logged (MUST NOT 7).
/// </para>
/// </remarks>
public sealed partial class ResponsesNamespaceAdapter
{
    /// <summary>Codex's <c>MCP_TOOL_NAME_DELIMITER</c>.</summary>
    internal const string NamespaceDelimiter = "__";

    private static readonly HashSet<string> RewrittenEvents = new(StringComparer.Ordinal)
    {
        "response.output_item.added",
        "response.output_item.done",
        "response.completed",
        "response.incomplete",
        "response.failed",
    };

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex ToolNamePattern { get; }

    private readonly HostedModelProvider _provider;
    private readonly FieldPolicy _policy;
    private readonly Func<string> _key;
    private readonly HttpMessageInvoker _upstream;
    private readonly ILogger _logger;
    private readonly Uri _responsesUri;

    internal ResponsesNamespaceAdapter(
        HostedModelProvider provider, Func<string> key, HttpMessageInvoker upstream, ILogger logger)
    {
        _provider = provider;
        _policy = FieldPolicy.For(provider);
        _key = key;
        _upstream = upstream;
        _logger = logger;
        _responsesUri = ResponsesUri(provider);
    }

    /// <summary>The only path the adapter serves: <c>/{prefix}/responses</c>.</summary>
    internal string RoutePath => $"/{_provider.Prefix}/responses";

    internal static Uri ResponsesUri(HostedModelProvider provider)
    {
        if (provider.Upstream.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                $"Hosted provider '{provider.Prefix}' upstream must be https (MUST NOT 3).");
        return new Uri(provider.Upstream.AbsoluteUri.TrimEnd('/') + "/responses");
    }

    // ── HTTP handling ───────────────────────────────────────────────────────

    public async Task HandleAsync(HttpContext context)
    {
        var ct = context.RequestAborted;

        // Any other method or path is 404 without contacting the upstream. That includes Codex's
        // /models refresh, which then falls back to its unknown-model defaults.
        if (!HttpMethods.IsPost(context.Request.Method)
            || !string.Equals(context.Request.Path.Value, RoutePath, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var started = Stopwatch.StartNew();
        var stats = new RequestStats();
        var status = 0;
        try
        {
            status = await ForwardAsync(context, stats, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Codex went away (cancel, interrupt, restart). The upstream request was cancelled
            // through the same token.
            status = 499;
            _logger.LogInformation(
                "HostedProviderAdapter provider={Provider} client aborted; upstream request cancelled",
                _provider.Prefix);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // The upstream failed mid-stream, after headers were sent. Codex sees a broken stream
            // and applies its own stream retry budget.
            status = 502;
            _logger.LogWarning(
                "HostedProviderAdapter provider={Provider} upstream stream failed: {Error}",
                _provider.Prefix, ex.GetBaseException().GetType().Name);
            context.Abort();
        }
        finally
        {
            _logger.LogInformation(
                "HostedProviderAdapter provider={Provider} status={Status} durationMs={DurationMs} "
                + "toolsFlattened={ToolsFlattened} fieldsStripped={FieldsStripped} itemsStripped={ItemsStripped} "
                + "droppedToolTypes={DroppedToolTypes} unmatchedCalls={UnmatchedCalls}",
                _provider.Prefix, status, started.ElapsedMilliseconds, stats.ToolsFlattened,
                string.Join(",", stats.FieldsStripped), stats.ItemsStripped,
                string.Join(",", stats.DroppedToolTypes), stats.UnmatchedCalls);
        }
    }

    private async Task<int> ForwardAsync(HttpContext context, RequestStats stats, CancellationToken ct)
    {
        JsonObject body;
        try
        {
            body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: ct) as JsonObject
                ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            return await WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                "phleet adapter: request body is not a JSON object", ct);
        }

        var rewrite = RewriteRequest(body, _provider, _policy, stats);
        foreach (var field in stats.FieldsStripped)
            _logger.LogDebug("HostedProviderAdapter provider={Provider} stripped field {Field}", _provider.Prefix, field);

        if (rewrite.Error is { } error)
            return await WriteErrorAsync(context, StatusCodes.Status400BadRequest, error, ct);

        var streaming = body["stream"] is JsonValue s && s.TryGetValue<bool>(out var isStream) && isStream;

        using var request = new HttpRequestMessage(HttpMethod.Post, _responsesUri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        // Only the adapter's own key reaches the upstream. Nothing inbound is copied — in
        // particular not an inbound Authorization header.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            streaming ? "text/event-stream" : "application/json"));

        HttpResponseMessage upstream;
        try
        {
            upstream = await _upstream.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            var root = ex.GetBaseException();
            return await WriteErrorAsync(context, StatusCodes.Status502BadGateway,
                $"phleet adapter: upstream {_provider.Prefix} unreachable: {root.GetType().Name}", ct);
        }

        using (upstream)
        {
            var status = (int)upstream.StatusCode;
            context.Response.StatusCode = status;
            var mediaType = upstream.Content.Headers.ContentType?.MediaType;
            if (upstream.Content.Headers.ContentType is { } contentType)
                context.Response.ContentType = contentType.ToString();

            await using var upstreamBody = await upstream.Content.ReadAsStreamAsync(ct);

            // 4xx/5xx: status and body pass through untouched, so the turn fails with the
            // vendor's own message.
            if (!upstream.IsSuccessStatusCode)
            {
                await upstreamBody.CopyToAsync(context.Response.Body, ct);
                return status;
            }

            if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.CacheControl = "no-cache";
                await RewriteSseAsync(upstreamBody, context.Response.Body, rewrite.Table, stats, ct);
                return status;
            }

            using var reader = new StreamReader(upstreamBody, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(ct);
            string output = text;
            try
            {
                if (JsonNode.Parse(text) is JsonObject json)
                {
                    stats.UnmatchedCalls += RewriteResponseObject(json, rewrite.Table);
                    output = json.ToJsonString();
                }
            }
            catch (JsonException)
            {
                _logger.LogWarning(
                    "HostedProviderAdapter provider={Provider} upstream JSON body did not parse; forwarded unchanged",
                    _provider.Prefix);
            }

            await context.Response.WriteAsync(output, ct);
            return status;
        }
    }

    private static async Task<int> WriteErrorAsync(HttpContext context, int status, string message, CancellationToken ct)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var payload = new JsonObject { ["error"] = new JsonObject { ["message"] = message } };
        await context.Response.WriteAsync(payload.ToJsonString(), ct);
        return status;
    }

    // ── SSE ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Forwards an SSE stream one event at a time, flushing after each, rewriting only the events
    /// that carry output items. Never buffers the whole stream (MUST NOT 9).
    /// </summary>
    internal async Task RewriteSseAsync(
        Stream upstream, Stream downstream, ToolNameTable table, RequestStats stats, CancellationToken ct)
    {
        using var reader = new StreamReader(upstream, Encoding.UTF8);
        var block = new List<string>();

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0)
            {
                if (block.Count > 0)
                    await WriteEventAsync(downstream, RewriteSseEvent(block, table, stats), ct);
                block.Clear();
                continue;
            }

            block.Add(line);
        }

        if (block.Count > 0)
            await WriteEventAsync(downstream, RewriteSseEvent(block, table, stats), ct);
    }

    private static async Task WriteEventAsync(Stream downstream, IReadOnlyList<string> lines, CancellationToken ct)
    {
        var text = string.Join("\n", lines) + "\n\n";
        await downstream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await downstream.FlushAsync(ct);
    }

    /// <summary>Rewrites one SSE event block (its lines, without the terminating blank line).</summary>
    internal IReadOnlyList<string> RewriteSseEvent(IReadOnlyList<string> lines, ToolNameTable table, RequestStats stats)
    {
        string? eventName = null;
        var data = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
                data.Add(line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..]);
        }

        if (data.Count == 0)
            return lines;

        var payload = string.Join("\n", data);

        // Without an event: line the type lives in the data. Only parse what could carry an item.
        if (eventName is not null ? !RewrittenEvents.Contains(eventName) : !MayCarryOutputItem(payload))
            return lines;

        JsonObject? json;
        try
        {
            json = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            json = null;
        }

        var type = eventName ?? json?["type"]?.GetValue<string>();
        if (type is null || !RewrittenEvents.Contains(type))
            return lines;

        if (json is null)
        {
            _logger.LogWarning(
                "HostedProviderAdapter provider={Provider} SSE event {Event} did not parse; forwarded unchanged",
                _provider.Prefix, type);
            return lines;
        }

        var unmatched = type.StartsWith("response.output_item.", StringComparison.Ordinal)
            ? json["item"] is JsonObject item ? RewriteOutputItem(item, table) : 0
            : json["response"] is JsonObject response ? RewriteResponseObject(response, table) : 0;

        // Count each call once: at output_item.done. The same call also appears in .added and in
        // the final response object.
        if (type == "response.output_item.done")
            stats.UnmatchedCalls += unmatched;

        var rewritten = new List<string>(lines.Count);
        var dataWritten = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (!dataWritten)
                    rewritten.Add("data: " + json.ToJsonString());
                dataWritten = true;
                continue;
            }

            rewritten.Add(line);
        }

        return rewritten;
    }

    private static bool MayCarryOutputItem(string payload) =>
        payload.Contains("\"response.output_item.", StringComparison.Ordinal)
        || payload.Contains("\"response.completed\"", StringComparison.Ordinal)
        || payload.Contains("\"response.incomplete\"", StringComparison.Ordinal)
        || payload.Contains("\"response.failed\"", StringComparison.Ordinal);

    // ── Response rewrite ───────────────────────────────────────────────────

    /// <summary>
    /// Rewrites every <c>function_call</c> in a response object's <c>output[]</c>. Returns the number
    /// of calls whose name matched no tool in this request.
    /// </summary>
    internal static int RewriteResponseObject(JsonObject response, ToolNameTable table)
    {
        if (response["output"] is not JsonArray output)
            return 0;

        var unmatched = 0;
        foreach (var node in output)
        {
            if (node is JsonObject item)
                unmatched += RewriteOutputItem(item, table);
        }
        return unmatched;
    }

    /// <summary>
    /// Restores <c>namespace</c> + <c>name</c> on a <c>function_call</c> whose name EXACTLY matches a
    /// flattened name from this request. Any other name is left untouched, so Codex reports it as
    /// an unsupported call. Returns 1 for a call that matched no tool at all, else 0.
    /// </summary>
    internal static int RewriteOutputItem(JsonObject item, ToolNameTable table)
    {
        if (!IsType(item, "function_call") || item["name"] is not JsonValue nameValue
            || !nameValue.TryGetValue<string>(out var name))
        {
            return 0;
        }

        if (table.Flattened.TryGetValue(name, out var original))
        {
            item["namespace"] = original.Namespace;
            item["name"] = original.Member;
            return 0;
        }

        return table.Plain.Contains(name) ? 0 : 1;
    }

    // ── Request rewrite ────────────────────────────────────────────────────

    /// <summary>Codex's join rule (<c>core/src/tools/handlers/mcp.rs</c> <c>join_tool_name</c>).</summary>
    internal static string JoinToolName(string @namespace, string member) =>
        @namespace.TrimEnd('_') + NamespaceDelimiter + member.TrimStart('_');

    internal static RequestRewrite RewriteRequest(JsonObject body, HostedModelProvider provider)
        => RewriteRequest(body, provider, FieldPolicy.For(provider), new RequestStats());

    /// <summary>
    /// Applies the field, item and tool policy to <paramref name="body"/> in place. Returns the
    /// name table for the response, or an error to answer 400 with before any upstream call.
    /// </summary>
    internal static RequestRewrite RewriteRequest(
        JsonObject body, HostedModelProvider provider, FieldPolicy policy, RequestStats stats)
    {
        foreach (var key in body.Select(kv => kv.Key).ToList())
        {
            var value = body[key];

            // A null carries nothing (Codex sends "reasoning": null when no effort is set).
            if (value is null)
            {
                body.Remove(key);
                continue;
            }

            switch (key)
            {
                case "model" or "instructions" or "input" or "tools" or "stream" or "tool_choice" or "max_output_tokens":
                    break;

                case "parallel_tool_calls" when policy.PassParallelToolCalls:
                case "include" when policy.PassInclude:
                case "prompt_cache_key" when policy.PassPromptCacheKey:
                    break;

                case "store" when policy.PassStoreFalse:
                    if (value is JsonValue store && store.TryGetValue<bool>(out var storeValue) && storeValue)
                        return RequestRewrite.Fail(
                            $"phleet adapter: store:true is not supported by {provider.Prefix}");
                    break;

                case "reasoning":
                    KeepSubFields(body, key, ["effort", "summary"], stats);
                    break;

                case "text":
                    KeepSubFields(body, key, policy.PassTextVerbosity ? ["format", "verbosity"] : ["format"], stats);
                    break;

                default:
                    body.Remove(key);
                    stats.FieldsStripped.Add(key);
                    break;
            }
        }

        if (body["input"] is JsonArray input)
            RewriteInput(input, policy, stats);

        var table = new ToolNameTable();
        if (body["tools"] is JsonArray tools)
        {
            var error = RewriteTools(tools, provider, table, stats);
            if (error is not null)
                return RequestRewrite.Fail(error);
        }

        if (body["tool_choice"] is JsonObject choice)
            FlattenNamespacedName(choice);

        return new RequestRewrite(table, null);
    }

    private static void KeepSubFields(JsonObject body, string key, string[] keep, RequestStats stats)
    {
        if (body[key] is not JsonObject obj)
        {
            body.Remove(key);
            stats.FieldsStripped.Add(key);
            return;
        }

        foreach (var sub in obj.Select(kv => kv.Key).ToList())
        {
            if (obj[sub] is null)
            {
                obj.Remove(sub);
                continue;
            }

            if (!keep.Contains(sub, StringComparer.Ordinal))
            {
                obj.Remove(sub);
                stats.FieldsStripped.Add($"{key}.{sub}");
            }
        }

        if (obj.Count == 0)
            body.Remove(key);
    }

    private static void RewriteInput(JsonArray input, FieldPolicy policy, RequestStats stats)
    {
        for (var i = input.Count - 1; i >= 0; i--)
        {
            if (input[i] is not JsonObject item)
            {
                input.RemoveAt(i);
                stats.ItemsStripped++;
                continue;
            }

            switch (item["type"]?.GetValueKind() == JsonValueKind.String ? item["type"]!.GetValue<string>() : null)
            {
                case "message":
                    break;

                case "function_call":
                case "function_call_output":
                    FlattenNamespacedName(item);
                    break;

                case "reasoning":
                    if (policy.DropReasoningEncryptedContent)
                        item.Remove("encrypted_content");
                    break;

                default:
                    input.RemoveAt(i);
                    stats.ItemsStripped++;
                    break;
            }
        }
    }

    /// <summary>
    /// Folds <c>namespace</c> into <c>name</c> with the join rule, then drops <c>namespace</c>.
    /// </summary>
    private static void FlattenNamespacedName(JsonObject obj)
    {
        var ns = obj["namespace"] is JsonValue nsValue && nsValue.TryGetValue<string>(out var n) ? n : null;
        obj.Remove("namespace");

        if (!string.IsNullOrEmpty(ns) && obj["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name))
            obj["name"] = JoinToolName(ns, name);
    }

    private static string? RewriteTools(
        JsonArray tools, HostedModelProvider provider, ToolNameTable table, RequestStats stats)
    {
        var flat = new List<JsonNode>();
        var names = new List<string>();

        foreach (var node in tools)
        {
            if (node is not JsonObject tool)
            {
                stats.DroppedToolTypes.Add("non-object");
                continue;
            }

            var type = tool["type"]?.GetValueKind() == JsonValueKind.String ? tool["type"]!.GetValue<string>() : "untyped";
            switch (type)
            {
                case "function":
                {
                    var name = StringOrEmpty(tool["name"]);
                    table.Plain.Add(name);
                    names.Add(name);
                    flat.Add(tool.DeepClone());
                    break;
                }

                case "namespace":
                {
                    var ns = StringOrEmpty(tool["name"]);
                    if (tool["tools"] is not JsonArray members)
                        break;

                    foreach (var memberNode in members)
                    {
                        if (memberNode is not JsonObject member || !IsType(member, "function"))
                        {
                            stats.DroppedToolTypes.Add(
                                memberNode is JsonObject m ? $"namespace.{StringOrEmpty(m["type"])}" : "namespace.non-object");
                            continue;
                        }

                        var memberName = StringOrEmpty(member["name"]);
                        var flatName = JoinToolName(ns, memberName);
                        var clone = member.DeepClone().AsObject();
                        clone["name"] = flatName;
                        flat.Add(clone);
                        names.Add(flatName);
                        table.Flattened.TryAdd(flatName, (ns, memberName));
                        stats.ToolsFlattened++;
                    }
                    break;
                }

                default:
                    stats.DroppedToolTypes.Add(type);
                    break;
            }
        }

        var collisions = names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var invalid = names.Where(n => n.Length == 0 || n.Length > provider.ToolNameMaxLength || !ToolNamePattern.IsMatch(n))
            .Distinct(StringComparer.Ordinal).ToList();

        if (collisions.Count > 0 || invalid.Count > 0)
        {
            var parts = new List<string>();
            if (collisions.Count > 0)
                parts.Add($"name collision after flattening: {string.Join(", ", collisions)}");
            if (invalid.Count > 0)
                parts.Add($"names not matching ^[A-Za-z0-9_-]+$ or over {provider.ToolNameMaxLength} characters: {string.Join(", ", invalid)}");
            return $"phleet adapter: rejected tools for {provider.Prefix} — {string.Join("; ", parts)}";
        }

        tools.Clear();
        foreach (var tool in flat)
            tools.Add(tool);

        return null;
    }

    private static bool IsType(JsonObject obj, string type) =>
        obj["type"] is JsonValue value && value.TryGetValue<string>(out var t) && t == type;

    private static string StringOrEmpty(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";

    // ── Types ──────────────────────────────────────────────────────────────

    /// <summary>The per-vendor top-level field and item policy (the spec's two tables).</summary>
    internal sealed record FieldPolicy(
        bool PassParallelToolCalls,
        bool PassStoreFalse,
        bool PassInclude,
        bool PassPromptCacheKey,
        bool PassTextVerbosity,
        bool DropReasoningEncryptedContent)
    {
        public static FieldPolicy For(HostedModelProvider provider) => provider.Prefix switch
        {
            "deepseek" => new FieldPolicy(
                PassParallelToolCalls: false,
                PassStoreFalse: false,
                PassInclude: false,
                PassPromptCacheKey: false,
                PassTextVerbosity: false,
                DropReasoningEncryptedContent: true),
            "openrouter" => new FieldPolicy(
                PassParallelToolCalls: true,
                PassStoreFalse: true,
                PassInclude: true,
                PassPromptCacheKey: true,
                PassTextVerbosity: true,
                DropReasoningEncryptedContent: false),
            _ => throw new InvalidOperationException(
                $"Hosted provider '{provider.Prefix}' has no adapter field policy."),
        };
    }

    /// <summary>Flat name → (namespace, member), built from one request only.</summary>
    internal sealed class ToolNameTable
    {
        public Dictionary<string, (string Namespace, string Member)> Flattened { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Plain { get; } = new(StringComparer.Ordinal);
    }

    internal sealed record RequestRewrite(ToolNameTable Table, string? Error)
    {
        public static RequestRewrite Fail(string error) => new(new ToolNameTable(), error);
    }

    internal sealed class RequestStats
    {
        public int ToolsFlattened { get; set; }
        public List<string> FieldsStripped { get; } = [];
        public int ItemsStripped { get; set; }
        public List<string> DroppedToolTypes { get; } = [];
        public int UnmatchedCalls { get; set; }
    }
}
