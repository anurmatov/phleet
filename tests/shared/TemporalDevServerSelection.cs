using System.Text.RegularExpressions;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Testing;

namespace Fleet.Tests.Shared;

/// <summary>
/// Test-only choice of the Temporal dev server the workflow-outcome real-server tests start (#476).
/// </summary>
/// <remarks>
/// <para>
/// Unset, which is how CI runs: the SDK downloads its default dev server, exactly as before.
/// </para>
/// <para>
/// For qualification against one server version, set BOTH <see cref="CliPathVariable"/> (absolute
/// path to a cached, executable Temporal CLI) and <see cref="ServerVersionVariable"/> (the exact
/// <c>x.y.z</c> server version it must report). The binary is then started through the SDK's
/// <c>ExistingPath</c>, which never downloads, and the test fails unless the running server reports
/// exactly that version. One variable without the other, a relative, missing, directory or
/// non-executable path, or a malformed version fails before anything is started.
/// </para>
/// </remarks>
internal static class TemporalDevServerSelection
{
    public const string CliPathVariable = "WORKFLOW_OUTCOMES_TEMPORAL_CLI_PATH";
    public const string ServerVersionVariable = "WORKFLOW_OUTCOMES_TEMPORAL_SERVER_VERSION";

    private static readonly Regex VersionPattern = new(@"^[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant);

    public sealed record Selection(string CliPath, string ServerVersion);

    /// <summary>The explicit selection, or null for the SDK's ordinary download. Throws on a partial or invalid pair.</summary>
    public static Selection? Resolve(string? cliPath, string? serverVersion)
    {
        var pathSet = !string.IsNullOrEmpty(cliPath);
        var versionSet = !string.IsNullOrEmpty(serverVersion);
        if (!pathSet && !versionSet) return null;
        if (!pathSet || !versionSet)
            throw new InvalidOperationException($"{CliPathVariable} and {ServerVersionVariable} must be set together");
        if (!Path.IsPathFullyQualified(cliPath!))
            throw new InvalidOperationException($"{CliPathVariable} must be an absolute path");
        if (!File.Exists(cliPath))
            throw new InvalidOperationException($"{CliPathVariable} does not name an existing file");
        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(cliPath!) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new InvalidOperationException($"{CliPathVariable} is not executable");
        if (!VersionPattern.IsMatch(serverVersion!))
            throw new InvalidOperationException($"{ServerVersionVariable} must be an exact x.y.z server version");
        return new Selection(cliPath!, serverVersion!);
    }

    /// <summary>Throws unless the started server reported exactly the selected version.</summary>
    public static void EnsureServerVersion(string? reported, string expected)
    {
        if (!string.Equals(reported, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"the selected Temporal CLI started server {reported ?? "<none>"}, not {expected}");
    }

    /// <summary>Starts the dev server chosen by the two environment variables.</summary>
    public static Task<WorkflowEnvironment> StartLocalAsync(WorkflowEnvironmentStartLocalOptions options) =>
        StartLocalAsync(options, Environment.GetEnvironmentVariable(CliPathVariable), Environment.GetEnvironmentVariable(ServerVersionVariable));

    public static async Task<WorkflowEnvironment> StartLocalAsync(WorkflowEnvironmentStartLocalOptions options, string? cliPath, string? serverVersion)
    {
        var selection = Resolve(cliPath, serverVersion);
        if (selection is null) return await WorkflowEnvironment.StartLocalAsync(options);

        options.DevServerOptions ??= new DevServerOptions();
        options.DevServerOptions.ExistingPath = selection.CliPath;
        var env = await WorkflowEnvironment.StartLocalAsync(options);
        try
        {
            var info = await env.Client.Connection.WorkflowService.GetSystemInfoAsync(new GetSystemInfoRequest());
            EnsureServerVersion(info.ServerVersion, selection.ServerVersion);
            return env;
        }
        catch
        {
            await env.DisposeAsync();
            throw;
        }
    }
}
