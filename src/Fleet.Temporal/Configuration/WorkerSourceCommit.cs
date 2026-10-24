using System.Text.RegularExpressions;
using Temporalio.Worker;

namespace Fleet.Temporal.Configuration;

/// <summary>
/// The source commit this bridge image was built from, stamped as the Temporal worker build id.
/// </summary>
/// <remarks>
/// <para>
/// Workflow failure accountability (#476): Temporal records the completing worker's build id on
/// every <c>WorkflowTaskCompleted</c> event, and the orchestrator applies a compiled classifier rule
/// only when every one of them equals the rule's <c>sourceCommit</c>. This is the run's source
/// provenance, read from the run's own history rather than from an operator's claim.
/// </para>
/// <para>
/// The value comes from the image build (<c>--build-arg GIT_COMMIT=&lt;40-hex&gt;</c>, baked as
/// <see cref="EnvironmentVariable"/>). Anything that is not exactly 40 lowercase hex leaves the SDK's
/// default build id, so compiled runs stay unverified instead of claiming a commit. Worker
/// versioning stays off: the build id is recorded, never used to route tasks.
/// </para>
/// </remarks>
public static class WorkerSourceCommit
{
    public const string EnvironmentVariable = "FLEET_BUILD_COMMIT";

    private static readonly Regex CommitPattern = new(@"^[0-9a-f]{40}\z", RegexOptions.CultureInvariant);

    /// <summary>The commit, or null when <paramref name="value"/> is not exactly 40 lowercase hex.</summary>
    public static string? Resolve(string? value) => value is not null && CommitPattern.IsMatch(value) ? value : null;

    /// <summary>Stamps a resolvable commit as the worker build id; otherwise leaves the options untouched.</summary>
    public static string? Apply(TemporalWorkerOptions options, string? value)
    {
        var commit = Resolve(value);
#pragma warning disable CS0618 // BuildId without UseWorkerVersioning: recorded in history, never routed on.
        if (commit is not null) options.BuildId = commit;
#pragma warning restore CS0618
        return commit;
    }
}
