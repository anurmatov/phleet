using Fleet.Agent.Tests.Harness;
using Fleet.Shared;

namespace Fleet.Agent.Tests;

/// <summary>
/// #349: <c>tests/claude-local-wire/run.sh</c> proves what the pinned CLI sends for each row of its
/// ROWS table. That proof only covers the agent if every row's <c>--effort</c> value and
/// <c>CLAUDE_CODE_EXTRA_BODY</c> are exactly what <see cref="ClaudeLocalModel"/> produces for the
/// row's Effort, so this pins the two to each other.
/// </summary>
public class ClaudeLocalModelWireHarnessTests
{
    private sealed record Row(string Label, string Effort, string Flag, string ExtraBody);

    [Fact]
    public void Every_local_effort_value_has_a_row()
    {
        var efforts = Rows().Select(r => r.Effort).ToHashSet();

        Assert.Contains("null", efforts);
        foreach (var level in ClaudeLocalModel.ThinkingLevels)
            Assert.Contains(level, efforts);
    }

    [Fact]
    public void Each_row_uses_the_flag_and_extra_body_the_agent_sends()
    {
        foreach (var row in Rows().Where(r => r.Effort != "-"))
        {
            var effort = row.Effort == "null" ? null : row.Effort;

            Assert.True((ClaudeLocalModel.EffortArgument(effort) ?? "none") == row.Flag,
                $"row {row.Label}: harness passes --effort {row.Flag}, the agent passes {ClaudeLocalModel.EffortArgument(effort) ?? "none"}");

            var extra = ClaudeLocalModel.BuildEnvironment("http://inference-host:11434", "tag", effort)
                .Where(e => e.Key == "CLAUDE_CODE_EXTRA_BODY")
                .Select(e => e.Value)
                .SingleOrDefault() ?? "none";
            Assert.True(extra == row.ExtraBody,
                $"row {row.Label}: harness sets CLAUDE_CODE_EXTRA_BODY={row.ExtraBody}, the agent sets {extra}");
        }
    }

    private static List<Row> Rows()
    {
        var lines = File.ReadAllLines(RepoPaths.Resolve("tests/claude-local-wire/run.sh"));
        var begin = Array.IndexOf(lines, "# rows:begin");
        var end = Array.IndexOf(lines, "# rows:end");
        Assert.True(begin >= 0 && end > begin, "run.sh has lost its '# rows:begin' / '# rows:end' markers");

        var rows = lines[(begin + 1)..end]
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('\'') && l.EndsWith('\''))
            .Select(l => l.Trim('\'').Split('|'))
            .Select(c => new Row(c[0], c[1], c[2], c[3]))
            .ToList();
        Assert.NotEmpty(rows);
        return rows;
    }
}
