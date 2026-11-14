using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Fleet.Memory.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Memory.Tests;

/// <summary>
/// #518: synthetic records for the scoped <c>memory_stats</c> checks. project-a holds 2 active (learning, decision) and 1
/// archived; project-b 3 active (learning ×2, reference) and 2 archived; one active learning record has no project.
/// </summary>
internal static class StatsRecords
{
    internal static async Task<MemoryFileStore> SeedAsync(string root)
    {
        var store = new MemoryFileStore(Options.Create(new StorageOptions { Path = root }), NullLogger<MemoryFileStore>.Instance);
        store.EnsureDirectories();
        await SaveAsync(store, 1, "learning", "project-a");
        await SaveAsync(store, 2, "decision", "project-a");
        store.Delete((await SaveAsync(store, 3, "learning", "project-a")).FilePath, permanent: false);
        await SaveAsync(store, 4, "learning", "project-b");
        await SaveAsync(store, 5, "learning", "project-b");
        await SaveAsync(store, 6, "reference", "project-b");
        store.Delete((await SaveAsync(store, 7, "decision", "project-b")).FilePath, permanent: false);
        store.Delete((await SaveAsync(store, 8, "learning", "project-b")).FilePath, permanent: false);
        await SaveAsync(store, 9, "learning", "");
        return store;
    }

    internal static Task<MemoryDocument> SaveAsync(MemoryFileStore store, int n, string type, string project) =>
        store.SaveAsync(new MemoryDocument
        {
            Id = $"00000000-0000-0000-0000-{n:D12}", Title = $"synthetic record {n}", Content = "synthetic content",
            Project = project, Type = type, Agent = "agent1",
        });

    /// <summary>The exact tool output: rows by descending count, ties in the fixed type order, then both headings.</summary>
    internal static string Expected(int total, int archived, params (string Type, int Count)[] nonZero)
    {
        var rows = nonZero.Concat(MemoryDocument.ValidTypes.Where(t => nonZero.All(r => r.Type != t)).Select(t => (Type: t, Count: 0)));
        var n = Environment.NewLine;
        return "## Memory Statistics" + n + n + string.Concat(rows.Select(r => $"- **{r.Type}**: {r.Count}{n}"))
            + n + $"**Total active**: {total}{n}**Archived**: {archived}{n}";
    }

    /// <summary>project-a only: learning 1, decision 1, archived 1.</summary>
    internal static readonly string ProjectA = Expected(2, 1, ("learning", 1), ("decision", 1));

    internal static readonly string Nothing = Expected(0, 0);

    /// <summary>Identifiers and totals that must never appear in a scoped project-a or empty result.</summary>
    internal static readonly string[] Hidden = ["project-a", "project-b", "00000000-0000", "synthetic record", "agent1", "skipped", "denied", "corrupt"];
}

