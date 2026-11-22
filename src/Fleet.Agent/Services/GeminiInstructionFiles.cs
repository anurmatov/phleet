using System.Text;

namespace Fleet.Agent.Services;

/// <summary>
/// How a Gemini agent's instructions reach the Antigravity CLI (<c>agy</c>): as always-on rule files
/// in the CLI's global rules directory, <c>~/.gemini/config/rules/</c>.
/// </summary>
/// <remarks>
/// <para>The CLI has no flag or variable for a system prompt. It adds every always-on rule file to
/// the system instruction it sends, inside its own prompt, and it cuts what does not fit without
/// failing. Measured on the pinned CLI (<c>tests/antigravity-cli/run.sh</c> keeps these true):</para>
/// <list type="bullet">
/// <item>one rule file is cut at 24,000 bytes, on a line boundary;</item>
/// <item>all always-on rule files together share 60,000 bytes; over that the CLI leaves the largest
/// files out and only names their paths;</item>
/// <item>files of one directory are added in name order, each body byte for byte.</item>
/// </list>
/// <para>So the instructions are split on line boundaries into files of at most
/// <see cref="ChunkBytes"/> whose names sort in order, and a start is refused when the whole text
/// cannot arrive: a line longer than a file, or more than <see cref="BudgetBytes"/> together with
/// the rule files of others that the CLI would also load. A cut prompt is never sent.</para>
/// <para>The files hold the instructions in clear, like the prompt file of the other providers, so
/// they are written for their owner only. Nothing here logs their content.</para>
/// </remarks>
internal static class GeminiInstructionFiles
{
    /// <summary>The most one of our files holds. The CLI cuts a rule file at 24,000 bytes.</summary>
    internal const int ChunkBytes = 20_000;

    /// <summary>The CLI's own limit for one rule file; a larger file of someone else counts as this much.</summary>
    internal const int CliFileCapBytes = 24_000;

    /// <summary>
    /// The most all rule files together may hold for a start to go ahead. The CLI's limit is
    /// 60,000 bytes (59,959 arrived whole, 60,030 did not); this leaves a margin under it.
    /// </summary>
    internal const int BudgetBytes = 56_000;

    internal const string FilePrefix = "fleet-instructions-";

    /// <summary>Without this header the CLI does not treat a file in the rules directory as always on.</summary>
    internal const string FrontMatter = "---\ntrigger: always_on\n---\n";

    private static readonly string[] DirectoryRuleFiles = ["GEMINI.md", "AGENTS.md"];
    private static readonly string[] CustomizationRoots = [".agents", ".agent", "_agents", "_agent"];

    /// <summary><c>~/.gemini/config/rules</c> under <paramref name="geminiHome"/> (<c>~/.gemini</c>).</summary>
    internal static string RulesDirectory(string geminiHome) => Path.Combine(geminiHome, "config", "rules");

    internal static string FileName(int number) => $"{FilePrefix}{number:000}.md";

    /// <summary>
    /// The instructions as pieces that end on line boundaries, each at most <see cref="ChunkBytes"/>
    /// of UTF-8; their concatenation is the text. Null, with <paramref name="fault"/>, when one line
    /// is longer than a piece.
    /// </summary>
    internal static IReadOnlyList<string>? Split(string instructions, out string fault)
    {
        fault = "";
        var chunks = new List<string>();
        var current = new StringBuilder();
        var currentBytes = 0;
        var start = 0;
        while (start < instructions.Length)
        {
            var newline = instructions.IndexOf('\n', start);
            var end = newline < 0 ? instructions.Length : newline + 1;
            var lineBytes = Encoding.UTF8.GetByteCount(instructions.AsSpan(start, end - start));
            if (lineBytes > ChunkBytes)
            {
                fault = $"one line of the agent's instructions is {lineBytes} bytes; "
                    + $"the Antigravity CLI takes lines of at most {ChunkBytes} bytes";
                return null;
            }

            if (currentBytes + lineBytes > ChunkBytes)
            {
                chunks.Add(current.ToString());
                current.Clear();
                currentBytes = 0;
            }

            current.Append(instructions, start, end - start);
            currentBytes += lineBytes;
            start = end;
        }

        if (current.Length > 0)
            chunks.Add(current.ToString());
        return chunks;
    }

