using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging;
using static Fleet.Agent.Tests.ProjectContextTestSupport;

namespace Fleet.Agent.Tests;

/// <summary>
/// #347 "Attachment: rendered at delivery, marked on acceptance" — the attacher and its ledger in
/// isolation. The delivery paths through <see cref="TaskManager"/> are covered by
/// <see cref="TaskManagerProjectContextTests"/>.
/// </summary>
public sealed class ProjectContextAttacherTests : IDisposable
{
    private readonly ProjectContextRoot _root = new ProjectContextRoot()
        .WithFull(ProjectA, "# project-a\nThe whole canonical context.\n")
        .WithFull(ProjectB, "# project-b\nNo trailing newline");
    private readonly LedgerTestExecutor _executor = new();
    private readonly ConcurrentCapturingLogger<ProjectContextAttacher> _logger = new();
    private readonly ProjectContextAttacher _attacher;

    public ProjectContextAttacherTests() =>
        _attacher = new ProjectContextAttacher(_executor, _logger) { ContentRoot = _root.Path };

    public void Dispose() => _root.Dispose();

    [Fact]
    public void NoRequests_RenderNothing_AndLeaveTheLedgerAlone()
    {
        _attacher.MarkAccepted(_attacher.Render([Request()]));
        _executor.Warm = false; // would clear the ledger if a render touched it

        Assert.Same(ProjectContextRender.Empty, _attacher.Render(null));
        Assert.Same(ProjectContextRender.Empty, _attacher.Render([]));
        Assert.Equal("text", ProjectContextRender.Empty.Apply("text"));
        Assert.Single(_attacher.LedgerSnapshot());
    }

    [Fact]
    public void Render_WrapsTheFullFileInANamedVersionedBlock_InFrontOfTheInput()
    {
        var render = _attacher.Render([Request(ProjectB, 4), Request(ProjectA, 3)]);

        Assert.Equal(
            "[project context: project-a · full v3 · attached for this turn]\n# project-a\nThe whole canonical context.\n[end project context: project-a]\n\n"
            + "[project context: project-b · full v4 · attached for this turn]\n# project-b\nNo trailing newline\n[end project context: project-b]\n\n",
            render.Prefix);
        Assert.Equal([new ProjectContextKey(ProjectA, 3), new ProjectContextKey(ProjectB, 4)], render.PendingKeys);
        Assert.Equal(render.Prefix + "hello", render.Apply("hello"));
    }

    [Fact]
    public void Render_DedupesByProjectAndVersion()
    {
        var render = _attacher.Render([Request(ProjectA, 3, "chat"), Request("PROJECT-A", 3, "repo")]);

        Assert.Equal(1, CountOccurrences(render.Prefix, "[project context: "));
        Assert.Single(render.PendingKeys);
    }

