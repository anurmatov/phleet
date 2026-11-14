using System.Xml.Linq;

namespace Fleet.Protocol.Tests;

/// <summary>
/// The repository-wide build files must not become a way around the dependency rules.
/// </summary>
/// <remarks>
/// <c>Fleet.Protocol</c> is BCL-only and <c>Fleet.Conversations.Contracts</c> references the
/// protocol alone. Both rules are checked against the project files, and a reference added to
/// <c>Directory.Build.props</c> or <c>Directory.Packages.props</c> reaches every project without
/// appearing in any of them. The assembly-level checks would still catch a reference that is used,
/// but not one that only ships.
/// </remarks>
public class BuildPropsDependencyTests
{
    private static readonly string[] RootBuildFiles = ["Directory.Build.props", "Directory.Packages.props"];

    private static readonly string[] ReferenceItems =
        ["PackageReference", "ProjectReference", "GlobalPackageReference", "Reference", "FrameworkReference"];

    /// <summary>Directories that hold build output or dependencies, never source.</summary>
    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules", ".git", ".claude"];

    /// <summary>The deployment's runtime state (<c>./fleet</c>), which is not tracked.</summary>
    private const string RuntimeStateDirectory = "fleet";

    private static DirectoryInfo RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
                directory = directory.Parent;

            Assert.True(directory is not null, "could not locate the repository root");
            return directory!;
        }
    }

    /// <summary>
    /// Covers the two root files and any <c>Directory.*</c> file a later change nests under them.
    /// </summary>
    [Fact]
    public void No_shared_build_file_adds_a_reference_to_the_projects_under_it()
    {
        var root = RepositoryRoot.FullName;
        var files = SourceFiles(root)
            .Where(path => Path.GetFileName(path).StartsWith("Directory.", StringComparison.Ordinal))
            .ToArray();

        foreach (var expected in RootBuildFiles)
            Assert.Contains(Path.Combine(root, expected), files);

        foreach (var file in files)
        {
            var references = XDocument.Load(file).Descendants()
                .Where(e => ReferenceItems.Contains(e.Name.LocalName, StringComparer.Ordinal))
                .Select(e => $"{e.Name.LocalName} {e.Attribute("Include")?.Value}")
                .ToArray();

            Assert.True(
                references.Length == 0,
                $"{Path.GetRelativePath(root, file)} declares {string.Join(", ", references)}. A reference "
                + "here reaches Fleet.Protocol and Fleet.Conversations.Contracts without appearing in "
                + "their project files. Put it in the project that needs it.");
        }
    }

    /// <summary>
    /// An image build that restores without the two root files has no target framework and no
    /// package versions. Each .NET Dockerfile copies them into the build stage before it first
    /// runs <c>dotnet</c>.
    /// </summary>
    [Fact]
    public void Every_dotnet_dockerfile_copies_the_shared_build_files_before_restoring()
    {
        var root = RepositoryRoot.FullName;

        var dockerfiles = SourceFiles(root)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.StartsWith("Dockerfile", StringComparison.Ordinal)
                    || name.EndsWith(".Dockerfile", StringComparison.Ordinal);
            })
            .Select(path => (Path: Path.GetRelativePath(root, path), Lines: File.ReadAllLines(path)))
            .Where(file => file.Lines.Any(RunsDotnetBuild))
            .ToArray();

        Assert.NotEmpty(dockerfiles);

        foreach (var (path, lines) in dockerfiles)
        {
            var firstBuild = Array.FindIndex(lines, RunsDotnetBuild);
            var copy = Array.FindIndex(lines, line =>
                line.StartsWith("COPY ", StringComparison.Ordinal)
                && RootBuildFiles.All(file => line.Contains(file, StringComparison.Ordinal)));

            Assert.True(
                copy >= 0 && copy < firstBuild,
                $"{path} runs dotnet without first copying {string.Join(" and ", RootBuildFiles)}.");
        }
    }

    [Theory]
    [InlineData("Dockerfile", "Fleet.Agent")]
    [InlineData("Dockerfile.temporal", "Fleet.Temporal")]
    [InlineData("src/Fleet.Orchestrator/Dockerfile", "Fleet.Orchestrator")]
    [InlineData("src/Fleet.Bridge/Dockerfile", "Fleet.Bridge")]
    [InlineData("src/Fleet.Telegram/Dockerfile", "Fleet.Telegram")]
    [InlineData("src/Fleet.Memory/Dockerfile", "Fleet.Memory")]
    [InlineData("src/Fleet.Comms/Dockerfile", "Fleet.Comms")]
    [InlineData("src/Fleet.SecretProxy/Dockerfile", "Fleet.SecretProxy")]
    public void Image_restore_includes_every_referenced_project(string dockerfile, string project)
    {
        var root = RepositoryRoot.FullName;
        var source = File.ReadAllText(Path.Combine(root, dockerfile));
        var beforeRestore = source[..source.IndexOf("RUN dotnet restore", StringComparison.Ordinal)];
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Visit($"src/{project}/{project}.csproj");

        void Visit(string path)
        {
            if (!visited.Add(path)) return;
            Assert.Contains($"COPY {path} {Path.GetDirectoryName(path)}/", beforeRestore);
            var document = XDocument.Load(Path.Combine(root, path));
            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var relative = reference.Attribute("Include")!.Value.Replace('\\', '/');
                Visit(Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root,
                    Path.GetDirectoryName(path)!, relative))));
            }
        }
    }

    private static bool RunsDotnetBuild(string line) =>
        line.Contains("dotnet restore", StringComparison.Ordinal)
        || line.Contains("dotnet publish", StringComparison.Ordinal)
        || line.Contains("dotnet build", StringComparison.Ordinal);

    /// <summary>Every file under <paramref name="root"/>, never descending into a skipped directory.</summary>
    private static IEnumerable<string> SourceFiles(string root)
    {
        var pending = new Stack<string>([root]);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            foreach (var file in Directory.EnumerateFiles(directory))
                yield return file;

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                var runtimeState = directory == root && name == RuntimeStateDirectory;

                if (!runtimeState && !SkippedDirectories.Contains(name, StringComparer.Ordinal))
                    pending.Push(child);
            }
        }
    }
}
