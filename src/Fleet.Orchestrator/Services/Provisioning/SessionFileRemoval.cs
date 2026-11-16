using Fleet.Orchestrator.Data;
using Fleet.Shared;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Removes an agent's session file, <see cref="AgentSessionFile.RelativePath"/> under its work
/// directory, from the host side of its workspace mount. Two callers: a reprovision that was asked
/// for a new session, between its two containers, and the creation of an agent, whose workspace
/// may be the one a deleted agent of the same name left behind. Neither calls it while a container
/// of the agent should exist, so nothing writes the file again before the next container reads it.
/// </summary>
internal static class SessionFileRemoval
{
    /// <summary>Where <see cref="ContainerSpecBuilder.BuildBinds"/> mounts <c>workspaces/{container}</c>.</summary>
    internal const string WorkspaceMount = "/workspace";

    /// <summary>The name every fault of <see cref="Remove"/> starts with.</summary>
    internal const string FaultCode = "session_file_not_removed";

    /// <summary>
    /// The host path of the file the agent's next container reads as its session file, or null when
    /// the agent's work directory is outside the workspace and provider-home mounts: such a file
    /// lives in the container's own filesystem and is gone with the container.
    /// </summary>
    internal static string? HostPath(Agent agent, string baseDir)
    {
        if (WorkDirInsidePersistentMount(agent.WorkDir) is not { } below)
            return null;
        return Path.Combine([WorkspaceDir(agent, baseDir), .. below, .. AgentSessionFile.RelativePath.Split('/')]);
    }

    /// <summary>
    /// Removes the file. A file that is not there is success. Returns the fault when it is there and
    /// could not be removed: one sentence naming the file and why, to which the caller adds what
    /// that means for its own operation. The caller must report it and start no container.
    /// </summary>
    internal static string? Remove(Agent agent, string baseDir, ILogger logger)
    {
        if (HostPath(agent, baseDir) is not { } path)
        {
            logger.LogInformation(
                "New session for '{Agent}': work directory '{WorkDir}' is outside the workspace and provider-home mounts, so no session file outlives its container",
                agent.Name, agent.WorkDir);
            return null;
        }

        string? reason = null;
        try
        {
            // The workspace belongs to the agent, which could have made a directory on the way a
            // link out of it. Nothing is deleted through one.
            if (LinkOnTheWay(WorkspaceDir(agent, baseDir), path) is { } link)
            {
                reason = $"{Path.GetRelativePath(baseDir, link)} is a symbolic link";
            }
            else
            {
                var existed = File.Exists(path);
                File.Delete(path);
                logger.LogInformation(
                    existed
                        ? "New session for '{Agent}': removed {Path}"
                        : "New session for '{Agent}': no session file at {Path}",
                    agent.Name, path);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // No directory to hold it, so no file.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "New session for '{Agent}': could not remove {Path}", agent.Name, path);
            reason = ex.GetType().Name;
        }

        return reason is null
            ? null
            : $"{FaultCode}: {Path.GetRelativePath(baseDir, path)} could not be removed ({reason}).";
    }

    private static string WorkspaceDir(Agent agent, string baseDir) =>
        Path.Combine(baseDir, "workspaces", agent.ContainerName);

    /// <summary>
    /// Maps the workspace and the two persistent provider-home mounts in BuildBinds to their
    /// paths below workspaces/{container}. Both home mounts exist for every provider.
    /// </summary>
    private static string[]? WorkDirInsidePersistentMount(string? workDir)
    {
        if (workDir is null || !workDir.StartsWith('/'))
            return null;

        var segments = new List<string>();
        foreach (var segment in workDir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }
            segments.Add(segment);
        }

        if (segments.Count > 0 && segments[0] == WorkspaceMount.TrimStart('/'))
            return [.. segments.Skip(1)];
        if (segments.Count >= 2 && segments[0] == "root")
        {
            if (segments[1] == ".claude") return ["claude", .. segments.Skip(2)];
            if (segments[1] == ".codex") return ["codex", .. segments.Skip(2)];
        }
        return null;
    }

    /// <summary>The first directory between the workspace and the file that is a link, or null.</summary>
    private static string? LinkOnTheWay(string workspaceDir, string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        var between = new List<string>();
        while (directory is not null && directory.Length > workspaceDir.Length)
        {
            between.Add(directory);
            directory = Path.GetDirectoryName(directory);
        }

        between.Reverse();
        return between.FirstOrDefault(d => new DirectoryInfo(d).LinkTarget is not null);
    }
}
