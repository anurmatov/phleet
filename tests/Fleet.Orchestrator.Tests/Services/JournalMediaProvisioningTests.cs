using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// <c>Journal:MediaEnabled</c> is derived from the deployment's <c>.env</c>, and a fresh install
/// that said yes to media must actually turn the agent's uploader on (#388).
/// </summary>
/// <remarks>
/// <para>
/// The flag gates the agent half of the media plane. Comms can accept uploads all day and the agent
/// will still journal every attachment as <c>not_archived(media_disabled)</c> if the flag is off,
/// so the failure mode is silent and permanent: photos are declined on messages that were journaled
/// correctly, and nothing ever reports it.
/// </para>
/// <para>
/// ⚠️ These tests read a REAL env file through the same code path provisioning uses, rather than
/// passing a boolean to a helper. The bug this suite exists for was exactly a mismatch between two
/// places that each had a piece of the answer: the gate asked for a key that <c>setup.sh> never
/// writes and that compose defaults on its own, so a host that opted into media provisioned every
/// agent with the flag off. A unit test on the boolean could not have seen that.
/// </para>
/// </remarks>
public sealed class JournalMediaProvisioningTests : IDisposable
{
    private readonly string _envFile = Path.Combine(
        Path.GetTempPath(), $"fleet-env-{Guid.NewGuid():N}.env");

    public void Dispose()
    {
        if (File.Exists(_envFile)) File.Delete(_envFile);
    }

    // ── the emitted key ───────────────────────────────────────────────────────

    [Fact]
    public void An_agent_with_media_on_gets_Journal_MediaEnabled_true()
    {
        var json = ContainerProvisioningService.GenerateAppsettingsJson(
            AgentWithJournal(), "acto",
            journal: new JournalProvisioning("ingest-token", [], null, MediaEnabled: true));

        Assert.True(HasMediaKey(json));
    }

    /// <summary>
    /// The one-write rollback: with media off the key is ABSENT rather than false, so an agent in a
    /// deployment with no bucket gets the exact bytes it produced before media existed.
    /// </summary>
    [Fact]
    public void An_agent_with_media_off_gets_no_media_key_at_all()
    {
        var json = ContainerProvisioningService.GenerateAppsettingsJson(
            AgentWithJournal(), "acto",
            journal: new JournalProvisioning("ingest-token", [], null, MediaEnabled: false));

        Assert.False(
            JsonDocument.Parse(json).RootElement.TryGetProperty("Journal", out var journal)
            && journal.TryGetProperty("MediaEnabled", out _));
    }

    // ── the gate, read from the deployment's .env ─────────────────────────────

    /// <summary>
    /// THE fresh-install case. <c>setup.sh</c> writes only an endpoint when the operator says yes to
    /// media; the bucket name is defaulted by compose and never appears in <c>.env</c>. Gating on
    /// both keys made every install that opted in provision the agent with media OFF, which is the
    /// bug the review caught.
    /// </summary>
    [Fact]
    public void A_fresh_install_that_wrote_only_the_endpoint_turns_media_on()
    {
        WriteEnv("""
            FLEET_COMMS_JOURNAL_ENABLED=true
            FLEET_COMMS_MEDIA_ENDPOINT=http://comms-minio:9000
            """);

        Assert.True(Service().MediaEndpointIsConfigured());
    }

    /// <summary>
    /// Declining media writes a BLANK endpoint rather than a <c>false</c>, so the blank must read as
    /// off. A gate that treated "present" as "on" would turn media on for every host that declined.
    /// </summary>
    [Fact]
    public void A_blank_endpoint_is_media_off()
    {
        WriteEnv("FLEET_COMMS_MEDIA_ENDPOINT=");
        Assert.False(Service().MediaEndpointIsConfigured());
    }

