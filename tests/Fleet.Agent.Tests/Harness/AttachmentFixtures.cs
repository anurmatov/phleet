using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Fleet.Agent.Tests.Harness;

/// <summary>
/// The synthetic attachment fixtures (#455 §9), generated per run by the committed
/// <c>Fixtures/attachments/generate.py</c> into a temporary directory. A missing python3, ffmpeg or
/// qpdf is a hard failure, never a skip: a skipped real-tool row is an unmeasured row reported green.
/// </summary>
internal sealed class AttachmentFixtures : IDisposable
{
    private readonly PiTempDirectory _temp = new("pi-fixtures");
    private readonly Dictionary<string, (string Path, string? Code)> _entries = new(StringComparer.Ordinal);

    private AttachmentFixtures()
    {
    }

    public string Directory => _temp.Path;

    public static AttachmentFixtures Generate(int seed = 455)
    {
        var fixtures = new AttachmentFixtures();
        try
        {
            var script = RepoPaths.Resolve("tests/Fleet.Agent.Tests/Fixtures/attachments/generate.py");
            var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(fixtures.Directory);
            psi.ArgumentList.Add("--seed");
            psi.ArgumentList.Add(seed.ToString());
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("python3 did not start");
            var stderr = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)) || process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"generate.py failed (a hard failure by design: it needs python3, ffmpeg and qpdf): {stderr.Result}");

            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures.Directory, "manifest.json")))!;
            foreach (var (name, entry) in manifest["fixtures"]!.AsObject())
                fixtures._entries[name] = (Path.Combine(fixtures.Directory, (string)entry!["file"]!), (string?)entry["code"]);
            return fixtures;
        }
        catch
        {
            fixtures.Dispose();
            throw;
        }
    }

    public string PathOf(string name) => _entries[name].Path;

    public string Code(string name) => _entries[name].Code ?? throw new InvalidOperationException($"{name} has no code");

    public byte[] Bytes(string name) => File.ReadAllBytes(PathOf(name));

    public void Dispose() => _temp.Dispose();
}