public sealed class MemoryStatsAclTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "memory-stats-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_dir, "memories");
    private MemoryFileStore _store = null!;
    private readonly IEmbeddingService _embedding = Substitute.For<IEmbeddingService>();
    private readonly AclResponses _responses = new();

    public async Task InitializeAsync()
    {
        _store = await StatsRecords.SeedAsync(Root);
        // The index and embedder are unreachable or throwing: stats must never depend on them.
        _embedding.EmbedAsync(default!, default).ReturnsForAnyArgs<Task<float[]>>(_ => throw new HttpRequestException("synthetic embed outage"));
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_dir, true);
        return Task.CompletedTask;
    }

    /// <summary>The orchestrator ACL endpoint as the cache sees it: the current map, or a failed fetch.</summary>
    private sealed class AclResponses : HttpMessageHandler
    {
        public Dictionary<string, string[]> Acl = new() { ["agent1"] = ["project-a"] };
        public bool Fail;
        public int Fetches;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult(Fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { acl = Acl }) });
        }
    }

    /// <summary>An ACL cache; <paramref name="populate"/> stands in for its first successful fetch.</summary>
    private AclCacheService Acl(bool enabled = true, Dictionary<string, List<string>>? populate = null, bool allowNoProject = false)
    {
        var acl = new AclCacheService(Options.Create(new AclOptions { EnableProjectScopedAcl = enabled, AclAllowNoProject = allowNoProject }),
            Options.Create(new OrchestratorOptions { BaseUrl = "https://acl.example.test" }), NullLogger<AclCacheService>.Instance,
            new HttpClient(_responses));
        if (populate is not null) acl.InjectAclForTesting(populate);
        return acl;
    }

    private MemoryStatsTool Tool(AclCacheService acl, string? agent = "agent1")
    {
        var qdrant = Options.Create(new QdrantOptions { Url = "http://127.0.0.1:1", SimilarityThreshold = 0 });
        var memory = new MemoryService(_store, new VectorStore(qdrant, Options.Create(new EmbeddingOptions()), NullLogger<VectorStore>.Instance),
            _embedding, qdrant, new MemorySizeGuidance(0, NullLogger.Instance), NullLogger<MemoryService>.Instance);
        var http = new DefaultHttpContext();
        if (agent is not null) http.Request.QueryString = new QueryString("?agent=" + agent);
        return new MemoryStatsTool(memory, acl, new HttpContextAccessor { HttpContext = http }, NullLogger<MemoryStatsTool>.Instance);
    }

    private static void NoHiddenValues(string output)
    {
        foreach (var hidden in StatsRecords.Hidden) Assert.DoesNotContain(hidden, output, StringComparison.OrdinalIgnoreCase);
    }

    // ── Scoped counts and privacy ───────────────────────────────────────────

    [LinuxFact]
    public async Task MemoryStats_counts_only_readable_parsed_records_by_directory_type()
    {
        var output = await Tool(Acl(populate: new() { ["agent1"] = ["project-a"] })).GetStatsAsync(default);

        Assert.Equal(StatsRecords.ProjectA, output);
        NoHiddenValues(output);
        await _embedding.DidNotReceiveWithAnyArgs().EmbedAsync(default!, default);
    }

    [LinuxFact]
    public async Task MemoryStats_hidden_project_additions_and_deletions_leave_visible_totals_unchanged()
    {
        var tool = Tool(Acl(populate: new() { ["agent1"] = ["project-a"] }));
        Assert.Equal(StatsRecords.ProjectA, await tool.GetStatsAsync(default));

        await StatsRecords.SaveAsync(_store, 10, "codebase_knowledge", "project-b");
        _store.Delete((await StatsRecords.SaveAsync(_store, 11, "task_result", "project-b")).FilePath, permanent: false);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "learning"), "*synthetic-record-4.md")) File.Delete(file);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "_archived"), "*synthetic-record-7.md")) File.Delete(file);
        await StatsRecords.SaveAsync(_store, 12, "learning", "");

        Assert.Equal(StatsRecords.ProjectA, await tool.GetStatsAsync(default));
    }

    [LinuxFact]
    public async Task MemoryStats_no_visible_records_report_zero_rows_and_both_headings()
    {
        var output = await Tool(Acl(populate: new() { ["agent2"] = ["project-c"] }), "agent2").GetStatsAsync(default);

        Assert.Equal(StatsRecords.Nothing, output);
        NoHiddenValues(output);
    }

    // The existing CanRead decides every record: no new no-project, public or wildcard interpretation.
    [LinuxFact]
    public async Task MemoryStats_uses_the_existing_acl_decision_for_no_project_and_wildcard_rows()
    {
        Assert.Equal(StatsRecords.Expected(3, 1, ("learning", 2), ("decision", 1)),
            await Tool(Acl(populate: new() { ["agent1"] = ["project-a"] }, allowNoProject: true)).GetStatsAsync(default));
        Assert.Equal(StatsRecords.Expected(6, 3, ("learning", 4), ("decision", 1), ("reference", 1)),
            await Tool(Acl(populate: new() { ["agent1"] = ["*"] })).GetStatsAsync(default));
    }

    // ── Ordinary cache: unavailable, refresh and stale-on-failure ─────────────

    [Fact]
    public async Task MemoryStats_unavailable_ordinary_cache_is_the_exact_error_without_totals()
    {
        Assert.Equal("memory_stats: ACL cache unavailable", await Tool(Acl()).GetStatsAsync(default));
    }

    [Fact]
    public async Task MemoryStats_missing_caller_identity_returns_no_totals()
    {
        var output = await Tool(Acl(populate: new() { ["agent1"] = ["project-a"] }), agent: null).GetStatsAsync(default);

        Assert.StartsWith("memory_stats: agent identity required", output);
        Assert.DoesNotContain("Total", output);
    }

    // Ordinary removals apply after the existing successful refresh, not on the next request; a later failed refresh keeps
    // the populated (stale) scoped cache. Refreshes are driven directly instead of waiting for the five-minute timer.
    [LinuxFact]
    public async Task MemoryStats_ordinary_removal_applies_after_a_successful_refresh_and_a_failed_refresh_keeps_the_cache()
    {
        var acl = Acl(populate: new() { ["agent1"] = ["project-a"] });
        var tool = Tool(acl);
        Assert.Equal(StatsRecords.ProjectA, await tool.GetStatsAsync(default));

        _responses.Acl = new() { ["agent1"] = [] };
        Assert.Equal(StatsRecords.ProjectA, await tool.GetStatsAsync(default)); // still the cached map until a refresh
        await acl.RefreshAsync();
        Assert.Equal(StatsRecords.Nothing, await tool.GetStatsAsync(default));

        _responses.Acl = new() { ["agent1"] = ["project-a"] };
        await acl.RefreshAsync();
        Assert.Equal(StatsRecords.ProjectA, await tool.GetStatsAsync(default));

        _responses.Fail = true;
        var fetches = _responses.Fetches;
        await acl.RefreshAsync(); // fails every attempt: the populated cache is retained, never cleared or widened
        Assert.True(_responses.Fetches > fetches);
        Assert.Equal(StatsRecords.ProjectA, await tool.GetStatsAsync(default));
    }

    [Fact]
    public async Task MemoryStats_acl_disabled_keeps_the_legacy_global_file_counts()
    {
        File.WriteAllText(Path.Combine(Root, "learning", "2026-01-01_ffffffff_plain.md"), "no frontmatter\n");

        var output = await Tool(Acl(enabled: false), agent: null).GetStatsAsync(default);

        // Every .md file, parsed or not and in any project, exactly as before.
        Assert.Equal(StatsRecords.Expected(7, 3, ("learning", 5), ("decision", 1), ("reference", 1)), output);
    }

    // ── Files: malformed, absent, linked, special and failures ───────────────

    [LinuxFact]
    public async Task MemoryStats_malformed_and_unframed_files_and_subdirectories_are_left_out_unnamed()
    {
        File.WriteAllText(Path.Combine(Root, "learning", "2026-01-01_aaaaaaaa_plain.md"), "plain text, no frontmatter\n");
        File.WriteAllText(Path.Combine(Root, "decision", "2026-01-01_bbbbbbbb_broken.md"), "---\nproject: [project-a\n---\nbody\n");
        File.WriteAllText(Path.Combine(Root, "learning", "2026-01-01_cccccccc_empty-frontmatter.md"), "---\n---\nbody\n");
        File.WriteAllText(Path.Combine(Root, "_archived", "2026-01-01_dddddddd_unterminated.md"), "---\nproject: project-a\n");
        Directory.CreateDirectory(Path.Combine(Root, "reference", "folder.md"));

        var output = await Tool(Acl(populate: new() { ["agent1"] = ["project-a"] })).GetStatsAsync(default);

        Assert.Equal(StatsRecords.ProjectA, output);
        foreach (var name in new[] { "aaaaaaaa", "bbbbbbbb", "cccccccc", "dddddddd", "plain", "broken", "folder" })
            Assert.DoesNotContain(name, output);
    }

    [LinuxFact]
    public async Task MemoryStats_only_an_observed_absent_type_or_archive_directory_counts_zero()
    {
        Directory.Delete(Path.Combine(Root, "decision"), true);
        Directory.Delete(Path.Combine(Root, "_archived"), true);

        Assert.Equal(StatsRecords.Expected(1, 0, ("learning", 1)),
            await Tool(Acl(populate: new() { ["agent1"] = ["project-a"] })).GetStatsAsync(default));
    }

    [Theory]
    [InlineData("linked_file")]
    [InlineData("linked_directory")]
    [InlineData("linked_archive")]
    [InlineData("type_path_is_a_file")]
    [InlineData("type_path_is_a_fifo")]
    [InlineData("storage_root_missing")]
    public async Task MemoryStats_linked_or_non_directory_entries_are_unavailable_without_partial_totals(string fault)
    {
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        switch (fault)
        {
            case "linked_file":
                var target = Directory.EnumerateFiles(Path.Combine(Root, "learning"), "*synthetic-record-1.md").Single();
                File.Copy(target, Path.Combine(outside, "copy.md"));
                File.CreateSymbolicLink(Path.Combine(Root, "learning", "2026-01-01_eeeeeeee_link.md"), Path.Combine(outside, "copy.md"));
                break;
            case "linked_directory":
                Directory.Delete(Path.Combine(Root, "reference"), true);
                Directory.CreateSymbolicLink(Path.Combine(Root, "reference"), outside);
                break;
            case "linked_archive":
                Directory.Move(Path.Combine(Root, "_archived"), Path.Combine(outside, "_archived"));
                Directory.CreateSymbolicLink(Path.Combine(Root, "_archived"), Path.Combine(outside, "_archived"));
                break;
            case "type_path_is_a_fifo":
                Directory.Delete(Path.Combine(Root, "reference"), true);
                Assert.Equal(0, MkFifo(Path.Combine(Root, "reference"), 0x180));
                break;
            case "storage_root_missing":
                Directory.Move(Root, Path.Combine(_dir, "moved-root"));
                break;
            default:
                Directory.Delete(Path.Combine(Root, "reference"), true);
                File.WriteAllText(Path.Combine(Root, "reference"), "not a directory");
                break;
        }

        var output = await Task.Run(() => Tool(Acl(populate: new() { ["agent1"] = ["project-a"] })).GetStatsAsync(default))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("memory_stats: unavailable", output);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    // A FIFO is refused from its no-follow type before any open (opening one would block); bounded so a regression fails.
    [Fact]
    public async Task MemoryStats_special_entry_is_refused_before_it_is_opened()
    {
        Assert.Equal(0, MkFifo(Path.Combine(Root, "learning", "2026-01-01_ffffffff_fifo.md"), 0x180));

        var output = await Task.Run(() => Tool(Acl(populate: new() { ["agent1"] = ["project-a"] })).GetStatsAsync(default))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("memory_stats: unavailable", output);
    }

    [Fact]
    public async Task MemoryStats_cancellation_aborts_without_output()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Tool(Acl(populate: new() { ["agent1"] = ["project-a"] })).GetStatsAsync(cancelled.Token));
    }

    // ── Replacement after a check: the scan stays on its no-follow handles ────

    /// <summary>Two project-a records outside the storage root that a replaced path could point at.</summary>
    private string OutsideRecords()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;
        foreach (var n in new[] { "eeeeeeee", "ffffffff" })
            File.WriteAllText(Path.Combine(outside, $"2026-01-01_{n}_outside.md"),
                $"---\nid: {n}-0000-0000-0000-000000000000\nagent: agent1\ntitle: \"outside\"\nproject: project-a\n---\n\nbody\n");
        return outside;
    }

    private Task<Dictionary<string, int>> ScanAsync(Action<string> afterOpen) =>
        _store.GetScopedStatsAsync(project => project == "project-a", default, afterOpen);

    // A type or archive directory replaced after its handle is open (by a link to outside records, or by a new
    // directory) never redirects the scan: it lists and reads the directory it opened, and nothing outside counts.
    [LinuxTheory]
    [InlineData("learning", "link")]
    [InlineData("learning", "directory")]
    [InlineData("_archived", "link")]
    public async Task MemoryStats_a_directory_replaced_after_it_is_opened_never_redirects_the_scan(string replaced, string with)
    {
        var outside = OutsideRecords();
        var path = Path.Combine(Root, replaced);
        var swapped = false;

        var stats = await ScanAsync(relative =>
        {
            if (relative != replaced) return;
            Directory.Move(path, Path.Combine(_dir, "moved-" + replaced));
            if (with == "link") Directory.CreateSymbolicLink(path, outside);
            else
            {
                Directory.CreateDirectory(path);
                foreach (var file in Directory.EnumerateFiles(outside)) File.Copy(file, Path.Combine(path, Path.GetFileName(file)));
            }
            swapped = true;
        });

        // Redirected, learning or the archive would read the two outside project-a records instead of its one.
        Assert.True(swapped);
        Assert.Equal((1, 1, 1), (stats["learning"], stats["decision"], stats["_archived"]));
        Assert.Equal(0, stats.Where(s => s.Key is not ("learning" or "decision" or "_archived")).Sum(s => s.Value));
    }

    // An entry replaced after it was listed and before it is opened is refused without following or blocking: a link to
    // an outside record, a FIFO, a socket or a vanished entry each fail the whole scan, never count.
    [Theory]
    [InlineData("link")]
    [InlineData("fifo")]
    [InlineData("socket")]
    [InlineData("vanished")]
    public async Task MemoryStats_an_entry_replaced_after_listing_is_refused_without_following_or_blocking(string with)
    {
        var outside = OutsideRecords();
        var target = Directory.EnumerateFiles(Path.Combine(Root, "learning"), "*synthetic-record-1.md").Single();
        var relative = "learning/" + Path.GetFileName(target);
        System.Net.Sockets.Socket? socket = null;
        try
        {
            var error = await Record.ExceptionAsync(() => Task.Run(() => ScanAsync(step =>
            {
                if (step != relative) return;
                File.Delete(target);
                switch (with)
                {
                    case "link": File.CreateSymbolicLink(target, Directory.EnumerateFiles(outside).First()); break;
                    case "fifo": Assert.Equal(0, MkFifo(target, 0x180)); break;
                    case "socket":
                        // Bound at a short path (the 108-byte socket limit), then renamed into place: still a socket.
                        var shortPath = Path.Combine(Path.GetTempPath(), "s" + Guid.NewGuid().ToString("N")[..8]);
                        socket = new(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                        socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(shortPath));
                        File.Move(shortPath, target);
                        break;
                }
            })).WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }
        finally { socket?.Dispose(); }
    }

    // The refusal carries a fixed message: no record, path or project for the tool or a log to repeat.
    [Fact]
    public async Task MemoryStats_a_refused_replacement_names_no_path_or_record()
    {
        var outside = OutsideRecords();
        var target = Directory.EnumerateFiles(Path.Combine(Root, "learning"), "*synthetic-record-1.md").Single();
        var error = await Record.ExceptionAsync(() => _store.GetScopedStatsAsync(_ => true, default, step =>
        {
            if (step != "learning/" + Path.GetFileName(target)) return;
            File.Delete(target);
            File.CreateSymbolicLink(target, Directory.EnumerateFiles(outside).First());
        }));
        Assert.IsType<IOException>(error);
        Assert.DoesNotContain("eeeeeeee", error!.Message);
        Assert.DoesNotContain(Root, error.Message);
    }
}