    /// <summary>Never asked is the third state: the line is commented out in <c>.env.example</c>.</summary>
    [Fact]
    public void A_commented_endpoint_is_media_off()
    {
        WriteEnv("#FLEET_COMMS_MEDIA_ENDPOINT=http://comms-minio:9000");
        Assert.False(Service().MediaEndpointIsConfigured());
    }

    /// <summary>Whitespace is not a value; a stray <c>KEY= </c> must not start a media plane.</summary>
    [Fact]
    public void A_whitespace_endpoint_is_media_off()
    {
        WriteEnv("FLEET_COMMS_MEDIA_ENDPOINT=   ");
        Assert.False(Service().MediaEndpointIsConfigured());
    }

    /// <summary>
    /// No env file at all — a host whose provisioning path is wrong — is off, not an exception.
    /// Media is opt-in, and an unreadable file must not silently enable a bucket.
    /// </summary>
    [Fact]
    public void A_missing_env_file_is_media_off()
    {
        Assert.False(Service(envFile: Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.env"))
            .MediaEndpointIsConfigured());
    }

    /// <summary>
    /// The two halves of the switch cannot disagree: media is on for the agent exactly when the
    /// key would be written, which is the property the reviewer asked to be shown.
    /// </summary>
    [Theory]
    [InlineData("FLEET_COMMS_MEDIA_ENDPOINT=http://comms-minio:9000", true)]
    [InlineData("FLEET_COMMS_MEDIA_ENDPOINT=", false)]
    [InlineData("#FLEET_COMMS_MEDIA_ENDPOINT=http://comms-minio:9000", false)]
    public void The_gate_and_the_emitted_key_agree(string envLine, bool expected)
    {
        WriteEnv(envLine);
        var service = Service();

        Assert.Equal(expected, service.MediaEndpointIsConfigured());

        var json = ContainerProvisioningService.GenerateAppsettingsJson(
            AgentWithJournal(), "acto",
            journal: new JournalProvisioning("ingest-token", [], null, service.MediaEndpointIsConfigured()));

        // `true` is written; `false` is ABSENT (the one-write rollback), so the assertion is on
        // presence rather than on a value that never appears.
        Assert.Equal(expected, HasMediaKey(json));
    }

    // ── rig ───────────────────────────────────────────────────────────────────

    private void WriteEnv(string content) => File.WriteAllText(_envFile, content);

    private ContainerProvisioningService Service(string? envFile = null)
    {
        // The real config object, with only the two keys the path under test reads. `Docker:SocketPath`
        // points nowhere: nothing here talks to Docker, and DockerService must not open a socket to
        // the runner's real daemon from a unit test.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provisioning:EnvFilePath"] = envFile ?? _envFile,
            ["Journal:TokenKey"] = Convert.ToBase64String(new byte[32]),
            ["Docker:SocketPath"] = Path.Combine(Path.GetTempPath(), $"absent-sock-{Guid.NewGuid():N}"),
        }).Build();

        return new ContainerProvisioningService(
            new StubScopeFactory(),
            new DockerService(NullLogger<DockerService>.Instance, configuration),
            configuration,
            new JournalTokenService(configuration),
            NullLogger<ContainerProvisioningService>.Instance);
    }

    private static Agent AgentWithJournal() => new()
    {
        Name = "agent1",
        DisplayName = "Agent One",
        Role = "developer",
        Model = "model-x",
        ContainerName = "ctr-agent1",
        Provider = "claude",
        JournalEnabled = true,
    };

    /// <summary>Whether the generated config carries <c>Journal:MediaEnabled</c>, and with what value.</summary>
    private static bool HasMediaKey(string json)
    {
        if (!JsonDocument.Parse(json).RootElement.TryGetProperty("Journal", out var journal))
            return false;

        if (!journal.TryGetProperty("MediaEnabled", out var value)) return false;

        Assert.True(value.GetBoolean(), "the key is written only when media is on");
        return true;
    }

    /// <summary>Provisioning never touches the DB in these paths; the scope factory is unused.</summary>
    private sealed class StubScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("not used by these tests");
    }
}
