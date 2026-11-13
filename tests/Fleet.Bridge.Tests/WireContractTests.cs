using System.Reflection;
using System.Text;
using System.Text.Json;
using Fleet.Bridge.Configuration;
using Fleet.Bridge.Services;
using Fleet.WireFixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Bridge.Tests;

/// <summary>
/// What fleet-bridge puts on the broker for the CTO agent and what it accepts back, held to the
/// samples under <c>tests/fixtures/wire/</c>. The agent's end is tested against the same files in
/// <c>Fleet.Agent.Tests</c>.
/// </summary>
public sealed class WireContractTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_question_is_published_as_its_sample_and_answered_by_the_agents_reply()
    {
        var (relay, sent) = ConnectedRelay();
        await using var _ = relay;

        var asking = relay.AskAsync("what now?", "reader", "docs", timeoutSeconds: 5, CancellationToken.None);
        var request = await FirstAsync(sent);

        Assert.Equal("fleet.group", request.Exchange);
        Assert.Equal("co-cto", request.RoutingKey);
        Assert.Equal(DeliveryModes.Persistent, request.Properties.DeliveryMode);
        WireGolden.AssertMatches("relay.bridge.bridge-request.json", request.Body);

        // The agent answers with the correlation id it was sent.
        using var published = JsonDocument.Parse(request.Body);
        var correlationId = published.RootElement.GetProperty("CorrelationId").GetString()!;
        var reply = WireGolden.Sample("relay.agent.bridge-response.json").Replace(WireGolden.SampleId, correlationId);
        await DeliverAsync(relay, reply);

        Assert.Equal("the text", await asking.WaitAsync(Wait));
    }

    [Fact]
    public async Task A_status_check_is_published_as_its_sample_and_answered_by_the_agents_status()
    {
        var (relay, sent) = ConnectedRelay();
        await using var _ = relay;

        var checking = relay.CheckStatusAsync(CancellationToken.None);
        var request = await FirstAsync(sent);

        Assert.Equal("fleet.group", request.Exchange);
        Assert.Equal("co-cto", request.RoutingKey);
        WireGolden.AssertMatches("relay.bridge.status-check.json", request.Body);

        await DeliverAsync(relay, WireGolden.Sample("relay.agent.status-response.json"));

        Assert.Equal("the text", await checking.WaitAsync(Wait));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Sent(string Exchange, string RoutingKey, BasicProperties Properties, byte[] Body);

    /// <summary>A relay with a publish channel in place; its start-up path opens a real connection.</summary>
    private static (BridgeRelayService Relay, List<Sent> Sent) ConnectedRelay()
    {
        var channel = Substitute.For<IChannel>();
        var sent = new List<Sent>();
        channel.BasicPublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (sent)
                    sent.Add(new Sent(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<BasicProperties>(3),
                        call.ArgAt<ReadOnlyMemory<byte>>(4).ToArray()));
                return ValueTask.CompletedTask;
            });

        var relay = new BridgeRelayService(
            Options.Create(new BridgeOptions { BridgeChatId = -100123, TargetAgent = "Co-Cto" }),
            Options.Create(new RabbitMqOptions { Host = "broker" }),
            NullLogger<BridgeRelayService>.Instance);
        SetField(relay, "_publishChannel", channel);
        SetField(relay, "_initialized", true);
        return (relay, sent);
    }

    private static async Task<Sent> FirstAsync(List<Sent> sent)
    {
        using var cts = new CancellationTokenSource(Wait);
        while (true)
        {
            lock (sent)
                if (sent.Count > 0) return sent[0];
            await Task.Delay(10, cts.Token);
        }
    }

    private static Task DeliverAsync(BridgeRelayService relay, string json) =>
        (Task)typeof(BridgeRelayService)
            .GetMethod("OnMessageReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(relay, [relay, new BasicDeliverEventArgs(
                consumerTag: "tag", deliveryTag: 1, redelivered: false, exchange: "fleet.group", routingKey: "bridge",
                properties: new BasicProperties(), body: Encoding.UTF8.GetBytes(json))])!;

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
}
