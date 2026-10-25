using System.Collections.ObjectModel;

namespace Fleet.WorkflowOutcomeQualification;

public sealed record PlannedCommand(
    string Operation,
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory,
    TimeSpan Deadline);

public sealed class CommandPlanner(string manifestDirectory)
{
    public PlannedCommand Build(ProcessQualificationManifest manifest, string operation)
    {
        // The recovered manifest does not yet bind every service/container/volume and nested
        // connection identity. Never turn its Compose project name into mutation authority.
        if (operation is not ("git-source" or "git-status" or "codex-version"))
            throw new QualificationBlockedException("complete immutable process/cleanup registry is not implemented");

        if (!manifest.AllowedOperations.Contains(operation))
            throw new QualificationException("operation '" + operation + "' is not in allowedOperations");

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin:/bin",
            ["HOME"] = OwnedPaths.Directory(manifestDirectory, "home"),
            ["XDG_CACHE_HOME"] = OwnedPaths.Directory(manifestDirectory, "cache"),
            ["TMPDIR"] = OwnedPaths.Directory(manifestDirectory, "tmp"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_TERMINAL_PROMPT"] = "0"
        };
        string[] argv;
        var deadline = TimeSpan.FromSeconds(30);
        var workingDirectory = OwnedPaths.Directory(manifestDirectory,
            operation.StartsWith("git-", StringComparison.Ordinal) ? manifest.Runtime.SourceDirectory : manifest.Runtime.WorkingDirectory);

        switch (operation)
        {
            case "git-source":
                argv = ["git", "show", "-s", "--format=%H %T", "HEAD"];
                break;
            case "git-status":
                argv = ["git", "status", "--porcelain=v1", "--untracked-files=all"];
                break;
            case "codex-version":
                argv = ["codex", "--version"];
                environment["CODEX_OSS_BASE_URL"] = manifest.Fixture.Endpoint;
                deadline = TimeSpan.FromSeconds(15);
                break;
            default:
                throw new QualificationException("no command is defined for operation '" + operation + "'");
        }

        if (argv.Length == 0 || argv.Any(string.IsNullOrWhiteSpace))
            throw new QualificationException("planned argv must be nonempty");
        var executable = manifest.Executables[argv[0]];
        var binary = OwnedPaths.File(manifestDirectory, executable.Path, executable.Sha256);
        return new PlannedCommand(
            operation, binary, argv[1..],
            new ReadOnlyDictionary<string, string>(environment), workingDirectory, deadline);
    }

}
