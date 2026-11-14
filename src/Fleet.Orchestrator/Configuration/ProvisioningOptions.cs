namespace Fleet.Orchestrator.Configuration;

/// <summary>
/// The <c>Provisioning</c> configuration section: where the deployment lives on the host and what
/// every agent container is given. Each key and its default is declared here and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// Read with <see cref="From"/> at the point of use, not bound once: a value is what
/// <see cref="IConfiguration"/> holds at that moment, exactly as the raw reads this replaces, and a
/// missing key takes its default while an empty one stays empty.
/// </para>
/// <para>
/// Not here, on purpose: the advisory size limits (<c>PromptSizeWarnings:*</c>), which are parsed
/// from the raw string so a bad value is a warning and not a crash, and the secret-proxy switch,
/// which <c>SecretProxySettings</c> reads by the same rule as the shell scripts.
/// </para>
/// </remarks>
public sealed class ProvisioningOptions
{
    public const string Section = "Provisioning";

    public const string DefaultEnvFilePath = "/app/deploy/.env";
    public const string DefaultNetworkName = "fleet-net";
    public const string DefaultAgentImage = "fleet:agent";
    public const string DefaultComposeProject = "fleet";
    public const string DefaultRolesDir = "/app/roles";
    public const string DefaultProjectsDir = "/app/projects";

    /// <summary>
    /// The fleet base directory on the Docker host (<c>FLEET_BASE_DIR</c>): workspaces, credentials
    /// and the secret-proxy state live under it, and every relative bind is expanded against it.
    /// Null when not configured; provisioning needs it (<see cref="RequireBaseDir"/>).
    /// </summary>
    public string? BaseDir { get; private init; }

    /// <summary>The deployment's <c>.env</c>, as this process sees it.</summary>
    public string EnvFilePath { get; private init; } = DefaultEnvFilePath;

    /// <summary>The network a newly created agent joins when the request names none.</summary>
    public string DefaultNetwork { get; private init; } = DefaultNetworkName;

    /// <summary>The image of an agent whose row names none.</summary>
    public string AgentImage { get; private init; } = DefaultAgentImage;

    /// <summary>The speech-to-text service every agent is pointed at, or empty for none.</summary>
    public string WhisperServiceUrl { get; private init; } = "";

    /// <summary>The text-to-speech service every agent is pointed at, or empty for none.</summary>
    public string KokoroServiceUrl { get; private init; } = "";

    /// <summary>Docker <c>json-file</c> log rotation for agent containers; null leaves Docker's default.</summary>
    public string? ContainerLogMaxSize { get; private init; }

    /// <inheritdoc cref="ContainerLogMaxSize"/>
    public string? ContainerLogMaxFile { get; private init; }

    /// <summary>The Compose project name, for finding a container Compose created as <c>{project}-{name}-1</c>.</summary>
    public string ComposeProject { get; private init; } = DefaultComposeProject;

    /// <summary>Where the seeder reads role instruction files from.</summary>
    public string RolesDir { get; private init; } = DefaultRolesDir;

    /// <summary>Where the seeder reads project context files from.</summary>
    public string ProjectsDir { get; private init; } = DefaultProjectsDir;

    /// <summary>The seed file for a first run, or null to seed no agents.</summary>
    public string? SeedFilePath { get; private init; }

    /// <summary>The section as configuration holds it now.</summary>
    public static ProvisioningOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        return new ProvisioningOptions
        {
            BaseDir = section[nameof(BaseDir)],
            EnvFilePath = section[nameof(EnvFilePath)] ?? DefaultEnvFilePath,
            DefaultNetwork = section[nameof(DefaultNetwork)] ?? DefaultNetworkName,
            AgentImage = section[nameof(AgentImage)] ?? DefaultAgentImage,
            WhisperServiceUrl = section[nameof(WhisperServiceUrl)] ?? "",
            KokoroServiceUrl = section[nameof(KokoroServiceUrl)] ?? "",
            ContainerLogMaxSize = section[nameof(ContainerLogMaxSize)],
            ContainerLogMaxFile = section[nameof(ContainerLogMaxFile)],
            ComposeProject = section[nameof(ComposeProject)] ?? DefaultComposeProject,
            RolesDir = section[nameof(RolesDir)] ?? DefaultRolesDir,
            ProjectsDir = section[nameof(ProjectsDir)] ?? DefaultProjectsDir,
            SeedFilePath = section[nameof(SeedFilePath)],
        };
    }

    /// <summary>The base directory, for code that cannot work without one.</summary>
    /// <exception cref="InvalidOperationException">It is not configured.</exception>
    public string RequireBaseDir() =>
        BaseDir ?? throw new InvalidOperationException("Provisioning:BaseDir is required but not configured.");

    /// <summary>
    /// What makes this section unusable for provisioning, or null. Checked once at startup, so a
    /// deployment that cannot provision says so when it starts and not at its first provision.
    /// </summary>
    public string? DescribeFault()
    {
        if (string.IsNullOrWhiteSpace(BaseDir))
            return "Provisioning:BaseDir is not set. It is the fleet base directory on the Docker host "
                 + "(Provisioning__BaseDir, FLEET_BASE_DIR in the compose file); no agent can be provisioned without it.";
        if (string.IsNullOrWhiteSpace(EnvFilePath))
            return "Provisioning:EnvFilePath is blank. It is the path of the deployment's .env as the orchestrator sees it "
                 + $"(Provisioning__EnvFilePath); leave it unset for {DefaultEnvFilePath}.";
        return null;
    }
}
