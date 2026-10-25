namespace Fleet.WorkflowOutcomeQualification;

/// <summary>
/// Immutable target and operation contract for the connected process qualification driver.
/// Every value is operator-supplied; the driver never discovers or falls back to one.
/// </summary>
public sealed record ProcessQualificationManifest(
    QualificationTarget Target,
    IReadOnlyDictionary<string, HashedFile> Executables,
    IReadOnlyList<HashedFile> ConfigFiles,
    IReadOnlyList<HashedFile> PolicyFiles,
    DockerTarget Docker,
    TemporalTarget Temporal,
    RuntimeTarget Runtime,
    InferenceFixtureTarget Fixture,
    IReadOnlySet<string> AllowedOperations,
    IReadOnlyList<string> Scenarios,
    IReadOnlySet<string> AuthorizedRuntimeOracles,
    CleanupTarget Cleanup,
    int MaxDurationSeconds);

public sealed record QualificationTarget(
    string Registry,
    string ApprovalRef,
    string SourceRef,
    string SourceCommit,
    string SourceTree,
    IReadOnlyDictionary<string, ImageTarget> Images);

public sealed record ImageTarget(string Reference, string Digest, string BuildCommit);

public sealed record HashedFile(string Path, string Sha256);

public sealed record DockerTarget(
    string Endpoint,
    string ConfigFile,
    string Context,
    string EngineId,
    string ApiVersion,
    string ServerVersion);

public sealed record TemporalTarget(
    string Endpoint,
    string Namespace,
    string RunId,
    string TaskQueue,
    string TlsMode,
    string ServerName,
    string IdentitySha256,
    string ServerVersion);

public sealed record RuntimeTarget(
    string SourceDirectory,
    string WorkingDirectory,
    string PathEnvironment,
    string OrchestratorBaseUrl,
    string BrokerEndpoint,
    string DatabaseServer,
    string DatabaseName,
    string ConnectionEnv,
    string SeedSql,
    string SeedSqlSha256,
    Guid ScenarioBatchId,
    string ScenarioNonce,
    Guid ScenarioItemId,
    int ScenarioVersion,
    long ScenarioGeneration,
    int ScenarioAttempt,
    string ExpectedTurnOutputSha256,
    string TerminalHash);

public sealed record InferenceFixtureTarget(
    string CliVersion,
    string Model,
    string Protocol,
    string Endpoint,
    IReadOnlyList<string> ShellArgv,
    IReadOnlyList<string> ShellEnvironment,
    int ShellDeadlineSeconds);

public sealed record CleanupTarget(
    string ProjectName,
    IReadOnlyList<string> Owned,
    bool Verify,
    IReadOnlyList<string> Commands);
