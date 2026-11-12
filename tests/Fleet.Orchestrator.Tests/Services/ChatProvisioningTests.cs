using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.Extensions.Configuration;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// What Chat writes into an agent's generated settings (#530). On by default; never a reason to
/// refuse provisioning.
/// </summary>
public class ChatProvisioningTests
{
    private static readonly string Key = Convert.ToBase64String(new byte[32]).TrimEnd('=');

    private static JournalTokenService Tokens(string? key) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Journal:TokenKey"] = key }).Build());

    private static Agent AgentNamed(string name, bool chatEnabled) => new()
    {
        Name = name,
        DisplayName = name,
        Role = "developer",
        Model = "example-model",
        Provider = "claude",
        ContainerName = $"fleet-{name}",
        ChatEnabled = chatEnabled,
    };

    [Fact]
    public void AnAgentWithChatOff_GetsNoSection_AndTheSameBytesAsBefore()
    {
        var agent = AgentNamed("builder", chatEnabled: false);

        Assert.Null(ChatAgents.BuildProvisioning(agent, Tokens(Key), southUrl: null, primaryHumanUserId: 42));
        Assert.Equal(
            ContainerProvisioningService.GenerateAppsettingsJson(agent, ""),
            ContainerProvisioningService.GenerateAppsettingsJson(agent, "", chat: null));
        Assert.Null(JsonNode.Parse(ContainerProvisioningService.GenerateAppsettingsJson(agent, ""))!["Conversations"]);
    }

    [Fact]
    public void AnAgentWithChatOn_GetsTheListenerAddress_AndATokenThatNamesIt()
    {
        var tokens = Tokens(Key);
        var agent = AgentNamed("builder", chatEnabled: true);

        var chat = ChatAgents.BuildProvisioning(agent, tokens, southUrl: null, primaryHumanUserId: null)!;
        var section = JsonNode.Parse(ContainerProvisioningService.GenerateAppsettingsJson(agent, "", chat: chat))!["Conversations"]!;

        Assert.Equal(ChatAgents.DefaultSouthUrl, section["SouthBaseUrl"]!.GetValue<string>());
        var token = section["SouthBearerToken"]!.GetValue<string>();
        Assert.True(tokens.TryVerify(token, SignIn.AgentTokenPurpose, out var subject));
        Assert.Equal("builder", subject);
        // No primary human is named on this deployment, so no principal is singled out.
        Assert.Null(section["PrimaryHumanPrincipalId"]);
        // It names no agent and no broker: the agent already holds both.
        Assert.Equal(["SouthBaseUrl", "SouthBearerToken"], section.AsObject().Select(p => p.Key).ToArray());
    }

    [Fact]
    public void EachAgentsTokenIsItsOwn_AndIsNotAJournalToken()
    {
        var tokens = Tokens(Key);
        var builder = ChatAgents.BuildProvisioning(AgentNamed("builder", true), tokens, null, null)!.SouthToken;
        var reviewer = ChatAgents.BuildProvisioning(AgentNamed("reviewer", true), tokens, null, null)!.SouthToken;

        Assert.NotEqual(builder, reviewer);
        Assert.False(tokens.TryVerify(builder, JournalTokenService.PurposeIngest, out _));
        Assert.False(tokens.TryVerify(builder, JournalTokenService.PurposeRead, out _));
        Assert.False(tokens.TryVerify(tokens.Mint(JournalTokenService.PurposeIngest, "builder"), SignIn.AgentTokenPurpose, out _));
    }

    [Fact]
    public void WhenTheDeploymentNamesAPrimaryHuman_TheOwnerPrincipalIsThatPerson()
    {
        var chat = ChatAgents.BuildProvisioning(AgentNamed("builder", true), Tokens(Key), null, primaryHumanUserId: 42)!;
        var section = JsonNode.Parse(
            ContainerProvisioningService.GenerateAppsettingsJson(AgentNamed("builder", true), "", chat: chat))!["Conversations"]!;

        Assert.Equal("owner", section["PrimaryHumanPrincipalId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, ChatAgents.DefaultSouthUrl)]
    [InlineData("  ", ChatAgents.DefaultSouthUrl)]
    [InlineData("http://chat.example.test:9000/", "http://chat.example.test:9000")]
    public void TheListenerAddressComesFromConfiguration_WithADefault(string? configured, string expected) =>
        Assert.Equal(expected, ChatAgents.BuildProvisioning(AgentNamed("builder", true), Tokens(Key), configured, null)!.SouthBaseUrl);

    [Fact]
    public void ChatIsOnByDefault_ForANewAgent() => Assert.True(new Agent
    {
        Name = "builder", DisplayName = "builder", Role = "developer", Model = "example-model",
        ContainerName = "fleet-builder",
    }.ChatEnabled);

    [Theory]
    [InlineData("builder", "valid", true)]
    [InlineData("Builder_2-b", "valid", true)]
    // No chat service on this deployment: the agent simply has no chat.
    [InlineData("builder", "missing", false)]
    [InlineData("builder", "invalid", false)]
    // A name is the routing key, the queue segment and the token subject. A dot or a space is none of those.
    [InlineData("build.er", "valid", false)]
    [InlineData("the builder", "valid", false)]
    [InlineData("", "valid", false)]
    public void ChatApplies_OnlyWhereTheDeploymentAndTheNameCanCarryIt(string name, string key, bool applies)
    {
        var tokens = Tokens(key switch { "valid" => Key, "invalid" => "invalid", _ => null });
        var agent = AgentNamed(name, chatEnabled: true);

        Assert.Equal(applies, ChatAgents.IsEffective(agent, tokens));
        Assert.Equal(applies, ChatAgents.BuildProvisioning(agent, tokens, null, null) is not null);

        // Switched off, it never applies.
        Assert.False(ChatAgents.IsEffective(AgentNamed(name, chatEnabled: false), tokens));
    }

    [Fact]
    public void ChatIsAvailable_ExactlyWhenTheSharedKeyIsUsable()
    {
        Assert.True(ChatAgents.IsAvailable(Tokens(Key)));
        Assert.False(ChatAgents.IsAvailable(Tokens(null)));
        Assert.False(ChatAgents.IsAvailable(Tokens("invalid")));
    }
}
