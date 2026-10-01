using System.Diagnostics;

namespace Fleet.Comms.Tests;

public sealed class MinioDependencyGuardTests
{
    [Theory]
    [InlineData("echo \"$(perl -e 'print 1')\"", 1)]
    [InlineData("value=\"$(perl -e 'print 1')\"", 1)]
    [InlineData("echo '$(perl -e 1)'", 0)]
    [InlineData("echo \"literal perl text\"", 0)]
    [InlineData("echo \"$(printf '%s' fine)\"", 0)]
    [InlineData("exec weed server -s3", 0, "weed")]
    [InlineData("exec curl http://example.test", 1, "weed")]
    [InlineData("mc ls bucket", 1, "weed")]
    public void Quoted_commands_are_inspected_but_literal_data_is_not(string command, int expected, string allow = "mc")
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CLAUDE.md"))) root = root.Parent;
        Assert.NotNull(root);
        var input = Path.GetTempFileName();
        try
        {
            File.WriteAllText(input, "#!/bin/bash\n" + command + "\n");
            var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(root.FullName, "scripts/check-minio-init-deps.sh"));
            start.ArgumentList.Add("--allow");
            start.ArgumentList.Add(allow);
            start.ArgumentList.Add(input);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), output);
            Assert.Equal(expected, process.ExitCode);
        }
        finally { File.Delete(input); }
    }
}
