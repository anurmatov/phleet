using System.Text.Json;
using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Endpoints;
using Fleet.Memory.Services;
using Fleet.Memory.Tools;
using Fleet.Shared;
using Fleet.Shared.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client.Grpc;

namespace Fleet.Memory.Tests;

/// <summary>
/// The two places a search is asked from: <c>GET /internal/memory/search</c> (the dashboard, through
/// the orchestrator) and the <c>memory_search</c> tool (the agents). Invented memories only.
/// </summary>
public sealed class MemorySearchSurfaceTests : IDisposable
{
    private readonly MemoryFixture _f = new(new MemoryIndexSettings { SectionChars = 200 });

    public void Dispose() => _f.Dispose();

    private static string Words(string word, int count) => string.Join(" ", Enumerable.Repeat(word, count));

    // ── GET /internal/memory/search ─────────────────────────────────────────

    private Task<IResult> Get(string? q, string? project = null, string? type = null, string? tag = null, string? since = null, string? limit = null) =>
        MemorySearchEndpoint.HandleAsync(q, project, type, tag, since, limit, _f.Service, CancellationToken.None);

    private static JsonElement Json(IResult result)
    {
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        // As ASP.NET writes it: the web defaults.
        return JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string ErrorOf(IResult result)
    {
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        return JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task The_answer_is_the_contract_field_for_field()
    {
        _f.Embedder.Same["postponement"] = "backoff";
        var keyword = await _f.AddAsync("Queue notes", $"{Words("filler", 40)} Use backoff between attempts.",
            id: "5e1f9a3c-0000-4000-8000-000000000001", tags: ["queues", "ops"], project: "Alpha", type: "decision",
            updated: new DateTimeOffset(2026, 3, 1, 12, 30, 0, TimeSpan.Zero));
        await _f.AddAsync("Scheduling", "Postponement grows.", project: "alpha");

        var json = Json(await Get("backoff", project: "alpha"));

        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(2, json.GetArrayLength());

        var first = json[0];
        Assert.Equal(
            ["id", "title", "project", "type", "tags", "snippet", "highlights", "match", "score", "updated_at"],
            first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(keyword.Id, first.GetProperty("id").GetString());
        Assert.Equal("Queue notes", first.GetProperty("title").GetString());
        Assert.Equal("Alpha", first.GetProperty("project").GetString());
        Assert.Equal("decision", first.GetProperty("type").GetString());
        Assert.Equal(["queues", "ops"], first.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("keyword", first.GetProperty("match").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("score").ValueKind);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 12, 30, 0, TimeSpan.Zero), first.GetProperty("updated_at").GetDateTimeOffset());

        // highlights: [start, length] pairs into snippet.
        var snippet = first.GetProperty("snippet").GetString()!;
        var pair = Assert.Single(first.GetProperty("highlights").EnumerateArray());
        Assert.Equal(2, pair.GetArrayLength());
        Assert.Equal("backoff", snippet.Substring(pair[0].GetInt32(), pair[1].GetInt32()));

        var second = json[1];
        Assert.Equal("meaning", second.GetProperty("match").GetString());
        Assert.Equal(JsonValueKind.Number, second.GetProperty("score").ValueKind);
        Assert.InRange(second.GetProperty("score").GetSingle(), 0.01f, 1f);
        Assert.Equal(0, second.GetProperty("highlights").GetArrayLength());
    }

    [Fact]
    public async Task A_query_that_is_an_id_answers_with_that_memory_first()
    {
        var wanted = await _f.AddAsync("Retry budget", "Three attempts.", id: "5e1f9a3c-0000-4000-8000-000000000001");
        await _f.AddAsync("Neighbour", "Three attempts too.");

        var json = Json(await Get("5e1f9a3c"));

        Assert.Equal(1, json.GetArrayLength());
        Assert.Equal(wanted.Id, json[0].GetProperty("id").GetString());
        Assert.Equal("id", json[0].GetProperty("match").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_query_is_a_400(string? q)
    {
        Assert.Equal("Missing query parameter 'q'", ErrorOf(await Get(q)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("51")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("5.5")]
    [InlineData("1e1")]
    public async Task A_limit_that_is_not_one_to_fifty_is_a_400(string limit)
    {
        Assert.Equal("invalid_limit", ErrorOf(await Get("backoff", limit: limit)));
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-13-01")]
    [InlineData("03/01/2026")]
    [InlineData("2026-3-1")]
    [InlineData("1770000000")]
    public async Task A_since_that_is_not_an_iso_date_is_a_400(string since)
    {
        Assert.Equal("invalid_since", ErrorOf(await Get("backoff", since: since)));
    }

    [Theory]
    [InlineData("2026-03-01", "2026-03-01T00:00:00+00:00")]
    [InlineData("2026-03-01T09:30", "2026-03-01T09:30:00+00:00")]
    [InlineData("2026-03-01T09:30:15Z", "2026-03-01T09:30:15+00:00")]
    [InlineData("2026-03-01T09:30:15.250Z", "2026-03-01T09:30:15.25+00:00")]
    [InlineData("2026-03-01T09:30:15+05:00", "2026-03-01T04:30:15+00:00")]
    public void An_iso_date_or_date_and_time_is_read_as_utc(string since, string expected)
    {
        Assert.True(SearchDates.TryParseSince(since, out var parsed));
        Assert.Equal(DateTimeOffset.Parse(expected), parsed);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Fact]
    public async Task The_limit_is_twenty_unless_given_and_fifty_at_most()
    {
        for (var i = 0; i < 55; i++) await _f.AddAsync($"Backoff note {i:00}", "x");

        Assert.Equal(20, Json(await Get("backoff")).GetArrayLength());
        Assert.Equal(20, Json(await Get("backoff", limit: " ")).GetArrayLength());
        Assert.Equal(1, Json(await Get("backoff", limit: "1")).GetArrayLength());
        Assert.Equal(50, Json(await Get("backoff", limit: "50")).GetArrayLength());
    }

    [Fact]
    public async Task Project_type_tag_and_since_narrow_the_answer_and_an_empty_one_is_no_filter()
    {
        var day = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        await _f.AddAsync("Backoff a", "x", project: "alpha", type: "decision", tags: ["keep"], updated: day.AddDays(1));
        await _f.AddAsync("Backoff b", "x", project: "beta", type: "learning", tags: ["other"], updated: day.AddDays(-1));

        string[] Titles(JsonElement json) => json.EnumerateArray().Select(e => e.GetProperty("title").GetString()!).Order().ToArray();

        Assert.Equal(["Backoff a", "Backoff b"], Titles(Json(await Get("backoff", project: "", type: "", tag: "", since: ""))));
        Assert.Equal(["Backoff a"], Titles(Json(await Get("backoff", project: "alpha"))));
        Assert.Equal(["Backoff b"], Titles(Json(await Get("backoff", type: "learning"))));
        Assert.Equal(["Backoff a"], Titles(Json(await Get("backoff", tag: "keep"))));
        Assert.Equal(["Backoff a"], Titles(Json(await Get("backoff", since: "2026-03-01"))));
    }

    [Fact]
    public async Task The_route_as_mapped_binds_its_parameters_and_writes_the_contract_over_http()
    {
        await _f.AddAsync("Queue notes", "Use backoff between attempts.", id: "5e1f9a3c-0000-4000-8000-000000000001",
            tags: ["queues"], project: "alpha", type: "decision", updated: new DateTimeOffset(2026, 3, 1, 12, 30, 0, TimeSpan.Zero));
        await _f.AddAsync("Other project", "Use backoff between attempts.", project: "beta");

        // Not the service's own appsettings.json (it is copied beside the tests and names a fixed
        // port): an empty content root, no configuration, and a free loopback port.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _f.Dir });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_f.Service);
        await using var app = builder.Build();
        // The handler Program.cs maps the route with.
        Delegate handler = MemorySearchEndpoint.HandleAsync;
        app.MapGet("/internal/memory/search", handler);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var body = await http.GetStringAsync("/internal/memory/search?q=backoff&project=alpha&type=decision&tag=queues&since=2026-03-01&limit=5");
        // Byte for byte what the dashboard is sent (single quotes here stand for double ones).
        Assert.Equal(
            ("[{'id':'5e1f9a3c-0000-4000-8000-000000000001','title':'Queue notes','project':'alpha','type':'decision','tags':['queues']," +
             "'snippet':'Use backoff between attempts.','highlights':[[4,7]],'match':'keyword','score':null,'updated_at':'2026-03-01T12:30:00+00:00'}]")
            .Replace('\'', '"'),
            body);

        // Only q is required.
        Assert.Equal(2, JsonDocument.Parse(await http.GetStringAsync("/internal/memory/search?q=backoff")).RootElement.GetArrayLength());

        foreach (var (query, error) in new[]
                 {
                     ("", "Missing query parameter 'q'"),
                     ("?q=backoff&limit=0", "invalid_limit"),
                     ("?q=backoff&limit=51", "invalid_limit"),
                     ("?q=backoff&since=yesterday", "invalid_since"),
                 })
        {
            var response = await http.GetAsync("/internal/memory/search" + query);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(error, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
        }

        await app.StopAsync();
    }

    // ── memory_search ───────────────────────────────────────────────────────

    private (MemorySearchTool Tool, AclDeniedCounterService Denied, CapturingLogger<MemorySearchTool> Log) Tool(
        Dictionary<string, List<string>>? acl = null, string agent = "agent-a")
    {
        var cache = new AclCacheService(
            Options.Create(new AclOptions { EnableProjectScopedAcl = acl is not null }),
            Options.Create(new OrchestratorOptions { BaseUrl = "http://unused" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AclCacheService>.Instance);
        cache.InjectAclForTesting(acl ?? []);

        // The caller as the identity middleware records it: the URL is not who calls.
        var http = new DefaultHttpContext();
        AgentCallerHttp.Set(http, new AgentCaller(agent, AgentCallerKind.Agent));
        var denied = new AclDeniedCounterService();
        var log = new CapturingLogger<MemorySearchTool>();
        return (new MemorySearchTool(_f.Service, cache, new HttpContextAccessor { HttpContext = http }, denied, log), denied, log);
    }

    [Fact]
    public async Task Each_result_says_how_it_matched_and_shows_the_snippet_not_the_first_500_characters()
    {
        _f.Embedder.Same["postponement"] = "backoff";
        var opening = Words("opening", 80);   // 639 characters before the passage that matches
        var keyword = await _f.AddAsync("Queue notes", $"{opening} Use backoff between attempts.", project: "alpha", tags: ["queues"]);
        await _f.AddAsync("Backoff rules", "Short.");
        await _f.AddAsync("Scheduling", "Postponement grows.");

        var (tool, _, _) = Tool();
        var output = await tool.SearchAsync("backoff");

        Assert.StartsWith("Found 3 memories:", output);
        var blocks = output.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("## Backoff rules", blocks[0]);
        Assert.Contains("- **Match**: title", blocks[0]);

        Assert.Contains("## Queue notes", blocks[1]);
        Assert.Contains($"- **ID**: {keyword.Id}", blocks[1]);
        Assert.Contains("- **Project**: alpha", blocks[1]);
        Assert.Contains("- **Tags**: queues", blocks[1]);
        Assert.Contains("- **Match**: keyword", blocks[1]);
        Assert.Contains("Use backoff between attempts.", blocks[1]);
        Assert.DoesNotContain(opening[..500], blocks[1]);

        Assert.Contains("## Scheduling", blocks[2]);
        Assert.Matches(@"- \*\*Match\*\*: meaning \(similarity \d\.\d\d; no word of the query matched\)", blocks[2]);
    }

    [Fact]
    public async Task The_tool_finds_a_memory_by_the_start_of_its_id()
    {
        var wanted = await _f.AddAsync("Retry budget", "Three attempts.", id: "5e1f9a3c-0000-4000-8000-000000000001");
        await _f.AddAsync("Neighbour", "Three attempts too.");

        var (tool, _, _) = Tool();
        var output = await tool.SearchAsync("5e1f9a");

        Assert.StartsWith("Found 1 memories:", output);
        Assert.Contains($"- **ID**: {wanted.Id}", output);
        Assert.Contains("- **Match**: id", output);
    }

    [Fact]
    public async Task The_tool_takes_tag_and_since_and_refuses_a_since_it_cannot_read()
    {
        var day = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        await _f.AddAsync("Backoff a", "x", tags: ["keep"], updated: day.AddDays(1));
        await _f.AddAsync("Backoff b", "x", tags: ["other"], updated: day.AddDays(1));
        await _f.AddAsync("Backoff c", "x", tags: ["keep"], updated: day.AddDays(-1));

        var (tool, _, _) = Tool();

        var byTag = await tool.SearchAsync("backoff", tag: "keep");
        Assert.Contains("## Backoff a", byTag);
        Assert.Contains("## Backoff c", byTag);
        Assert.DoesNotContain("## Backoff b", byTag);

        var bySince = await tool.SearchAsync("backoff", tag: "keep", since: "2026-03-01");
        Assert.Contains("## Backoff a", bySince);
        Assert.DoesNotContain("## Backoff c", bySince);

        Assert.Equal(
            "memory_search: invalid value for 'since' filter: 'last week'.\nHint: pass an ISO 8601 date such as 2026-03-01.",
            await tool.SearchAsync("backoff", since: "last week"));
    }

    [Fact]
    public async Task The_access_rule_filters_every_kind_of_hit_and_a_denial_names_nothing()
    {
        _f.Embedder.Same["postponement"] = "backoff";
        // One memory of each kind in the project the agent may read, and one in a project it may not.
        await _f.AddAsync("Backoff allowed", "x", project: "alpha");
        await _f.AddAsync("Tag allowed", "x", project: "alpha", tags: ["backoff"]);
        await _f.AddAsync("Keyword allowed", "Use backoff.", project: "alpha");
        await _f.AddAsync("Meaning allowed", "Postponement grows.", project: "alpha");
        await _f.AddAsync("Backoff secret", "x", project: "hidden-project");
        await _f.AddAsync("Tag secret", "x", project: "hidden-project", tags: ["backoff"]);
        await _f.AddAsync("Keyword secret", "Use backoff.", project: "hidden-project");
        await _f.AddAsync("Meaning secret", "Postponement grows.");   // no project: denied too

        var (tool, denied, log) = Tool(new() { ["agent-a"] = ["alpha"] });
        var output = await tool.SearchAsync("backoff", limit: 10);

        Assert.StartsWith("Found 4 memories:", output);
        foreach (var title in new[] { "Backoff allowed", "Tag allowed", "Keyword allowed", "Meaning allowed" })
            Assert.Contains($"## {title}", output);
        Assert.DoesNotContain("secret", output);
        Assert.DoesNotContain("hidden-project", output);

        Assert.Equal(4, Assert.Single(denied.GetSnapshot()).Count);
        var denials = log.Entries.Where(e => e.Message.Contains("denied")).Select(e => e.Message).ToList();
        Assert.Equal(
            ["memory_search: denied (no_project)", "memory_search: denied (project_not_allowed)", "memory_search: denied (project_not_allowed)", "memory_search: denied (project_not_allowed)"],
            denials.Order());
    }

    [Fact]
    public async Task An_id_the_agent_may_not_read_is_not_found_by_it()
    {
        await _f.AddAsync("Secret", "Hidden text.", id: "5e1f9a3c-0000-4000-8000-000000000001", project: "hidden-project");

        var (tool, denied, _) = Tool(new() { ["agent-a"] = ["alpha"] });
        var output = await tool.SearchAsync("5e1f9a3c");

        Assert.Equal("No memories found matching the query.", output);
        Assert.Equal(1, Assert.Single(denied.GetSnapshot()).Count);
    }

    [Fact]
    public async Task One_denied_memory_cannot_spend_the_candidate_pool_with_its_sections()
    {
        await _f.AddAsync("Hidden notes", string.Join("\n\n", Enumerable.Repeat(Words("needle", 27), 150)), project: "denied");
        var allowed = await _f.AddAsync("Visible notes", "needle with other words", project: "allowed");
        Assert.Equal(150, _f.Index.Points.Count(p => Equals(p.Payload[IndexPayload.Project], "denied")));

        var (tool, _, _) = Tool(new() { ["agent-a"] = ["allowed"] });
        var output = await tool.SearchAsync("needle", limit: 1);

        Assert.StartsWith("Found 1 memories:", output);
        Assert.Contains(allowed.Id, output);
        Assert.DoesNotContain("Hidden notes", output);
        Assert.Equal(2, (await _f.Service.SearchAsync("needle", limit: 2)).Count);
    }

    // ── Settings ────────────────────────────────────────────────────────────

    private static (MemoryIndexSettings Settings, List<string> Warnings) Settings(params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();
        var log = new CapturingLogger<MemoryIndexSettings>();
        var settings = MemoryIndexSettings.FromConfiguration(configuration, log);
        return (settings, log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList());
    }

    [Fact]
    public void Unset_settings_are_the_defaults_with_no_cut_off()
    {
        var (settings, warnings) = Settings(("Search:SectionChars", ""), ("Search:MinMeaningScore", " "));

        Assert.Equal((1200, 200, 0f), (settings.SectionChars, settings.MaxSections, settings.MinMeaningScore));
        Assert.Contains(Path.GetFullPath("./models/all-MiniLM-L6-v2.onnx", AppContext.BaseDirectory), settings.EmbedderId);
        Assert.Contains(Path.GetFullPath("./models/all-MiniLM-L6-v2-vocab.txt", AppContext.BaseDirectory), settings.EmbedderId);
        Assert.EndsWith("/384", settings.EmbedderId);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Settings_are_read_and_the_model_is_named_from_the_embedding_settings()
    {
        var (settings, warnings) = Settings(
            ("Search:SectionChars", "800"), ("Search:MaxSections", "50"), ("Search:MinMeaningScore", "0.35"),
            ("Embedding:Provider", "Ollama"), ("Embedding:Ollama:Model", "some-model"), ("Embedding:Dimensions", "1024"));

        Assert.Equal((800, 50, 0.35f), (settings.SectionChars, settings.MaxSections, settings.MinMeaningScore));
        Assert.Equal("ollama/some-model/1024", settings.EmbedderId);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("Search:SectionChars", "abc")]
    [InlineData("Search:SectionChars", "50")]
    [InlineData("Search:SectionChars", "100000")]
    [InlineData("Search:MaxSections", "0")]
    [InlineData("Search:MaxSections", "-3")]
    [InlineData("Search:MinMeaningScore", "1.5")]
    [InlineData("Search:MinMeaningScore", "high")]
    public void A_value_that_cannot_be_used_is_the_default_and_one_warning_never_a_failed_start(string key, string value)
    {
        var (settings, warnings) = Settings((key, value));

        Assert.Equal((1200, 200, 0f), (settings.SectionChars, settings.MaxSections, settings.MinMeaningScore));
        var warning = Assert.Single(warnings);
        Assert.StartsWith(key + " must be ", warning);
    }

    [Fact]
    public async Task The_container_gives_the_service_the_registered_catalogue_settings_and_index()
    {
        // The registrations Program.cs makes for these, with stand-ins for Qdrant and the model.
        var catalogue = new MemoryCatalogue();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new StorageOptions { Path = _f.Dir }));
        services.AddSingleton(Options.Create(new QdrantOptions { Url = "http://localhost:1", SimilarityThreshold = 0 }));
        services.AddSingleton<MemoryFileStore>();
        services.AddSingleton<IVectorIndex>(_f.Index);
        services.AddSingleton<IEmbeddingService>(_f.Embedder);
        services.AddSingleton(new MemorySizeGuidance(0, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        services.AddSingleton(catalogue);
        services.AddSingleton(new MemoryIndexSettings { SectionChars = 200, MaxSections = 2 });
        services.AddSingleton<MemoryService>();
        await using var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<MemoryService>();

        Assert.Same(catalogue, service.Catalogue);
        // The registered settings, not the defaults: a cap of two sections.
        var doc = await _f.WriteAsync("Long", string.Join("\n\n", Enumerable.Range(0, 5).Select(i => $"part{i} " + Words("ledger", 16))));
        await service.IndexFileAsync(doc.FilePath);
        Assert.Equal(2, _f.Index.PointsOf(doc.FilePath).Count);
    }

    // ── What is sent to Qdrant ──────────────────────────────────────────────

    [Fact]
    public void The_first_section_has_the_id_the_memorys_one_point_had_and_the_others_their_own()
    {
        // The id a memory's single point had before sections: the first 16 bytes of SHA-256 of the path.
        var legacy = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("/data/memories/learning/a.md")).AsSpan(0, 16));

        Assert.Equal(legacy, VectorStore.PointIdFor("/data/memories/learning/a.md", 0));
        var ids = Enumerable.Range(0, 200).Select(i => VectorStore.PointIdFor("/data/memories/learning/a.md", i)).ToList();
        Assert.Equal(200, ids.Distinct().Count());
    }

    [Fact]
    public void A_point_carries_both_vectors_and_the_payload_with_numbers_as_numbers()
    {
        var point = VectorStore.BuildPoint(new IndexPoint("/m/a.md", 2, [0.1f, 0.2f], "Очередь задач queue",
            new Dictionary<string, object>
            {
                [IndexPayload.FilePath] = "/m/a.md",
                [IndexPayload.Tags] = new List<string> { "x", "y" },
                [IndexPayload.UpdatedTs] = 1772366400L,
                [IndexPayload.SectionIndex] = 2,
                [IndexPayload.IndexHash] = "abc",
            }));

        Assert.Equal(VectorStore.PointIdFor("/m/a.md", 2).ToString(), point.Id.Uuid);
        Assert.Equal([0.1f, 0.2f], point.Vectors.Vectors_.Vectors["dense"].Data);
        Assert.Equal(3, point.Vectors.Vectors_.Vectors["sparse"].Indices.Data.Count);
        Assert.Equal("/m/a.md", point.Payload[IndexPayload.FilePath].StringValue);
        Assert.Equal(["x", "y"], point.Payload[IndexPayload.Tags].ListValue.Values.Select(v => v.StringValue));
        Assert.Equal(Value.KindOneofCase.IntegerValue, point.Payload[IndexPayload.UpdatedTs].KindCase);
        Assert.Equal(1772366400L, point.Payload[IndexPayload.UpdatedTs].IntegerValue);
        Assert.Equal(2L, point.Payload[IndexPayload.SectionIndex].IntegerValue);
    }

    [Fact]
    public void A_search_filter_is_the_keywords_and_a_lower_bound_on_the_updated_time()
    {
        Assert.Null(VectorStore.BuildFilter(null));
        Assert.Null(VectorStore.BuildFilter(new IndexFilter(new Dictionary<string, string>())));

        var filter = VectorStore.BuildFilter(new IndexFilter(
            new Dictionary<string, string> { [IndexPayload.Project] = "alpha", [IndexPayload.Tags] = "keep" }, UpdatedSince: 1772323200))!;

        Assert.Equal(3, filter.Must.Count);
        Assert.Empty(filter.MustNot);
        Assert.Contains(filter.Must, c => c.Field.Key == "project" && c.Field.Match.Keyword == "alpha");
        Assert.Contains(filter.Must, c => c.Field.Key == "tags" && c.Field.Match.Keyword == "keep");
        var since = Assert.Single(filter.Must, c => c.Field.Key == "updated_ts").Field.Range;
        Assert.Equal(1772323200d, since.Gte);
        Assert.False(since.HasLte || since.HasLt || since.HasGt);
    }

    [Fact]
    public void A_listing_asks_for_one_point_per_memory_and_a_removal_for_all_points_of_the_file()
    {
        var list = VectorStore.BuildListFilter(new Dictionary<string, string> { ["memory_type"] = "decision" });
        Assert.Equal("decision", Assert.Single(list.Must).Field.Match.Keyword);
        var notLater = Assert.Single(list.MustNot).Field;
        Assert.Equal(("section_index", 1d), (notLater.Key, notLater.Range.Gte));
        Assert.Empty(VectorStore.BuildListFilter(null).Must);

        var all = VectorStore.BuildFileFilter("/m/a.md");
        Assert.Equal(("file_path", "/m/a.md"), (Assert.Single(all.Must).Field.Key, all.Must[0].Field.Match.Keyword));
        Assert.Empty(all.MustNot);

        var stale = VectorStore.BuildFileFilter("/m/a.md", exceptHash: "abc");
        Assert.Equal("/m/a.md", Assert.Single(stale.Must).Field.Match.Keyword);
        Assert.Equal(("index_hash", "abc"), (Assert.Single(stale.MustNot).Field.Key, stale.MustNot[0].Field.Match.Keyword));
    }
}
