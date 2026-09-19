using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// Assembly of every assigned instruction into the system prompt (#309).
/// </summary>
/// <remarks>
/// <para>
/// The defect: <c>BuildSystemPrompt</c> read two fixed paths, <c>roles/_base</c> and
/// <c>roles/{Role}</c>, while the orchestrator wrote a directory for every assigned instruction.
/// Anything else an operator assigned was written to disk and never read — no error, no warning, no
/// log line, and the config API still reported it as assigned. Several such assignments carry
/// safety rules, so the failure was silent in the worst direction: adding a rule appeared to work.
/// </para>
/// <para>
/// These write real files into a temp tree and read the assembled string back. A test that stubbed
/// the file reads would be asserting against its own stub, and the two fixed <c>Path.Combine</c>
/// calls were the whole of the bug.
/// </para>
/// </remarks>
public sealed class PromptBuilderInstructionAssemblyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fleet-prompt-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void WriteInstruction(string directory, string content)
    {
        var dir = Path.Combine(_root, "roles", directory);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "system.md"), content);
    }

    private PromptBuilder Builder(string role, params string[] instructionOrder) =>
        new(
            Options.Create(new AgentOptions
            {
                Name = "test-agent",
                Role = role,
                WorkDir = Path.GetTempPath(),
                InstructionOrder = [.. instructionOrder],
            }),
            NullLogger<PromptBuilder>.Instance)
        {
            ContentRoot = _root,
        };

    // ── the reported defect ──────────────────────────────────────────────────

    /// <summary>
    /// A non-role assigned instruction reaches the assembled prompt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the acceptance criterion, and it is written the way the issue's own evidence was
    /// gathered: a distinctive line from the extra instruction, plus a <b>control</b> line from the
    /// role instruction. Without the control, a probe that never matched anything would look
    /// identical to an instruction that was dropped.
    /// </para>
    /// <para>
    /// <b>Mutation checked.</b> Reverting <c>BuildSystemPrompt</c> to the two fixed paths
    /// (<c>roles/_base</c> and <c>roles/{Role}</c>) turns this test red on the extra-instruction
    /// assertion while the control still passes — which is exactly the shape of the production
    /// observation in the issue.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnExtraAssignedInstruction_ReachesTheAssembledPrompt()
    {
        WriteInstruction("_base", "# Base\nbase-distinctive-line");
        WriteInstruction("co-cto", "# Role\nrole-distinctive-line");
        WriteInstruction("review-security", "# Review Security\nnever-approve-an-unread-diff");

        var prompt = Builder("co-cto", "_base", "co-cto", "review-security").BuildSystemPrompt();

        // Control — rules out a probe that could never have matched.
        Assert.Contains("role-distinctive-line", prompt, StringComparison.Ordinal);

        // The heading and a body line, because the issue probed both and got 0 for each.
        Assert.Contains("# Review Security", prompt, StringComparison.Ordinal);
        Assert.Contains("never-approve-an-unread-diff", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// An instruction whose file is missing is reported, not passed over in silence.
    /// </summary>
    /// <remarks>
    /// The orchestrator writes a file for every name it puts in the list, so a miss means
    /// generation failed for that one. Under the old code this produced nothing at all; the whole
    /// complaint in #309 is that a missing instruction leaves no trace.
    /// </remarks>
    [Fact]
    public void AMissingInstructionFile_IsLogged()
    {
        WriteInstruction("_base", "base");

        var logger = new CollectingLogger<PromptBuilder>();
        var builder = new PromptBuilder(
            Options.Create(new AgentOptions
            {
                Name = "test-agent",
                Role = "co-cto",
                WorkDir = Path.GetTempPath(),
                InstructionOrder = ["_base", "absent-instruction"],
            }),
            logger)
        {
            ContentRoot = _root,
        };

        builder.BuildSystemPrompt();

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("absent-instruction", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ordinary assembly logs nothing.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Regression guard, and it caught a real defect.</b> The first implementation hoisted
    /// <c>_base</c> to the front and then met its own entry again in the loop, reporting it as a
    /// duplicate — so every agent logged a spurious "appears more than once" on every prompt build.
    /// A warning that fires constantly is a warning nobody reads, which is exactly how the genuine
    /// duplicate this code is meant to surface would have gone unnoticed.
    /// </remarks>
    [Fact]
    public void AnOrdinaryAssembly_LogsNoWarnings()
    {
        WriteInstruction("_base", "base");
        WriteInstruction("co-cto", "role");
        WriteInstruction("etiquette", "extra");

        var logger = new CollectingLogger<PromptBuilder>();
        var builder = new PromptBuilder(
            Options.Create(new AgentOptions
            {
                Name = "test-agent",
                Role = "co-cto",
                WorkDir = Path.GetTempPath(),
                InstructionOrder = ["_base", "co-cto", "etiquette"],
            }),
            logger)
        {
            ContentRoot = _root,
        };

        builder.BuildSystemPrompt();

        Assert.Empty(logger.Warnings);
    }

    // ── ordering ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Load order is honoured, with at least three instructions beyond the base.
    /// </summary>
    /// <remarks>
    /// The orchestrator supplies the list already sorted, so what is asserted here is that the
    /// agent inlines it in the order given rather than in directory or alphabetical order — the
    /// fixture names are deliberately NOT in alphabetical order, so an implementation that sorted
    /// them itself would fail.
    /// </remarks>
    [Fact]
    public void Instructions_AreInlinedInTheSuppliedOrder()
    {
        WriteInstruction("_base", "MARKER-BASE");
        WriteInstruction("zulu", "MARKER-ZULU");
        WriteInstruction("alpha", "MARKER-ALPHA");
        WriteInstruction("mike", "MARKER-MIKE");

        var prompt = Builder("zulu", "_base", "zulu", "alpha", "mike").BuildSystemPrompt();

        var positions = new[] { "MARKER-BASE", "MARKER-ZULU", "MARKER-ALPHA", "MARKER-MIKE" }
            .Select(m => prompt.IndexOf(m, StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(-1, positions);

        // Strictly increasing: supplied order, not alphabetical (which would put ALPHA first).
        Assert.True(positions.Zip(positions.Skip(1)).All(p => p.First < p.Second),
            $"instructions were reordered: positions were [{string.Join(", ", positions)}]");
    }

    /// <summary>
    /// <c>_base</c> is first even when the supplied list puts it later.
    /// </summary>
    /// <remarks>
    /// Its position relative to the role instruction is existing behaviour this change is not
    /// allowed to alter, so it is pinned rather than left to a load order an operator could set to
    /// anything.
    /// </remarks>
    [Fact]
    public void Base_IsPinnedFirstEvenWhenSuppliedLast()
    {
        WriteInstruction("_base", "MARKER-BASE");
        WriteInstruction("co-cto", "MARKER-ROLE");
        WriteInstruction("etiquette", "MARKER-EXTRA");

        var prompt = Builder("co-cto", "etiquette", "co-cto", "_base").BuildSystemPrompt();

        var basePos = prompt.IndexOf("MARKER-BASE", StringComparison.Ordinal);
        var rolePos = prompt.IndexOf("MARKER-ROLE", StringComparison.Ordinal);
        var extraPos = prompt.IndexOf("MARKER-EXTRA", StringComparison.Ordinal);

        Assert.True(basePos >= 0 && rolePos >= 0 && extraPos >= 0);
        Assert.True(basePos < extraPos, "_base must be inlined before everything else");
        Assert.True(basePos < rolePos, "_base must stay ahead of the role instruction");

        // And the rest keep the supplied order among themselves.
        Assert.True(extraPos < rolePos, "the supplied order must hold for non-base instructions");
    }

    /// <summary>
    /// An instruction listed twice is inlined once, and the repeat is reported.
    /// </summary>
    /// <remarks>
    /// Inlining the same bytes twice reads as emphasis for a safety rule and wastes context for a
    /// long one. Dropping the repeat silently would be its own version of #309, so it is logged.
    /// </remarks>
    [Fact]
    public void ARepeatedInstruction_IsInlinedOnceAndReported()
    {
        WriteInstruction("_base", "MARKER-BASE");
        WriteInstruction("etiquette", "MARKER-EXTRA");

        var logger = new CollectingLogger<PromptBuilder>();
        var builder = new PromptBuilder(
            Options.Create(new AgentOptions
            {
                Name = "test-agent",
                Role = "etiquette",
                WorkDir = Path.GetTempPath(),
                InstructionOrder = ["_base", "etiquette", "etiquette"],
            }),
            logger)
        {
            ContentRoot = _root,
        };

        var prompt = builder.BuildSystemPrompt();

        Assert.Equal(1, CountOccurrences(prompt, "MARKER-EXTRA"));
        Assert.Contains(logger.Warnings, w => w.Contains("etiquette", StringComparison.Ordinal));
    }

    // ── the regression surface ───────────────────────────────────────────────

    /// <summary>
    /// An agent with only base and role assembles byte-identically to before this change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole regression surface, asserted two ways. The <b>equivalence</b> half compares the
    /// new path against the fallback path, which is literally the pre-#309 code — two fixed reads
    /// in base-then-role order. The <b>literal</b> half pins the exact bytes, so a change that
    /// broke both paths the same way could not pass by agreeing with itself.
    /// </para>
    /// </remarks>
    [Fact]
    public void BaseAndRoleOnly_AssembleByteIdenticallyToTheOldTwoFileRead()
    {
        WriteInstruction("_base", "# Base\nbase body");
        WriteInstruction("co-cto", "# Role\nrole body");

        // The fallback branch IS the old behaviour: no InstructionOrder, two fixed paths.
        var legacy = Builder("co-cto").BuildSystemPrompt();
        var assembled = Builder("co-cto", "_base", "co-cto").BuildSystemPrompt();

        Assert.Equal(legacy, assembled);

        // AppendLine is "\n" on Linux and "\r\n" on Windows; the shape is what is pinned, not the
        // platform's newline.
        var nl = Environment.NewLine;
        Assert.Equal(
            $"# Base{nl}base body{nl}{nl}# Role{nl}role body{nl}{nl}",
            assembled);
    }

    /// <summary>
    /// Config generated before #309 carries no order, and falls back to exactly the two old paths.
    /// </summary>
    /// <remarks>
    /// This is what makes the change safe to deploy ahead of a fleet-wide reprovision: an agent
    /// whose <c>appsettings.json</c> predates the new key keeps today's prompt until it is
    /// regenerated, rather than losing its role instruction to an empty list.
    /// </remarks>
    [Fact]
    public void AnEmptyInstructionOrder_FallsBackToBasePlusRole()
    {
        WriteInstruction("_base", "MARKER-BASE");
        WriteInstruction("co-cto", "MARKER-ROLE");
        WriteInstruction("never-assigned", "MARKER-STALE");

        var prompt = Builder("co-cto").BuildSystemPrompt();

        Assert.Contains("MARKER-BASE", prompt, StringComparison.Ordinal);
        Assert.Contains("MARKER-ROLE", prompt, StringComparison.Ordinal);

        // The fallback is the OLD behaviour exactly — it must not start inlining directories the
        // old code never read.
        Assert.DoesNotContain("MARKER-STALE", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stale directory from an unassigned instruction is not resurrected.
    /// </summary>
    /// <remarks>
    /// ⚠️ This is why assembly is driven by the orchestrator's list rather than by enumerating
    /// <c>roles/</c>. The generator only writes files — it never deletes them — so unassigning an
    /// instruction leaves its directory in place. A directory listing would put a rule the operator
    /// deliberately removed back into the prompt, which is #309's failure with the sign flipped.
    /// </remarks>
    [Fact]
    public void AStaleDirectoryFromAnUnassignedInstruction_IsNotInlined()
    {
        WriteInstruction("_base", "MARKER-BASE");
        WriteInstruction("co-cto", "MARKER-ROLE");
        WriteInstruction("removed-last-week", "MARKER-REMOVED");

        var prompt = Builder("co-cto", "_base", "co-cto").BuildSystemPrompt();

        Assert.Contains("MARKER-ROLE", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("MARKER-REMOVED", prompt, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>Captures warning text so a test can assert on what was reported.</summary>
    private sealed class CollectingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
