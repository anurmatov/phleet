using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class DiffInstructionVersionsTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "diff_instruction_versions")]
    [Description("Show a unified diff between two versions of an instruction.")]
    public async Task<string> DiffInstructionVersionsAsync(
        [Description("Instruction name (e.g. 'base', 'co-cto', 'developer')")] string instruction_name,
        [Description("First version number (older)")] int version_a,
        [Description("Second version number (newer)")] int version_b)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var instruction = await db.Instructions
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Name == instruction_name);

        if (instruction is null)
            return $"Instruction '{instruction_name}' not found.";

        var versions = await db.InstructionVersions
            .Where(v => v.InstructionId == instruction.Id && (v.VersionNumber == version_a || v.VersionNumber == version_b))
            .AsNoTracking()
            .ToListAsync();

        var va = versions.FirstOrDefault(v => v.VersionNumber == version_a);
        var vb = versions.FirstOrDefault(v => v.VersionNumber == version_b);

        if (va is null) return $"Version {version_a} not found for instruction '{instruction_name}'.";
        if (vb is null) return $"Version {version_b} not found for instruction '{instruction_name}'.";

        var linesA = va.Content.Split('\n');
        var linesB = vb.Content.Split('\n');

        var diff = BuildUnifiedDiff(
            $"{instruction_name} v{version_a}",
            $"{instruction_name} v{version_b}",
            linesA, linesB);

        return diff;
    }

    private static string BuildUnifiedDiff(string labelA, string labelB, string[] linesA, string[] linesB)
    {
        // Simple unified diff using longest common subsequence
        var lcs = ComputeLcs(linesA, linesB);
        var sb = new StringBuilder();
        sb.AppendLine($"--- {labelA}");
        sb.AppendLine($"+++ {labelB}");

        int ia = 0, ib = 0, lcsIdx = 0;
        var hunks = new List<(int startA, int startB, List<string> lines)>();
        var current = new List<string>();
        int hunkStartA = -1, hunkStartB = -1;

        while (ia < linesA.Length || ib < linesB.Length)
        {
            bool matchA = lcsIdx < lcs.Count && ia < linesA.Length && lcs[lcsIdx] == linesA[ia];
            bool matchB = lcsIdx < lcs.Count && ib < linesB.Length && lcs[lcsIdx] == linesB[ib];

            if (matchA && matchB)
            {
                if (current.Count > 0)
                {
                    hunks.Add((hunkStartA, hunkStartB, current));
                    current = [];
                    hunkStartA = -1;
                }
                lcsIdx++; ia++; ib++;
            }
            else if (!matchA || ib >= linesB.Length || (ia < linesA.Length && !matchB))
            {
                if (hunkStartA < 0) { hunkStartA = ia; hunkStartB = ib; }
                current.Add($"-{linesA[ia++]}");
            }
            else
            {
                if (hunkStartA < 0) { hunkStartA = ia; hunkStartB = ib; }
                current.Add($"+{linesB[ib++]}");
            }
        }
        if (current.Count > 0)
            hunks.Add((hunkStartA, hunkStartB, current));

        if (hunks.Count == 0)
            return $"No differences between v{labelA.Split(' ')[^1]} and v{labelB.Split(' ')[^1]}.";

        foreach (var (startA, startB, lines) in hunks)
        {
            var removals = lines.Count(l => l.StartsWith('-'));
            var additions = lines.Count(l => l.StartsWith('+'));
            sb.AppendLine($"@@ -{startA + 1},{removals} +{startB + 1},{additions} @@");
            foreach (var line in lines)
                sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static List<string> ComputeLcs(string[] a, string[] b)
    {
        int m = a.Length, n = b.Length;
        var dp = new int[m + 1, n + 1];
        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
                dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        var result = new List<string>();
        int x = m, y = n;
        while (x > 0 && y > 0)
        {
            if (a[x - 1] == b[y - 1]) { result.Add(a[x - 1]); x--; y--; }
            else if (dp[x - 1, y] >= dp[x, y - 1]) x--;
            else y--;
        }
        result.Reverse();
        return result;
    }
}