    [Fact]
    public void Render_NeverMarks_OnlyAcceptanceDoes()
    {
        _attacher.Render([Request()]);
        Assert.Empty(_attacher.LedgerSnapshot());

        var again = _attacher.Render([Request()]);
        Assert.Contains(BlockHeader(ProjectA), again.Prefix, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedWarm_SuppressesTheNextRender()
    {
        _attacher.MarkAccepted(_attacher.Render([Request()]));

        var next = _attacher.Render([Request()]);

        Assert.Equal("", next.Prefix);
        Assert.Empty(next.PendingKeys);
        Assert.Equal([new ProjectContextKey(ProjectA, 3)], next.Suppressed);
    }

    [Fact]
    public void ANewFullVersion_IsNotSuppressedByTheOldOne()
    {
        _attacher.MarkAccepted(_attacher.Render([Request(ProjectA, 3)]));

        Assert.Single(_attacher.Render([Request(ProjectA, 4)]).PendingKeys);
    }

    [Fact]
    public void ColdExecutor_ClearsTheLedgerAtRender()
    {
        _attacher.MarkAccepted(_attacher.Render([Request()]));
        _executor.Warm = false;

        Assert.Single(_attacher.Render([Request()]).PendingKeys);
    }

    [Fact]
    public void SessionChange_ClearsTheLedgerAtRender()
    {
        _attacher.MarkAccepted(_attacher.Render([Request()])); // binds to session-1
        _executor.SessionId = "session-2";

        Assert.Single(_attacher.Render([Request()]).PendingKeys);
    }

    [Fact]
    public void CompactionEpochChange_ClearsTheLedgerAtRender()
    {
        _attacher.MarkAccepted(_attacher.Render([Request()]));
        Interlocked.Increment(ref _executor.Epoch);

        Assert.Single(_attacher.Render([Request()]).PendingKeys);
        Assert.Contains(_logger.At(LogLevel.Information), m => m.Contains("reason=compaction", StringComparison.Ordinal));
    }

    [Fact]
    public void UnboundLedger_BindsAtTheFirstWarmRender_KeepingTheColdTurnsMark()
    {
        // The first turn of a fresh process is cold: the ledger is cleared, nothing is bound, and
        // the accepted attachment is marked. The second turn is warm: the ledger binds to the
        // session that turn produced and suppresses — two routed turns, one render.
        _executor.Warm = false;
        _executor.SessionId = null;
        _attacher.MarkAccepted(_attacher.Render([Request()]));

        _executor.Warm = true;
        _executor.SessionId = "session-9";
        Assert.Empty(_attacher.Render([Request()]).PendingKeys);

        // ...and a later session change is detected against the session it bound to.
        _executor.SessionId = "session-10";
        Assert.Single(_attacher.Render([Request()]).PendingKeys);
    }

    [Fact]
    public void AMarkDecidedAgainstAnOlderLedger_IsDropped_SoTheRaceDuplicatesRatherThanDrops()
    {
        var early = _attacher.Render([Request()]);
        // A compaction lands between this render and its acceptance, and another render resets the
        // ledger. Writing the early mark into the new ledger could suppress a context the model no
        // longer holds; dropping it can only cause a second copy.
        Interlocked.Increment(ref _executor.Epoch);
        _attacher.Render([Request(ProjectB, 4)]);

        _attacher.MarkAccepted(early);

        Assert.Empty(_attacher.LedgerSnapshot());
        Assert.Single(_attacher.Render([Request()]).PendingKeys);
    }

    [Fact]
    public void MissingFullFile_AttachesNothing_MarksNothing_AndWarns()
    {
        var render = _attacher.Render([Request("project-c", 2), Request(ProjectA, 3)]);
        _attacher.MarkAccepted(render);

        Assert.DoesNotContain("project-c", render.Prefix, StringComparison.Ordinal);
        Assert.Equal([new ProjectContextKey("project-c", 2)], render.Missing);
        Assert.Equal([new ProjectContextKey(ProjectA, 3)], _attacher.LedgerSnapshot());
        Assert.Contains(_logger.At(LogLevel.Warning), m => m.Contains("full_file_missing project=project-c", StringComparison.Ordinal));
    }

    [Fact]
    public void LogDelivery_NamesPathRenderedSuppressedAndAcceptance()
    {
        _attacher.MarkAccepted(_attacher.Render([Request(ProjectA)]));
        var render = _attacher.Render([Request(ProjectA), Request(ProjectB, 4)]);

        _attacher.LogDelivery(render, ProjectContextAttacher.PathQueue, accepted: true);

        Assert.Contains(_logger.At(LogLevel.Information), m =>
            m == "ProjectContextAttach path=queue rendered=project-b suppressed=project-a:already_attached accepted=true");
        Assert.Equal(0, _attacher.PromptAcceptedMissingCount);
    }

    [Fact]
    public void AnUnacceptedTurnWithPendingKeys_CountsPromptAcceptedMissing()
    {
        var render = _attacher.Render([Request()]);

        _attacher.LogDelivery(render, ProjectContextAttacher.PathTurn, accepted: false);

        Assert.Equal(1, _attacher.PromptAcceptedMissingCount);
        Assert.Contains(_logger.At(LogLevel.Warning), m => m.StartsWith("prompt_accepted_missing path=turn", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeliveryWithoutRequests_LogsNothing()
    {
        _attacher.LogDelivery(ProjectContextRender.Empty, ProjectContextAttacher.PathTurn, accepted: false);

        Assert.Empty(_logger.Entries);
        Assert.Equal(0, _attacher.PromptAcceptedMissingCount);
    }

    [Fact]
    public void QueuedPartsAreUnionedOnce_InProjectOrder()
    {
        var entry = new QueuedMessage(1, Part("one", [Request(ProjectB, 4)]));
        Assert.True(entry.TryAppendPart(Part("two", [Request(ProjectA), Request(ProjectB, 4)])));
        Assert.True(entry.TryAppendPart(Part("three", null)));

        var payload = entry.BuildPayload(DateTimeOffset.Now);

        Assert.Equal([ProjectA, ProjectB], payload.ContextRequests!.Select(r => r.Project));
        Assert.Null(new QueuedMessage(1, Part("solo", null)).BuildPayload(DateTimeOffset.Now).ContextRequests);

        static QueuedMessagePart Part(string text, IReadOnlyList<ContextAttachmentRequest>? requests) =>
            new(text, text, true, TaskSource.UserMessage, null, null, null, null, null, 0, DateTimeOffset.Now, "user",
                ContextRequests: requests);
    }
}
