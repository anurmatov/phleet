using System.Text.RegularExpressions;

namespace Fleet.WorkflowOutcomeQualification;

/// <summary>Allow only configuration data needed for local source reads, never executable extensions/includes.</summary>
public static class GitConfigurationGuard
{
    public static void Verify(string source)
    {
        // Linked-worktree indirection and nested includes need their own immutable custody.
        var directory = OwnedPaths.Directory(source, ".git");
        var path = Path.Combine(directory, "config");
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || File.Exists(Path.Combine(directory, "config.worktree")))
            throw new QualificationBlockedException("Git configuration custody is unsupported");
        string section = "";
        foreach (var original in File.ReadAllLines(path))
        {
            var line = original.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                var match = Regex.Match(line, """^\[(core|remote|branch|user)(?: "[^"\\]+")?\]$""",
                    RegexOptions.CultureInvariant);
                if (!match.Success) throw new QualificationBlockedException("Git configuration section is not allowlisted");
                section = match.Groups[1].Value;
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals <= 0) throw new QualificationBlockedException("Git configuration syntax is unsupported");
            var key = line[..equals].Trim().ToLowerInvariant();
            var allowed = section switch
            {
                "core" => key is "repositoryformatversion" or "filemode" or "bare" or "logallrefupdates"
                    or "ignorecase" or "precomposeunicode" or "symlinks" or "fsmonitor" or "hookspath",
                "remote" => key is "url" or "fetch" or "pushurl",
                "branch" => key is "remote" or "merge" or "rebase",
                "user" => key is "name" or "email",
                _ => false
            };
            // Continuations/escaped values could hide an additional config directive.
            if (!allowed || line.EndsWith('\\'))
                throw new QualificationBlockedException("Git configuration key is not allowlisted");
        }
    }
}
