using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Temporalio.Testing;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>The broker and the orchestrator, recording what reaches them.</summary>
internal sealed class RecordingWire
{
    private readonly List<(string RoutingKey, string Body)> _published = [];
    private readonly List<string> _cancelUrls = [];

    public IConnectionFactory Broker { get; }
    public IHttpClientFactory Http { get; }

    /// <summary>What the orchestrator answers a cancel with. Null: it cannot be reached.</summary>
    public Func<HttpResponseMessage>? OrchestratorAnswers { get; set; } =
        () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

    public IReadOnlyList<(string RoutingKey, string Body)> Published
    {
        get { lock (_published) return [.. _published]; }
    }

    public IReadOnlyList<string> CancelUrls
    {
        get { lock (_cancelUrls) return [.. _cancelUrls]; }
    }

    public RecordingWire()
    {
        var channel = Substitute.For<IChannel>();
        channel.BasicPublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (_published)
                    _published.Add((call.ArgAt<string>(1), Encoding.UTF8.GetString(call.ArgAt<ReadOnlyMemory<byte>>(4).Span)));
                return ValueTask.CompletedTask;
            });
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(channel));
        Broker = Substitute.For<IConnectionFactory>();
        Broker.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(connection));

        Http = Substitute.For<IHttpClientFactory>();
        Http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new Orchestrator(this)));
    }

    public DelegationRelay Relay(string orchestratorUrl = "http://orchestrator.invalid") => new(
        Options.Create(new RabbitMqOptions()),
        Options.Create(new TemporalBridgeOptions { OrchestratorUrl = orchestratorUrl }),
        Broker, Http, NullLogger<DelegationRelay>.Instance);

    private sealed class Orchestrator(RecordingWire wire) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (wire._cancelUrls) wire._cancelUrls.Add(request.RequestUri!.AbsoluteUri);
            return wire.OrchestratorAnswers is { } answer
                ? Task.FromResult(answer())
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"));
        }
    }
}
