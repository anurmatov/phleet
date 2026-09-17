namespace Fleet.Comms.Tests;

/// <summary>
/// Everything that reads or writes <c>Comms__AuthStorePath</c> runs in one collection, so xUnit
/// never runs two of them at once.
///
/// <para>The environment is process-wide. Two classes setting that variable in parallel had each
/// other's store path resolved underneath them, which showed up as a scatter of unrelated failures
/// — the CLI writing to one database while the assertion read another. Sharing a collection is the
/// fix; giving each class its own variable is not possible, because the point of these tests is
/// that the CLI reads configuration exactly as the service does.</para>
/// </summary>
[CollectionDefinition("auth-store-path")]
public sealed class OperatorCliCollection;
