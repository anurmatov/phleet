using System.Security.Cryptography;

namespace Fleet.WorkflowOutcomeQualification;

/// <summary>Fail closed on symlinked custody or any file whose immutable bytes no longer match.</summary>
public static class OwnedPaths
{
    public static string Directory(string root, string relative)
    {
        var path = Resolve(root, relative);
        if (!System.IO.Directory.Exists(path)) throw new QualificationException("owned directory is missing");
        return path;
    }

    public static string File(string root, string relative, string expectedHash)
    {
        var path = Resolve(root, relative);
        if (!System.IO.File.Exists(path)) throw new QualificationException("pinned file is missing");
        using var stream = System.IO.File.OpenRead(path);
        if (Convert.ToHexStringLower(SHA256.HashData(stream)) != expectedHash)
            throw new QualificationException("pinned file hash changed");
        return path;
    }

    private static string Resolve(string root, string relative)
    {
        var basePath = Path.GetFullPath(root);
        var result = Path.GetFullPath(Path.Combine(basePath, relative));
        if (!result.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new QualificationException("path escapes custody root");
        for (var current = result; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((System.IO.File.Exists(current) || System.IO.Directory.Exists(current))
                && (System.IO.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new QualificationException("symlinked custody is not allowed");
        }
        return result;
    }
}
