namespace Fleet.Comms.Tests;

/// <summary>
/// Everything that reads or writes <c>Comms__AuthStorePath</c> runs in one collection, so xUnit
/// never runs two of them at once.
///
/// <para>The environment is process-wide. Two classes setting that variable in parallel had each
/// other's store path resolved underneath them, which showed up as a scatter of unrelated failures
/// — the CLI writing to one database while the assertion read another. Giving each class its own
/// variable is not possible, because the point of these tests is that the CLI reads configuration
/// exactly as the service does.</para>
///
/// <para>⚠️ <c>DisableParallelization</c> is the part of that fix that actually works, and putting
/// four classes in one collection was NOT. A collection without it still runs its classes
/// CONCURRENTLY; what a collection guarantees is that it does not overlap with other collections.
/// So the race the comment above describes was never closed — the classes kept setting the variable
/// over each other, and each one's <c>Dispose</c> nulls it for the others.</para>
///
/// <para>That is what <c>DeploymentHardeningTests.ARevokedDeviceNoLongerBlocksANewCode</c> was
/// hitting. It issues a code, revokes the device, and issues again; every step re-resolves
/// <c>Comms__AuthStorePath</c> through <c>OperatorCommands.ResolveStorePath()</c> at CALL time, so
/// a sibling class disposing between two of them leaves the next call with no path at all, and
/// <c>devices revoke</c> exits 1. The test is not flaky in the sense of being fragile — it is
/// correct, and it was the most call-per-test-heavy victim of a real defect.</para>
/// </summary>
[CollectionDefinition("auth-store-path", DisableParallelization = true)]
public sealed class OperatorCliCollection;
