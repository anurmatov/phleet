namespace Fleet.Comms.Tests;

/// <summary>
/// The guard that keeps the fix in <see cref="OperatorCliCollection"/> from being "cleaned up".
/// </summary>
/// <remarks>
/// <para>
/// <c>Comms__AuthStorePath</c> is process-wide, and every class in that collection sets it in its
/// constructor and nulls it in <c>Dispose</c>. A collection without <c>DisableParallelization</c>
/// still runs its classes concurrently — what a collection guarantees is only that it does not
/// overlap with OTHER collections — so tagging those classes was never the fix. That is what
/// <c>DeploymentHardeningTests.ARevokedDeviceNoLongerBlocksANewCode</c> was failing on: every CLI
/// call re-resolves the store path at call time, so a sibling disposing between two of them leaves
/// the next call with no path and <c>devices revoke</c> exits 1.
/// </para>
/// <para>
/// The failure is invisible from inside the class that hits it — the race only lands when the
/// runner's thread timing cooperates, so it passes on a fast laptop and fails on a loaded CI runner,
/// which is how it came to be reported as a flake and closed by a re-run. Removing
/// <c>DisableParallelization = true</c> would restore that without failing anything on most
/// machines. This assertion fails immediately instead.
/// </para>
/// </remarks>
public sealed class OperatorCliCollectionGuardTests
{
    [Fact]
    public void The_auth_store_path_collection_serializes_its_classes()
    {
        var attribute = (CollectionDefinitionAttribute)Assert.Single(
            typeof(OperatorCliCollection).GetCustomAttributes(typeof(CollectionDefinitionAttribute), false));

        Assert.True(
            attribute.DisableParallelization,
            "Comms__AuthStorePath is process-wide and every class in this collection sets and "
            + "clears it, so its classes must not run concurrently. A collection alone does not "
            + "serialize its own classes - only DisableParallelization does.");
    }
}
