using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.Extensions.Configuration;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// What turning Chat on for an agent writes into its generated settings (#530), and what it
/// refuses.
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

    [Theory]
    [InlineData("builder", "valid", null)]
    [InlineData("Builder_2-b", "valid", null)]
    [InlineData("builder", "missing", "chat_key_missing")]
    [InlineData("builder", "invalid", "chat_key_invalid")]
    // A name is the routing key, the queue segment and the token subject. A dot or a space is none of those.
    [InlineData("build.er", "valid", "chat_agent_name_invalid")]
    [InlineData("the builder", "valid", "chat_agent_name_invalid")]
    [InlineData("", "valid", "chat_agent_name_invalid")]
    public void TurningChatOn_IsRefusedByNameForWhatItCannotWorkWith(string name, string key, string? fault)
    {
        var tokens = Tokens(key switch { "valid" => Key, "invalid" => "invalid", _ => null });

        Assert.Equal(fault, ChatAgents.DescribeEnableFault(AgentNamed(name, chatEnabled: false), tokens));
        Assert.Equal(fault, ChatAgents.DescribeProvisioningFault(AgentNamed(name, chatEnabled: true), tokens));
        // An agent that never opted in is never refused for it.
        Assert.Null(ChatAgents.DescribeProvisioningFault(AgentNamed(name, chatEnabled: false), tokens));
    }

    [Fact]
    public void ChatIsAvailable_ExactlyWhenTheSharedKeyIsUsable()
    {
        Assert.True(ChatAgents.IsAvailable(Tokens(Key)));
        Assert.False(ChatAgents.IsAvailable(Tokens(null)));
        Assert.False(ChatAgents.IsAvailable(Tokens("invalid")));
    }
}
