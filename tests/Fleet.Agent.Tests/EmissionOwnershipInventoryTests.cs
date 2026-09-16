using System.Text.RegularExpressions;

namespace Fleet.Agent.Tests;

/// <summary>
/// #277 T1 — the ownership inventory is exhaustive, and stays that way.
///
/// #277 §1 enumerates every outbound emission in <c>Fleet.Agent</c> and assigns each exactly one
/// owner: either a Telegram sink send (suppressed for first-party conversations by the four
/// reserved-key guards) or a client conversation event. The value of that inventory is entirely in
/// being complete — revision 1 of the design missed ten <c>Sink.*</c> sites and an event
/// synthesizer, and an ownership table with holes in it is worse than none, because it reads as
/// proof that every effect is accounted for.
///
/// So this test counts the real call sites in the real source tree and compares them against a
/// pinned expectation. A NEW emission site anywhere in the assembly fails the build until someone
/// gives it an owner and updates the pin. That is the point: the assertion is deliberately
/// annoying to satisfy, because the alternative is an effect reaching a user with nobody having
/// decided which channel owns it.
///
/// It is a guard against drift, not a style check. Changing these numbers is fine — doing it
/// without adding the corresponding row to #277 §1 is not.
/// </summary>
public class EmissionOwnershipInventoryTests
{
    /// <summary>
    /// Per-file sink call-site counts, from #277 §1.1 as re-verified against this tree.
    ///
    /// <c>GroupBehavior</c> is deliberately absent: it holds a sink and calls nothing. If it ever
    /// gains a send, this test fails and the design table needs a row.
    /// </summary>
    private static readonly Dictionary<string, int> ExpectedSinkCallSites = new()
    {
        ["Services/TaskManager.cs"] = 26,
        ["Services/CommandDispatcher.cs"] = 7,
        ["Services/MessageRouter.cs"] = 3,
    };

    /// <summary>
    /// Files permitted to publish a client conversation event, from #277 §1.2.
    ///
    /// <c>ConversationEventBus</c> is on this list because it is itself a producer, not only a
    /// transport: an oversize terminal is dropped and REPLACED with
    /// <c>turn.outcome_unknown { terminal_event_oversize }</c> so the submission still terminates.
    /// That is the emission every table that enumerates only TaskManager and ConversationIntake
    /// misses.
    /// </summary>
    private static readonly HashSet<string> PublisherFiles = new(StringComparer.Ordinal)
    {
        "Services/TaskManager.cs",
        "Services/ConversationIntake.cs",
        "Services/ConversationEventBus.cs",
    };

    private static string AgentSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Fleet.Agent")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Fleet.Agent");
    }

    /// <summary>
    /// Comments are stripped before scanning. Doc comments legitimately name the very symbols
    /// these tests count — this file's own design notes reference <c>_lastSentMessageIds</c> by
    /// name — and matching them would make the guard fire on documentation, which trains people to
    /// edit the pin instead of reading the finding.
    /// </summary>
    private static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\n]*", "");
    }

    private static IEnumerable<(string Relative, string Text)> AgentSources()
    {
        var root = AgentSourceRoot();
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal)
                || relative.StartsWith("obj/", StringComparison.Ordinal))
                continue;
            yield return (relative, StripComments(File.ReadAllText(path)));
        }
    }

    /// <summary>
    /// Every sink call site in the assembly is in a file the design assigns an owner, and the
    /// per-file counts match. A new send in an unlisted file is the failure this exists to catch.
    /// </summary>
    [Fact]
    public void EverySinkCallSite_IsAccountedFor()
    {
        var sinkCall = new Regex(@"\b_sink\s*\.\s*Send\w+Async\s*\(", RegexOptions.Compiled);
        var actual = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (relative, text) in AgentSources())
        {
            var count = sinkCall.Matches(text).Count;
            if (count > 0) actual[relative] = count;
        }

        Assert.Equal(
            ExpectedSinkCallSites.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList(),
            actual.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The settable <c>Sink</c> property is gone and must stay gone (#277 D-1). As a property it
    /// was <c>null!</c> until AgentTransport assigned it, which is the ordering dependency this
    /// whole issue removes; re-adding one anywhere restores it.
    /// </summary>
    [Fact]
    public void NoSettableSinkProperty_Survives()
    {
        var settable = new Regex(@"IMessageSink\s+\w+\s*\{\s*get;\s*set;", RegexOptions.Compiled);

        var offenders = AgentSources()
            .Where(f => settable.IsMatch(f.Text))
            .Select(f => f.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Only the three files #277 §1.2 names may publish a client event. An event published from
    /// anywhere else — notably the relay branch of a completion handler — is an unowned emission
    /// reaching a client, which is the listed mutation for this test.
    /// </summary>
    [Fact]
    public void OnlyDesignatedFiles_PublishConversationEvents()
    {
        var publish = new Regex(@"\b_events\s*[?]?\s*\.\s*Publish\s*<?", RegexOptions.Compiled);

        var offenders = AgentSources()
            .Where(f => publish.IsMatch(f.Text) && !PublisherFiles.Contains(f.Relative))
            .Select(f => f.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// #277 MUST NOT 19/20: <c>_lastSentMessageIds</c> has exactly one reader outside the render
    /// path, and it is <c>GetLastSentMessageId</c>. A second reader, or a component that computes
    /// its own id, is duplicate ownership of a value only the transport can know.
    /// </summary>
    [Fact]
    public void LastSentMessageIds_LivesOnlyInTheTransport()
    {
        var offenders = AgentSources()
            .Where(f => f.Text.Contains("_lastSentMessageIds", StringComparison.Ordinal))
            .Select(f => f.Relative)
            .ToList();

        Assert.Equal(new[] { "Interfaces/AgentTransport.cs" }, offenders);
    }

    /// <summary>
    /// #277 MUST NOT 3 — the four reserved-key guards in the render path, neither moved nor
    /// multiplied. #274 Constraint 1 permits exactly those four lines in the transport and nothing
    /// more; the holder is a holder, not a router, and must never acquire a key check.
    ///
    /// GroupBehavior's single check is pinned here deliberately rather than excluded. It is a
    /// PERSISTENCE filter in SaveBuffers, not a render-path guard: client conversations are
    /// process-lifetime only, and BufferBotResponse calls SaveBuffers on every completion, so
    /// without it client text would reach disk through the completion path. #277 §11 records it as
    /// existing behaviour. Pinning it means a future edit to it is visible here instead of being
    /// absorbed into a number.
    /// </summary>
    [Fact]
    public void ReservedKeyGuards_StayInTheTransportAndStayAtFour()
    {
        var guard = new Regex(@"ConversationRegistry\.IsReservedKey\s*\(\s*chatId\s*\)",
            RegexOptions.Compiled);

        var byFile = AgentSources()
            .Select(f => (f.Relative, Count: guard.Matches(f.Text).Count))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

        Assert.Equal(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Interfaces/AgentTransport.cs"] = 4,   // render-path guards (#274 Constraint 1)
            ["Services/GroupBehavior.cs"] = 1,      // SaveBuffers persistence filter
        }, byFile);
    }
}