    /// <summary>
    /// Makes the rules directory hold exactly these instructions: writes the pieces, then removes
    /// every file of ours the text no longer needs. Returns null, or why the whole text cannot be
    /// delivered (in which case the caller must not start the CLI).
    /// </summary>
    internal static string? Prepare(string geminiHome, string workDir, string instructions)
    {
        var chunks = Split(instructions, out var fault);
        if (chunks is null)
            return fault;

        var ours = Encoding.UTF8.GetByteCount(instructions);
        var others = OtherRuleFiles(geminiHome, workDir);
        var othersBytes = others.Sum(f => f.CountedBytes);
        if (ours + othersBytes > BudgetBytes)
        {
            return others.Count == 0
                ? $"the agent's instructions are {ours} bytes; the Antigravity CLI delivers at most {BudgetBytes} bytes of instructions"
                : $"the agent's instructions are {ours} bytes and {others.Count} other rule file(s) the Antigravity CLI loads "
                    + $"({string.Join(", ", others.Select(f => f.Path))}) add {othersBytes} bytes; together they may be at most {BudgetBytes} bytes";
        }

        try
        {
            var directory = RulesDirectory(geminiHome);
            Directory.CreateDirectory(directory);
            for (var i = 0; i < chunks.Count; i++)
                WriteOwnerOnly(Path.Combine(directory, FileName(i + 1)), FrontMatter + chunks[i]);

            var needed = Enumerable.Range(1, chunks.Count).Select(FileName).ToHashSet(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(directory, FilePrefix + "*"))
            {
                if (!needed.Contains(Path.GetFileName(path)))
                    File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the agent's instructions could not be written for the Antigravity CLI ({ex.GetType().Name})";
        }

        return null;
    }

    /// <summary>
    /// The rule files of others that the CLI adds beside ours, with what each takes of the shared
    /// limit: the global files under <c>~/.gemini</c>, other files in the rules directories, and
    /// the directory rule files of the work directory and every directory above it. Counting a
    /// file the CLI would not load only refuses sooner; missing one could let the CLI drop ours.
    /// </summary>
    internal static IReadOnlyList<(string Path, long CountedBytes)> OtherRuleFiles(string geminiHome, string workDir)
    {
        var found = new List<(string, long)>();

        void Add(string path)
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && !file.Name.StartsWith(FilePrefix, StringComparison.Ordinal))
                    found.Add((path, Math.Min(file.Length, CliFileCapBytes)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A file that cannot be examined is counted at the most it could take.
                found.Add((path, CliFileCapBytes));
            }
        }

        void AddMarkdownIn(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return;
                foreach (var path in Directory.EnumerateFiles(directory, "*.md").Order(StringComparer.Ordinal))
                    Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                found.Add((directory, CliFileCapBytes));
            }
        }

        foreach (var directory in new[] { geminiHome, Path.Combine(geminiHome, "config") })
        {
            foreach (var name in DirectoryRuleFiles)
                Add(Path.Combine(directory, name));
        }

        AddMarkdownIn(RulesDirectory(geminiHome));
        try
        {
            var plugins = Path.Combine(geminiHome, "config", "plugins");
            if (Directory.Exists(plugins))
            {
                foreach (var plugin in Directory.EnumerateDirectories(plugins).Order(StringComparer.Ordinal))
                    AddMarkdownIn(Path.Combine(plugin, "rules"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            found.Add((Path.Combine(geminiHome, "config", "plugins"), CliFileCapBytes));
        }

        for (var directory = Path.GetFullPath(workDir); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            foreach (var name in DirectoryRuleFiles)
                Add(Path.Combine(directory, name));
            foreach (var root in CustomizationRoots)
                AddMarkdownIn(Path.Combine(directory, root, "rules"));
        }

        return found;
    }

    private static void WriteOwnerOnly(string path, string text)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Written again below.
        }

        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            if (File.Exists(path))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        stream.Write(bytes);
    }
}
