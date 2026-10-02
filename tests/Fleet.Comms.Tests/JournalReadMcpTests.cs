using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fleet.Comms.Auth;
using Fleet.Comms.Configuration;
using Fleet.Comms.Routes;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Comms.Tests;

/// <summary>
/// The journal's read tools over MCP (#394 AC9): who gets in, what each reader may see, and that a
/// message out of scope is byte-for-byte a message that does not exist.
/// </summary>
/// <remarks>
/// Through <see cref="CommsApp.BuildJournalApp"/> — the composition <c>Program</c> runs — over an
/// in-memory read store that applies the same scope rule the MySQL store applies in SQL. The SQL
/// half of the rule is asserted against a real database in Fleet.Conversations.Tests'
/// <c>JournalReadStoreTests</c>; this suite needs no database, like the rest of this project.
/// </remarks>
public sealed class JournalReadMcpTests
{
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";
    private const string Reviewer = "reviewer";

    // ── authentication (AC9) ─────────────────────────────────────────────────

    [Fact]
    public async Task No_token_and_an_ingest_token_get_the_listeners_one_401_and_reach_no_tool()
    {
        await using var host = await McpHost.StartAsync();

        var unknownPath = await host.SendAsync(HttpMethod.Get, "/nonexistent", token: null);
        var expected = await unknownPath.Content.ReadAsByteArrayAsync();
        Assert.Equal("{\"error\":\"unauthorized\"}", Encoding.UTF8.GetString(expected));

        var refusals = new List<HttpResponseMessage>();
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Get, HttpMethod.Delete })
        {
            refusals.Add(await host.SendAsync(method, JournalMcp.Path, token: null));
            refusals.Add(await host.SendAsync(method, JournalMcp.Path, Token(JournalTokens.PurposeIngest, AgentA)));
            refusals.Add(await host.SendAsync(method, JournalMcp.Path, Token(JournalTokens.PurposeStatus, AgentA)));
            refusals.Add(await host.SendAsync(method, JournalMcp.Path, "not-a-token"));
        }

        // The read token does not open the ingest route either.
        refusals.Add(await host.SendAsync(HttpMethod.Post, JournalEndpoints.MessagesPath, Token(JournalTokens.PurposeRead, AgentA)));

        foreach (var response in refusals)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
        }

        Assert.Equal(0, host.Reads.Calls);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task An_authenticated_get_or_delete_is_405_never_401(string method)
    {
        await using var host = await McpHost.StartAsync();

        var response = await host.SendAsync(new HttpMethod(method), JournalMcp.Path, Token(JournalTokens.PurposeRead, AgentA));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("POST", response.Content.Headers.Allow.Single());
    }

    /// <summary>MUST NOT 8: the endpoint exists on the journal listener and on no other.</summary>
    [Fact]
    public async Task The_mcp_route_is_mapped_on_the_journal_listener_only()
    {
        await using var north = await NorthTestHost.StartAsync();
        Assert.DoesNotContain(RoutePatterns(north.Services), p => p.Contains("mcp", StringComparison.OrdinalIgnoreCase));

        var opsBuilder = WebApplication.CreateBuilder();
        opsBuilder.WebHost.UseTestServer();
        opsBuilder.Logging.ClearProviders();
        await using var ops = CommsApp.BuildOpsApp(opsBuilder, new InMemoryAuthStore());
        Assert.DoesNotContain(RoutePatterns(ops.Services), p => p.Contains("mcp", StringComparison.OrdinalIgnoreCase));

        var southBuilder = WebApplication.CreateBuilder();
        southBuilder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        southBuilder.WebHost.UseTestServer();
        southBuilder.Logging.ClearProviders();
        await using var south = CommsApp.BuildSouthApp(southBuilder, new FakeConversationStore(), new CommsOptions
        {
            SouthBearerToken = "south-token",
            AgentName = "example-agent",
            ConversationConnectionString = "configured",
        });
        await south.StartAsync();
        Assert.DoesNotContain(RoutePatterns(south.Services), p => p.Contains("mcp", StringComparison.OrdinalIgnoreCase));

        var request = new HttpRequestMessage(HttpMethod.Post, JournalMcp.Path) { Content = new StringContent("{}") };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer south-token");
        Assert.Equal(HttpStatusCode.NotFound, (await south.GetTestClient().SendAsync(request)).StatusCode);

        // The SDK maps its POST on a route group, so the pattern carries a trailing slash.
        await using var journal = await McpHost.StartAsync();
        Assert.Contains(RoutePatterns(journal.Services), p => p.TrimEnd('/') == JournalMcp.Path);
    }

    // ── the tool list ────────────────────────────────────────────────────────

    [Fact]
    public async Task Exactly_the_three_tools_are_listed_and_all_are_read_only()
    {
        await using var host = await McpHost.StartAsync();

        var response = await host.RpcAsync(Token(JournalTokens.PurposeRead, AgentA), "tools/list", new { });
        var tools = response.Result.GetProperty("tools").EnumerateArray().ToArray();

        Assert.Equal(
            ["get_conversation", "get_message", "search_messages"],
            tools.Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray());

        foreach (var tool in tools)
            Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    }

    // ── scope: another agent's message is a message that does not exist (AC9) ─

    [Fact]
    public async Task Another_agents_message_is_byte_identical_to_a_nonexistent_one_by_either_get_message_form()
    {
        await using var host = await McpHost.StartAsync();
        var world = World.Seed(host.Reads);
        var a = Token(JournalTokens.PurposeRead, AgentA);

        var hiddenById = await host.CallAsync(a, "get_message", new { message_id = world.B1 });
        var missingById = await host.CallAsync(a, "get_message", new { message_id = World.Missing });
        var hiddenByTelegram = await host.CallAsync(a, "get_message", new { telegram_chat_id = World.ChatB, telegram_message_id = 20 });
        var missingByTelegram = await host.CallAsync(a, "get_message", new { telegram_chat_id = World.ChatB, telegram_message_id = 99 });
        var missingChat = await host.CallAsync(a, "get_message", new { telegram_chat_id = 424242, telegram_message_id = 1 });

        Assert.True(hiddenById.IsError);
        Assert.Equal(JournalReadTools.NotFoundBody, hiddenById.Text);

        foreach (var other in new[] { missingById, hiddenByTelegram, missingByTelegram, missingChat })
        {
            Assert.Equal(HttpStatusCode.OK, other.Status);
            Assert.Equal(hiddenById.Body, other.Body);
        }

        // And the owner reads it, so the refusal above was scope and not a broken fixture.
        var own = await host.CallAsync(Token(JournalTokens.PurposeRead, AgentB), "get_message", new { message_id = world.B1 });
        Assert.False(own.IsError);
        Assert.Equal(world.B1, own.Json.GetProperty("message_id").GetString());
    }

    [Fact]
    public async Task Search_returns_none_of_another_agents_messages()
    {
        await using var host = await McpHost.StartAsync();
        var world = World.Seed(host.Reads);

        var all = await host.CallAsync(Token(JournalTokens.PurposeRead, AgentA), "search_messages", new { limit = 100 });
        var ids = all.Json.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("message_id").GetString()!).ToHashSet();

        Assert.Equal(world.VisibleToA.ToHashSet(), ids);
        Assert.DoesNotContain(world.B1, ids);
        Assert.DoesNotContain(world.G2, ids);

        // A full-text hit in a hidden row is not a hit.
        var bravo = await host.CallAsync(Token(JournalTokens.PurposeRead, AgentA), "search_messages", new { query = "bravo" });
        Assert.Empty(bravo.Json.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, bravo.Json.GetProperty("next_cursor").ValueKind);

        // No total, anywhere.
        Assert.Equal(["items", "next_cursor"], all.Json.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Another_agents_conversation_is_byte_identical_to_a_nonexistent_one()
    {
        await using var host = await McpHost.StartAsync();
        var world = World.Seed(host.Reads);
        var a = Token(JournalTokens.PurposeRead, AgentA);

        var hidden = await host.CallAsync(a, "get_conversation", new { conversation_id = world.ConversationB });
        var missing = await host.CallAsync(a, "get_conversation", new { conversation_id = World.Missing });

        Assert.True(hidden.IsError);
        Assert.Equal(JournalReadTools.NotFoundBody, hidden.Text);
        Assert.Equal(missing.Body, hidden.Body);
    }

    [Fact]
    public async Task A_reply_to_a_hidden_message_and_to_a_nonexistent_one_have_the_same_reply_to()
    {
        await using var host = await McpHost.StartAsync();
        var world = World.Seed(host.Reads);
        var a = Token(JournalTokens.PurposeRead, AgentA);

        // G3 replies to G2 (agent-b only); A3 replies to a platform id the journal never saw.
        var toHidden = (await host.CallAsync(a, "get_message", new { message_id = world.G3 })).Json.GetProperty("reply_to");
        var toMissing = (await host.CallAsync(a, "get_message", new { message_id = world.A3 })).Json.GetProperty("reply_to");

        foreach (var replyTo in new[] { toHidden, toMissing })
        {
            Assert.Equal(["telegram_message_id", "message_id"], replyTo.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(JsonValueKind.Null, replyTo.GetProperty("message_id").ValueKind);
        }

        Assert.Equal(31, toHidden.GetProperty("telegram_message_id").GetInt64());
        Assert.Equal(999, toMissing.GetProperty("telegram_message_id").GetInt64());

        // A visible target resolves, and a message that replies to nothing has no reply_to at all.
        var toVisible = (await host.CallAsync(a, "get_message", new { message_id = world.A2 })).Json.GetProperty("reply_to");
        Assert.Equal(world.A1, toVisible.GetProperty("message_id").GetString());
        Assert.Equal(JsonValueKind.Null,
            (await host.CallAsync(a, "get_message", new { message_id = world.A1 })).Json.GetProperty("reply_to").ValueKind);

        // The reader with scope `all` sees the same hidden target resolved.
        var reviewer = await host.CallAsync(Token(JournalTokens.PurposeRead, Reviewer), "get_message", new { message_id = world.G3 });
        Assert.Equal(world.G2, reviewer.Json.GetProperty("reply_to").GetProperty("message_id").GetString());
    }

    [Fact]
    public async Task A_hidden_missing_or_foreign_anchor_is_byte_identical_to_a_nonexistent_conversation()
    {
        await using var host = await McpHost.StartAsync();
        var world = World.Seed(host.Reads);
        var a = Token(JournalTokens.PurposeRead, AgentA);

        var nonexistentConversation = await host.CallAsync(a, "get_conversation", new { conversation_id = World.Missing });

        foreach (var anchor in new[] { world.G2, World.Missing, world.A1 })
        {
            var response = await host.CallAsync(a, "get_conversation", new { conversation_id = world.Group, from_message_id = anchor });
            Assert.Equal(nonexistentConversation.Body, response.Body);
        }

        // A visible anchor in this conversation starts the page after it.
        var fromG1 = await host.CallAsync(a, "get_conversation", new { conversation_id = world.Group, from_message_id = world.G1 });
        Assert.Equal([world.G3],
            fromG1.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("message_id").GetString()!).ToArray());
    }

    [Fact]
    public async Task Search_in_a_hidden_conversation_answers_like_search_in_a_nonexistent_one()
    {
        await using var host = await McpHost.StartAsync();
        var world = World.Seed(host.Reads);
        var a = Token(JournalTokens.PurposeRead, AgentA);

        var hidden = await host.CallAsync(a, "search_messages", new { conversation_id = world.ConversationB });
        var missing = await host.CallAsync(a, "search_messages", new { conversation_id = World.Missing });

        Assert.False(hidden.IsError);
        Assert.Equal("{\"items\":[],\"next_cursor\":null}", hidden.Text);
        Assert.Equal(missing.Body, hidden.Body);
    }

    [Fact]
    public async Task A_subject_granted_all_scope_sees_both_agents_messages()
    {
        await using var host = await McpHost.StartAsync();
        var world = World.Seed(host.Reads);
        var reviewer = Token(JournalTokens.PurposeRead, Reviewer);

        var search = await host.CallAsync(reviewer, "search_messages", new { limit = 100 });
        var ids = search.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("message_id").GetString()!).ToHashSet();

        Assert.Contains(world.A1, ids);
        Assert.Contains(world.B1, ids);
        Assert.Contains(world.G2, ids);
        Assert.Equal(new JournalReader(Reviewer, JournalReadScope.All), host.Reads.LastReader);

        var conversation = await host.CallAsync(reviewer, "get_conversation", new { conversation_id = world.ConversationB });
        Assert.False(conversation.IsError);

        // An agent that is not listed is observed-only, whatever its name looks like.
        await host.CallAsync(Token(JournalTokens.PurposeRead, "Reviewer"), "search_messages", new { });
        Assert.Equal(new JournalReader("Reviewer", JournalReadScope.Observed), host.Reads.LastReader);
    }

    [Fact]
    public async Task The_telegram_form_is_ambiguous_only_among_visible_matches()
    {
        await using var host = await McpHost.StartAsync();
        var world = World.Seed(host.Reads);

        var reviewer = await host.CallAsync(Token(JournalTokens.PurposeRead, Reviewer), "get_message",
            new { telegram_chat_id = World.SharedUserChat, telegram_message_id = 7 });

        Assert.True(reviewer.IsError);
        Assert.Equal("ambiguous", reviewer.Json.GetProperty("error").GetString());
        Assert.Equal([world.SharedA, world.SharedB],
            reviewer.Json.GetProperty("candidates").EnumerateArray().Select(c => c.GetString()!).Order(StringComparer.Ordinal).ToArray());

        // Agent A sees only its own bot's copy, so for it the lookup is not ambiguous at all.
        var a = await host.CallAsync(Token(JournalTokens.PurposeRead, AgentA), "get_message",
            new { telegram_chat_id = World.SharedUserChat, telegram_message_id = 7 });
        Assert.False(a.IsError);
        Assert.Equal(world.SharedA, a.Json.GetProperty("message_id").GetString());
    }

    // ── the media boundary (AC9, MUST NOT 6) ─────────────────────────────────

    [Fact]
    public async Task Attachments_carry_metadata_only()
    {
        await using var host = await McpHost.StartAsync();
        var world = World.Seed(host.Reads);

        var record = await host.CallAsync(Token(JournalTokens.PurposeRead, AgentA), "get_message", new { message_id = world.A3 });
        var attachments = record.Json.GetProperty("attachments").EnumerateArray().ToArray();

        Assert.Equal(2, attachments.Length);
        foreach (var attachment in attachments)
        {
            Assert.Equal(
                ["ordinal", "kind", "mime_type", "byte_size", "file_name", "state", "not_archived_reason"],
                attachment.EnumerateObject().Select(p => p.Name).ToArray());
        }

        foreach (var forbidden in new[] { "object_id", "objectId", "sha256", "file_unique_id", "fileUniqueId", "url", "bucket", "key" })
            Assert.DoesNotContain($"\"{forbidden}\"", record.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ── arguments and cursors ────────────────────────────────────────────────

    [Fact]
    public async Task Pages_follow_the_cursor_and_the_cursor_refuses_other_filters_tools_and_garbage()
    {
        await using var host = await McpHost.StartAsync();
        World.Seed(host.Reads);
        var reviewer = Token(JournalTokens.PurposeRead, Reviewer);

        var seen = new List<string?>();
        string? cursor = null;
        do
        {
            var page = await host.CallAsync(reviewer, "search_messages", new { limit = 2, cursor });
            seen.AddRange(page.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("message_id").GetString()));
            cursor = page.Json.GetProperty("next_cursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(host.Reads.Rows.Count, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());

        var first = await host.CallAsync(reviewer, "search_messages", new { limit = 2 });
        var next = first.Json.GetProperty("next_cursor").GetString();

        var mismatched = new object[]
        {
            new { limit = 2, cursor = next, direction = "inbound" },
            new { limit = 2, cursor = next, query = "hello" },
            new { limit = 2, cursor = "not-a-cursor" },
            new { limit = 2, cursor = next + "A" },
        };

        foreach (var arguments in mismatched)
        {
            var refused = await host.CallAsync(reviewer, "search_messages", arguments);
            Assert.True(refused.IsError);
            Assert.Equal(JournalReadTools.InvalidCursorBody, refused.Text);
        }

        // Another tool's cursor, and a replay cursor carried to the wrong conversation or direction.
        var otherTool = await host.CallAsync(reviewer, "get_conversation", new { conversation_id = World.Missing, cursor = next });
        Assert.Equal(JournalReadTools.InvalidCursorBody, otherTool.Text);

        var group = host.Reads.Rows.First(r => r.Message.Conversation.ChatKind == "supergroup").Message.Conversation.Id;
        var replay = await host.CallAsync(reviewer, "get_conversation", new { conversation_id = group, limit = 1 });
        var replayCursor = replay.Json.GetProperty("next_cursor").GetString();
        Assert.NotNull(replayCursor);

        foreach (var arguments in new object[]
                 {
                     new { conversation_id = World.Missing, limit = 1, cursor = replayCursor },
                     new { conversation_id = group, limit = 1, cursor = replayCursor, direction = "backward" },
                 })
        {
            Assert.Equal(JournalReadTools.InvalidCursorBody,
                (await host.CallAsync(reviewer, "get_conversation", arguments)).Text);
        }

        Assert.False((await host.CallAsync(reviewer, "get_conversation", new { conversation_id = group, limit = 1, cursor = replayCursor })).IsError);
    }

    [Theory]
    [InlineData("\"+-<>()~*@\"")]
    [InlineData("\"an on to\"")]
    [InlineData("\"   \"")]
    public async Task A_query_with_nothing_searchable_is_invalid_query(string query)
    {
        await using var host = await McpHost.StartAsync();

        var response = await host.CallRawAsync(Token(JournalTokens.PurposeRead, AgentA), "search_messages", $"{{\"query\":{query}}}");

        Assert.True(response.IsError);
        Assert.Equal(JournalReadTools.InvalidQueryBody, response.Text);
        Assert.Equal(0, host.Reads.Calls);
    }

    [Fact]
    public async Task A_query_over_256_characters_is_invalid_query()
    {
        await using var host = await McpHost.StartAsync();

        var response = await host.CallAsync(Token(JournalTokens.PurposeRead, AgentA), "search_messages",
            new { query = string.Join(' ', Enumerable.Repeat("alpha", 43)) });

        Assert.Equal(JournalReadTools.InvalidQueryBody, response.Text);
    }

    [Fact]
    public async Task Query_operators_are_stripped_and_every_term_is_required()
    {
        await using var host = await McpHost.StartAsync();
        World.Seed(host.Reads);

        await host.CallAsync(Token(JournalTokens.PurposeRead, AgentA), "search_messages", new { query = "+hello -(alpha)* \"x\" @on" });

        Assert.Equal(["hello", "alpha"], host.Reads.LastSearch!.Terms);
        Assert.Equal("+hello +alpha", JournalSearchTerms.BooleanExpression(host.Reads.LastSearch.Terms!));
    }

    public static TheoryData<string, string, string> InvalidArguments() => new()
    {
        { "search_messages", "{\"limit\":0}", "limit" },
        { "search_messages", "{\"limit\":101}", "limit" },
        { "search_messages", "{\"direction\":\"sideways\"}", "direction" },
        { "search_messages", "{\"sender_kind\":\"bot\"}", "sender_kind" },
        { "search_messages", "{\"since\":\"2026-01-01T00:00:00\"}", "since" },
        { "search_messages", "{\"until\":\"yesterday\"}", "until" },
        { "search_messages", "{\"conversation_id\":\"not-an-id\"}", "conversation_id" },
        { "get_message", "{}", "message_id" },
        { "get_message", "{\"message_id\":\"01J00000000000000000000000\",\"telegram_chat_id\":1,\"telegram_message_id\":2}", "message_id" },
        { "get_message", "{\"telegram_chat_id\":1}", "telegram_message_id" },
        { "get_message", "{\"message_id\":\"x\"}", "message_id" },
        { "get_conversation", "{\"conversation_id\":\"x\"}", "conversation_id" },
        { "get_conversation", "{\"conversation_id\":\"01J00000000000000000000000\",\"limit\":201}", "limit" },
        { "get_conversation", "{\"conversation_id\":\"01J00000000000000000000000\",\"direction\":\"up\"}", "direction" },
        { "get_conversation", "{\"conversation_id\":\"01J00000000000000000000000\",\"from_message_id\":\"x\"}", "from_message_id" },
    };

    /// <summary>Refused from the arguments alone: the store is never asked.</summary>
    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task Invalid_arguments_name_the_field_and_never_reach_the_store(string tool, string arguments, string field)
    {
        await using var host = await McpHost.StartAsync();

        var response = await host.CallRawAsync(Token(JournalTokens.PurposeRead, AgentA), tool, arguments);

        Assert.True(response.IsError);
        Assert.Equal($"{{\"error\":\"invalid_argument\",\"field\":\"{field}\"}}", response.Text);
        Assert.Equal(0, host.Reads.Calls);
    }

    [Fact]
    public async Task An_unavailable_store_is_a_retryable_store_unavailable_and_never_a_page()
    {
        await using var host = await McpHost.StartAsync();
        World.Seed(host.Reads);
        host.Reads.Unavailable = true;
        var a = Token(JournalTokens.PurposeRead, AgentA);

        foreach (var (tool, arguments) in new (string, object)[]
                 {
                     ("search_messages", new { }),
                     ("get_message", new { message_id = World.Missing }),
                     ("get_conversation", new { conversation_id = World.Missing }),
                 })
        {
            var response = await host.CallAsync(a, tool, arguments);
            Assert.True(response.IsError);
            Assert.Equal("{\"error\":\"store_unavailable\",\"retryable\":true}", response.Text);
        }
    }

    // ── status ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_reports_the_all_scope_subjects_and_read_counts()
    {
        await using var host = await McpHost.StartAsync(readAllSubjects: $"{Reviewer}, zz-auditor ,{Reviewer}");
        World.Seed(host.Reads);
        var a = Token(JournalTokens.PurposeRead, AgentA);

        await host.CallAsync(a, "search_messages", new { });
        await host.CallAsync(a, "get_message", new { message_id = World.Missing });
        await host.CallAsync(a, "get_message", new { message_id = World.Missing });

        var status = await host.SendAsync(HttpMethod.Get, JournalEndpoints.StatusPath, Token(JournalTokens.PurposeStatus, "orchestrator"));
        using var body = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        var read = body.RootElement.GetProperty("read");

        Assert.Equal([Reviewer, "zz-auditor"],
            read.GetProperty("allScopeSubjects").EnumerateArray().Select(s => s.GetString()!).ToArray());
        Assert.Equal(1, read.GetProperty("requestsSinceStart").GetProperty("search_messages").GetProperty("ok").GetInt64());
        Assert.Equal(2, read.GetProperty("requestsSinceStart").GetProperty("get_message").GetProperty("not_found").GetInt64());
    }

    // ── the grant key (Comms__Journal__ReadAllSubjects) ──────────────────────

    [Theory]
    [InlineData("", 0)]
    [InlineData(" , ", 0)]
    [InlineData("agent-a", 1)]
    [InlineData("agent-a, agent_b ,agent-a", 2)]
    public void A_valid_grant_list_parses_ignoring_blanks_and_collapsing_duplicates(string value, int count)
    {
        Assert.Equal(count, JournalOptions.ParseReadAllSubjects(value).Count);
    }

    [Theory]
    [InlineData("agent a")]
    [InlineData("agent-a,agent.b")]
    [InlineData("cj1.read.agent-a.mac")]
    public void An_invalid_subject_fails_validation_with_its_code(string value)
    {
        var options = new CommsOptions
        {
            ConversationConnectionString = "Server=unused;",
            Journal = new JournalOptions { Enabled = true, TokenKeys = JournalTestHost.KeyA, ReadAllSubjects = value },
        };

        var message = Assert.Throws<InvalidOperationException>(options.ValidateJournal).Message;
        Assert.StartsWith("journal_read_grants_invalid", message, StringComparison.Ordinal);
    }

    /// <summary>Through the real entry point: exit 1 with the fixed code.</summary>
    [Fact]
    public async Task An_invalid_grant_exits_1_with_journal_read_grants_invalid()
    {
        var directory = Path.Combine(Path.GetTempPath(), "journal-read-grants-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var info = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory,
            };
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Fleet.Comms.dll"));

            foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("Comms__", StringComparison.Ordinal)).ToList())
                info.Environment.Remove(key);

            info.Environment["DOTNET_CONTENTROOT"] = directory;
            info.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            info.Environment["Comms__AuthStorePath"] = Path.Combine(directory, "auth.db");
            info.Environment["Comms__ConversationConnectionString"] = "Server=127.0.0.1;Port=1;Database=unused;User ID=unused;Password=unused;";
            info.Environment["Comms__SouthBearerToken"] = "a-south-credential";
            info.Environment["Comms__AgentName"] = "example-agent";
            info.Environment["Comms__Journal__Enabled"] = "true";
            info.Environment["Comms__Journal__TokenKeys"] = JournalTestHost.KeyA;
            info.Environment["Comms__Journal__ReadAllSubjects"] = "example-agent,not a subject";

            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("the process did not exit");
            }

            Assert.Equal(1, process.ExitCode);
            Assert.Contains("journal_read_grants_invalid", await stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(JournalTestHost.KeyA, await stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("not a subject", await stderr, StringComparison.Ordinal);
            await stdout;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Token(string purpose, string subject) => JournalTestHost.Token(purpose, subject);

    private static IReadOnlyList<string> RoutePatterns(IServiceProvider services) =>
        services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? "")
            .ToArray();

    /// <summary>The seeded journal: two agents' DMs, a shared group, and one chat id two bots share.</summary>
    private sealed record World(
        string ConversationA, string ConversationB, string Group,
        string A1, string A2, string A3, string B1, string G1, string G2, string G3,
        string SharedA, string SharedB)
    {
        public const string Missing = "01J00000000000000000000000";
        public const long ChatA = 1001;
        public const long ChatB = 1002;
        public const long GroupChat = -1000000000500;
        public const long SharedUserChat = 5555;

        public IReadOnlyList<string> VisibleToA => [A1, A2, A3, G1, G3, SharedA];

        public static World Seed(FakeJournalReadStore store)
        {
            var t = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
            var conversationA = Fleet.Protocol.Ulid.NewUlid(t);
            var conversationB = Fleet.Protocol.Ulid.NewUlid(t.AddSeconds(1));
            var group = Fleet.Protocol.Ulid.NewUlid(t.AddSeconds(2));
            var sharedA = Fleet.Protocol.Ulid.NewUlid(t.AddSeconds(3));
            var sharedB = Fleet.Protocol.Ulid.NewUlid(t.AddSeconds(4));

            var a1 = store.Add(conversationA, "private", ChatA, 10, t.AddMinutes(1), "hello from alpha", [AgentA]);
            var a2 = store.Add(conversationA, "private", ChatA, 11, t.AddMinutes(2), "a reply", [AgentA], replyTo: 10);
            var a3 = store.Add(conversationA, "private", ChatA, 12, t.AddMinutes(3), "reply to the void", [AgentA], replyTo: 999, attachments:
            [
                new JournalReadAttachment { Ordinal = 1, Kind = "document", MimeType = "application/pdf", ByteSize = 2048, FileName = "notes.pdf", State = "committed" },
                new JournalReadAttachment { Ordinal = 0, Kind = "photo", MimeType = "image/jpeg", State = "not_archived", NotArchivedReason = "media_disabled" },
            ]);
            var b1 = store.Add(conversationB, "private", ChatB, 20, t.AddMinutes(4), "secret bravo", [AgentB]);
            var g1 = store.Add(group, "supergroup", GroupChat, 30, t.AddMinutes(5), "group hello", [AgentA, AgentB]);
            var g2 = store.Add(group, "supergroup", GroupChat, 31, t.AddMinutes(6), "bravo only", [AgentB]);
            var g3 = store.Add(group, "supergroup", GroupChat, 32, t.AddMinutes(7), "alpha answers", [AgentA], replyTo: 31);
            var shared1 = store.Add(sharedA, "private", SharedUserChat, 7, t.AddMinutes(8), "same id, bot a", [AgentA]);
            var shared2 = store.Add(sharedB, "private", SharedUserChat, 7, t.AddMinutes(9), "same id, bot b", [AgentB]);

            return new World(conversationA, conversationB, group, a1, a2, a3, b1, g1, g2, g3, shared1, shared2);
        }
    }

    /// <summary>The journal listener with the read tools, over a store double.</summary>
    private sealed class McpHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private McpHost(WebApplication app, FakeJournalReadStore reads)
        {
            _app = app;
            _client = app.GetTestClient();
            Reads = reads;
        }

        public FakeJournalReadStore Reads { get; }
        public IServiceProvider Services => _app.Services;

        public static async Task<McpHost> StartAsync(string readAllSubjects = Reviewer)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            var options = new CommsOptions
            {
                ConversationConnectionString = "Server=unused-by-this-test;",
                Journal = new JournalOptions
                {
                    Enabled = true,
                    TokenKeys = JournalTestHost.KeyA,
                    ReadAllSubjects = readAllSubjects,
                },
            };
            options.ValidateJournal();

            var reads = new FakeJournalReadStore();
            var app = CommsApp.BuildJournalApp(builder, new FakeJournalStore(), options, new JournalRuntimeStats(), reads: reads);
            await app.StartAsync();
            return new McpHost(app, reads);
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token)
        {
            var request = new HttpRequestMessage(method, path);
            if (method == HttpMethod.Post) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return _client.SendAsync(request);
        }

        public Task<ToolResponse> CallAsync(string token, string tool, object arguments) =>
            CallRawAsync(token, tool, JsonSerializer.Serialize(arguments, SkipNulls));

        public async Task<ToolResponse> CallRawAsync(string token, string tool, string argumentsJson)
        {
            var rpc = await RpcAsync(token, "tools/call",
                JsonDocument.Parse($"{{\"name\":\"{tool}\",\"arguments\":{argumentsJson}}}").RootElement);

            var result = rpc.Result;
            var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
            var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
            return new ToolResponse(rpc.Status, rpc.Body, text, isError);
        }

        public async Task<RpcResponse> RpcAsync(string token, string method, object parameters)
        {
            var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
            var request = new HttpRequestMessage(HttpMethod.Post, JournalMcp.Path)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");

            // Stateless streamable HTTP answers one JSON-RPC message as one SSE event.
            var data = body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
            using var document = JsonDocument.Parse(data);
            Assert.True(document.RootElement.TryGetProperty("result", out var result), data);
            return new RpcResponse(response.StatusCode, body, result.Clone());
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private static readonly JsonSerializerOptions SkipNulls = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    private sealed record RpcResponse(HttpStatusCode Status, string Body, JsonElement Result);

    private sealed record ToolResponse(HttpStatusCode Status, string Body, string Text, bool IsError)
    {
        public JsonElement Json => JsonDocument.Parse(Text).RootElement;
    }
}

/// <summary>
/// An in-memory read store with the MySQL store's scope rule: a message is visible to a reader
/// with scope <c>observed</c> only through an observer row naming it, and the same rule guards
/// page rows, reply targets, anchors and whether a conversation exists.
/// </summary>
internal sealed class FakeJournalReadStore : IJournalReadStore
{
    private int _calls;

    public sealed record Row(JournalReadMessage Message, long OrderKey, IReadOnlySet<string> Observers);

    public List<Row> Rows { get; } = [];
    public bool Unavailable { get; set; }
    public int Calls => Volatile.Read(ref _calls);
    public JournalReader? LastReader { get; private set; }
    public JournalSearchQuery? LastSearch { get; private set; }

    public string Add(
        string conversationId, string chatKind, long chatId, long telegramMessageId, DateTimeOffset sentAt,
        string text, string[] observers, long? replyTo = null, IReadOnlyList<JournalReadAttachment>? attachments = null)
    {
        var id = Fleet.Protocol.Ulid.NewUlid(sentAt);
        Rows.Add(new Row(new JournalReadMessage
        {
            MessageId = id,
            Conversation = new JournalReadConversation { Id = conversationId, ChatKind = chatKind, TelegramChatId = chatId },
            TelegramMessageId = telegramMessageId,
            ReplyToTelegramMessageId = replyTo,
            Direction = "inbound",
            Sender = new JournalReadSender { Kind = "human", Id = "u_1" },
            SentAt = sentAt,
            RecordedAt = sentAt,
            Text = text,
            TextFormat = "plain",
            Origin = "telegram_update",
            DeliveryState = "received",
            Attachments = attachments?.OrderBy(a => a.Ordinal).ToArray() ?? [],
        }, telegramMessageId, observers.ToHashSet(StringComparer.Ordinal)));
        return id;
    }

    public Task<JournalSearchPage> SearchAsync(JournalReader reader, JournalSearchQuery query, CancellationToken ct = default)
    {
        Enter(reader);
        LastSearch = query;

        var rows = Visible(reader)
            .Where(r => query.ConversationId is null || r.Message.Conversation.Id == query.ConversationId)
            .Where(r => query.Direction is null || r.Message.Direction == query.Direction)
            .Where(r => query.SenderKind is null || r.Message.Sender.Kind == query.SenderKind)
            .Where(r => query.SenderId is null || r.Message.Sender.Id == query.SenderId)
            .Where(r => query.Since is null || r.Message.SentAt >= query.Since)
            .Where(r => query.Until is null || r.Message.SentAt < query.Until)
            .Where(r => query.Terms is null || query.Terms.All(term =>
                (r.Message.Text ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                || (r.Message.Transcript ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(r => (Key: JournalCursor.SentAtKey(r.Message.SentAt), r.Message))
            .Where(r => query.After is not { } after
                        || r.Key < after.Key || (r.Key == after.Key && string.CompareOrdinal(r.Message.MessageId, after.Id) < 0))
            .OrderByDescending(r => r.Key).ThenByDescending(r => r.Message.MessageId, StringComparer.Ordinal)
            .Take(query.Limit + 1)
            .ToList();

        var items = rows.Take(query.Limit).Select(r => new JournalMessageSummary
        {
            MessageId = r.Message.MessageId,
            ConversationId = r.Message.Conversation.Id,
            TelegramChatId = r.Message.Conversation.TelegramChatId,
            TelegramMessageId = r.Message.TelegramMessageId,
            ChatKind = r.Message.Conversation.ChatKind,
            Direction = r.Message.Direction,
            Sender = r.Message.Sender,
            SentAt = r.Message.SentAt,
            Origin = r.Message.Origin,
            TextPreview = r.Message.Text,
            AttachmentCount = r.Message.Attachments.Count,
        }).ToList();

        return Task.FromResult(new JournalSearchPage
        {
            Items = items,
            Next = rows.Count > query.Limit ? new JournalKeyPosition(rows[query.Limit - 1].Key, items[^1].MessageId) : null,
        });
    }

    public Task<JournalReadMessage?> GetMessageAsync(JournalReader reader, string messageId, CancellationToken ct = default)
    {
        Enter(reader);
        var row = Visible(reader).SingleOrDefault(r => r.Message.MessageId == messageId);
        return Task.FromResult(row is null ? null : Resolve(reader, row));
    }

    public Task<JournalMessageLookup> FindByTelegramAsync(
        JournalReader reader, long telegramChatId, long telegramMessageId, CancellationToken ct = default)
    {
        Enter(reader);
        var matches = Visible(reader)
            .Where(r => r.Message.Conversation.TelegramChatId == telegramChatId && r.Message.TelegramMessageId == telegramMessageId)
            .OrderBy(r => r.Message.MessageId, StringComparer.Ordinal)
            .Take(JournalReadLimits.AmbiguousCandidates)
            .ToList();

        return Task.FromResult(matches.Count switch
        {
            0 => new JournalMessageLookup(),
            1 => new JournalMessageLookup { Message = Resolve(reader, matches[0]) },
            _ => new JournalMessageLookup { Candidates = matches.Select(m => m.Message.MessageId).ToArray() },
        });
    }

    public Task<JournalConversationPage?> ReadConversationAsync(
        JournalReader reader, JournalReplayQuery query, CancellationToken ct = default)
    {
        Enter(reader);
        var visible = Visible(reader).Where(r => r.Message.Conversation.Id == query.ConversationId).ToList();
        if (visible.Count == 0) return Task.FromResult<JournalConversationPage?>(null);

        var start = query.After;
        if (start is null && query.FromMessageId is not null)
        {
            var anchor = visible.SingleOrDefault(r => r.Message.MessageId == query.FromMessageId);
            if (anchor is null) return Task.FromResult<JournalConversationPage?>(null);
            start = new JournalKeyPosition(anchor.OrderKey, anchor.Message.MessageId);
        }

        var sign = query.Backward ? -1 : 1;
        var rows = visible
            .Where(r => start is not { } s
                        || sign * Compare(r.OrderKey, r.Message.MessageId, s.Key, s.Id) > 0)
            .OrderBy(r => r.OrderKey * sign).ThenBy(r => r.Message.MessageId, Comparer<string>.Create((x, y) => sign * string.CompareOrdinal(x, y)))
            .Take(query.Limit + 1)
            .ToList();

        var items = rows.Take(query.Limit).ToList();
        return Task.FromResult<JournalConversationPage?>(new JournalConversationPage
        {
            Conversation = visible[0].Message.Conversation,
            Items = items.Select(r => Resolve(reader, r)).ToList(),
            Next = rows.Count > query.Limit ? new JournalKeyPosition(items[^1].OrderKey, items[^1].Message.MessageId) : null,
        });
    }

    private static int Compare(long key, string id, long otherKey, string otherId) =>
        key != otherKey ? key.CompareTo(otherKey) : string.CompareOrdinal(id, otherId);

    private void Enter(JournalReader reader)
    {
        Interlocked.Increment(ref _calls);
        LastReader = reader;
        if (Unavailable) throw new JournalStoreUnavailableException(null);
    }

    private IEnumerable<Row> Visible(JournalReader reader) =>
        Rows.Where(r => reader.Scope == JournalReadScope.All || r.Observers.Contains(reader.Subject));

    private JournalReadMessage Resolve(JournalReader reader, Row row) => row.Message with
    {
        ReplyToMessageId = row.Message.ReplyToTelegramMessageId is { } target
            ? Visible(reader).FirstOrDefault(r => r.Message.Conversation.Id == row.Message.Conversation.Id
                                                  && r.Message.TelegramMessageId == target)?.Message.MessageId
            : null,
    };
}
