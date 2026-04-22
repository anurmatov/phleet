using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Shared;

/// <summary>
/// Shared peer-config client used by fleet-bridge, fleet-telegram, and fleet-temporal-bridge.
///
/// On startup each peer reads its <c>PEER_CONFIG_KEYS</c> (comma-separated raw .env key names)
/// and optional <c>PEER_AGENT_DERIVED_KEYS</c> (comma-separated templates containing {SHORTNAME})
/// from its own environment, then fetches values from <c>GET /api/config/values?keys=…</c>
/// on the orchestrator with exponential backoff retry.
///
/// After bootstrap the client subscribes to <c>config.changed</c> events on the
/// <c>fleet.orchestrator</c> RabbitMQ topic exchange and self-filters: if any changedKey
/// matches a literal subscription or a template regex, the client refetches its slice
/// and invokes the caller-supplied <see cref="OnChanged"/> callback.
///
/// Peer retry: 1 s → 2 s → 4 s → 8 s → 16 s → 30 s (cap). After 5 minutes of total wait
/// the client logs an error and exits non-zero by throwing <see cref="PeerConfigBootstrapException"/>.
/// </summary>
public sealed class PeerConfigClient : IAsyncDisposable
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30),
    ];
    private static readonly TimeSpan BootstrapTimeout = TimeSpan.FromMinutes(5);

    // ── Config ────────────────────────────────────────────────────────────────

    private readonly string _orchestratorUrl;
    private readonly string _configToken;
    private readonly string _rabbitHost;
    private readonly string _rabbitExchange;
    private readonly HashSet<string> _literalKeys;
    private readonly List<(string Template, Regex Rx)> _templateMatchers;
    private readonly ILogger _logger;

    /// <summary>
    /// Invoked whenever the client refetches config after a matching <c>config.changed</c> event.
    /// Also invoked once after the initial bootstrap fetch succeeds.
    /// </summary>
    public Func<ConfigSnapshot, Task>? OnChanged { get; set; }

    // ── State ─────────────────────────────────────────────────────────────────

    private ConfigSnapshot _latest = new([], []);
    private IConnection? _rabbitConn;
    private IChannel? _rabbitChannel;

    // Reused across all FetchAsync calls to avoid socket exhaustion under rapid config.changed bursts.
    private readonly HttpClient _httpClient;

    public PeerConfigClient(
        string orchestratorUrl,
        string configToken,
        string rabbitHost,
        string rabbitExchange,
        IEnumerable<string> literalKeys,
        IEnumerable<string> templateKeys,
        ILogger logger)
    {
        _orchestratorUrl = orchestratorUrl.TrimEnd('/');
        _configToken = configToken;
        _rabbitHost = rabbitHost;
        _rabbitExchange = rabbitExchange;
        _literalKeys = new HashSet<string>(literalKeys, StringComparer.OrdinalIgnoreCase);
        _logger = logger;

        _templateMatchers = templateKeys
            .Select(t => (t, BuildTemplateRegex(t)))
            .ToList();

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!string.IsNullOrEmpty(configToken))
            _httpClient.DefaultRequestHeaders.Authorization = new("Bearer", configToken);
    }

    /// <summary>
    /// Factory: reads PEER_CONFIG_KEYS, PEER_AGENT_DERIVED_KEYS, and connection settings from
    /// the process environment.
    /// </summary>
    public static PeerConfigClient FromEnvironment(ILogger logger)
    {
        var orchestratorUrl = Env("ORCHESTRATOR_URL", "http://fleet-orchestrator:3600");
        var configToken = Env("ORCHESTRATOR_CONFIG_TOKEN", "");
        var rabbitHost = Env("RABBITMQ_HOST", "") is { Length: > 0 } h ? h
            : ExtractRabbitHost(Env("RABBITMQ_URL", "amqp://rabbitmq:5672/"));
        var rabbitExchange = Env("RABBITMQ_EXCHANGE", "fleet.orchestrator");

        var literalKeys = Env("PEER_CONFIG_KEYS", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var templateKeys = Env("PEER_AGENT_DERIVED_KEYS", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new PeerConfigClient(
            orchestratorUrl, configToken, rabbitHost, rabbitExchange,
            literalKeys, templateKeys, logger);
    }

    private static string Env(string key, string @default) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : @default;

    private static string ExtractRabbitHost(string amqpUrl)
    {
        try { return new Uri(amqpUrl).Host; }
        catch { return "rabbitmq"; }
    }

    // ── Bootstrap ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches config from the orchestrator with retry-backoff.
    /// Throws <see cref="PeerConfigBootstrapException"/> if the orchestrator is unreachable
    /// after <see cref="BootstrapTimeout"/>.
    /// </summary>
    public async Task BootstrapAsync(CancellationToken ct = default)
    {
        if (_literalKeys.Count == 0 && _templateMatchers.Count == 0)
        {
            _logger.LogInformation("PEER_CONFIG_KEYS is empty — skipping orchestrator config fetch");
            return;
        }

        var deadline = DateTime.UtcNow + BootstrapTimeout;
        var attempt = 0;

        while (true)
        {
            try
            {
                var snapshot = await FetchAsync(ct);
                _latest = snapshot;
                if (OnChanged is not null)
                    await OnChanged(snapshot);
                _logger.LogInformation("PeerConfigClient bootstrapped ({Literals} literals, {Templates} templates)",
                    snapshot.Literals.Count, snapshot.AgentDerived.Count);
                break;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                if (DateTime.UtcNow >= deadline)
                    throw new PeerConfigBootstrapException(
                        "Orchestrator config API unreachable after 5 minutes — cannot start", ex);

                var delay = attempt < RetryDelays.Length
                    ? RetryDelays[attempt]
                    : RetryDelays[^1];

                _logger.LogWarning(ex,
                    "Config fetch attempt {Attempt} failed — retrying in {Delay}s",
                    attempt + 1, delay.TotalSeconds);

                await Task.Delay(delay, ct);
                attempt++;
            }
        }
    }

    // ── RabbitMQ subscription ─────────────────────────────────────────────────

    /// <summary>
    /// Subscribes to <c>config.changed</c> on the <c>fleet.orchestrator</c> exchange.
    /// Call after <see cref="BootstrapAsync"/> returns.
    /// </summary>
    public async Task SubscribeAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_rabbitHost))
        {
            _logger.LogWarning("RabbitMQ not configured — config.changed subscription disabled");
            return;
        }

        var factory = new ConnectionFactory
        {
            HostName = _rabbitHost,
            ClientProvidedName = "fleet-peer-config-sub",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
        };

        _rabbitConn = await RabbitMqConnectionHelper.ConnectWithRetryAsync(factory, _logger, ct);
        _rabbitChannel = await _rabbitConn.CreateChannelAsync(cancellationToken: ct);

        await _rabbitChannel.ExchangeDeclareAsync(
            _rabbitExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: ct);

        // Exclusive queue per container (auto-deleted when disconnected)
        var queueName = $"fleet.peer-config.{Guid.NewGuid():N}";
        await _rabbitChannel.QueueDeclareAsync(
            queue: queueName,
            durable: false,
            exclusive: true,
            autoDelete: true,
            cancellationToken: ct);

        await _rabbitChannel.QueueBindAsync(
            queueName, _rabbitExchange, routingKey: "config.changed", cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(_rabbitChannel);
        consumer.ReceivedAsync += OnConfigChangedReceived;
        await _rabbitChannel.BasicConsumeAsync(
            queueName, autoAck: true, consumer: consumer, cancellationToken: ct);

        _logger.LogInformation("PeerConfigClient subscribed to config.changed on {Exchange}", _rabbitExchange);
    }

    // ── Event handling ────────────────────────────────────────────────────────

    private async Task OnConfigChangedReceived(object _, BasicDeliverEventArgs ea)
    {
        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            using var doc = JsonDocument.Parse(json);
            var changedKeys = doc.RootElement
                .GetProperty("changedKeys")
                .EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .ToList();

            if (!Matches(changedKeys))
            {
                _logger.LogDebug("config.changed received — no matching keys for this peer");
                return;
            }

            _logger.LogInformation("config.changed matches subscription — refetching");
            var snapshot = await FetchAsync(CancellationToken.None);
            _latest = snapshot;
            if (OnChanged is not null)
                await OnChanged(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling config.changed event");
        }
    }

    private bool Matches(IEnumerable<string> changedKeys)
    {
        foreach (var key in changedKeys)
        {
            if (_literalKeys.Contains(key))
                return true;
            foreach (var (_, rx) in _templateMatchers)
                if (rx.IsMatch(key))
                    return true;
        }
        return false;
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────

    private async Task<ConfigSnapshot> FetchAsync(CancellationToken ct)
    {
        var allKeys = _literalKeys
            .Concat(_templateMatchers.Select(t => t.Template))
            .ToList();

        if (allKeys.Count == 0)
            return new ConfigSnapshot([], []);

        var keysParam = string.Join(",", allKeys.Select(Uri.EscapeDataString));
        var url = $"{_orchestratorUrl}/api/config/values?keys={keysParam}";

        var resp = await _httpClient.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<ConfigValuesDto>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct)
            ?? throw new InvalidOperationException("Empty response from /api/config/values");

        return new ConfigSnapshot(
            body.Literals ?? [],
            body.AgentDerived ?? []);
    }

    // ── Accessors ─────────────────────────────────────────────────────────────

    public string? GetLiteral(string key) =>
        _latest.Literals.TryGetValue(key, out var v) ? v : null;

    public Dictionary<string, string> GetAgentDerived(string template) =>
        _latest.AgentDerived.TryGetValue(template, out var d) ? d : [];

    public ConfigSnapshot Latest => _latest;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Regex BuildTemplateRegex(string template)
    {
        const string placeholder = "{SHORTNAME}";
        var escaped = Regex.Escape(template).Replace(
            Regex.Escape(placeholder), @"[^_]+", StringComparison.OrdinalIgnoreCase);
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public async ValueTask DisposeAsync()
    {
        if (_rabbitChannel is not null)
            await _rabbitChannel.DisposeAsync();
        if (_rabbitConn is not null)
            await _rabbitConn.DisposeAsync();
        _httpClient.Dispose();
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public sealed record ConfigSnapshot(
    Dictionary<string, string> Literals,
    Dictionary<string, Dictionary<string, string>> AgentDerived);

internal sealed class ConfigValuesDto
{
    public Dictionary<string, string>? Literals { get; set; }
    public Dictionary<string, Dictionary<string, string>>? AgentDerived { get; set; }
}

public sealed class PeerConfigBootstrapException(string message, Exception? inner = null)
    : Exception(message, inner);
