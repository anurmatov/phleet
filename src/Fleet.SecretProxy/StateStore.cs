using System.Net;
using Fleet.Shared;

namespace Fleet.SecretProxy;

/// <summary>One parsed state file, indexed for the request path.</summary>
internal sealed class StateSnapshot
{
    private readonly Dictionary<string, SecretProxyBinding> _bindings;
    private readonly Dictionary<string, SecretProxyGrant> _grantsByHash;
    private readonly HashSet<string> _hosts;

    /// <summary>When the orchestrator wrote this state.</summary>
    public DateTimeOffset GeneratedAtUtc { get; }

    /// <summary>The bindings and grants without the timestamp: equal for two writes of the same state.</summary>
    public string Content { get; }

    public int BindingCount { get; }
    public int GrantCount { get; }

    public StateSnapshot(SecretProxyState state)
    {
        GeneratedAtUtc = state.GeneratedAtUtc;
        Content = (state with { GeneratedAtUtc = default }).Serialize();
        BindingCount = state.Bindings.Count;
        GrantCount = state.Grants.Count;
        _bindings = state.Bindings.ToDictionary(b => b.Key, StringComparer.Ordinal);
        _hosts = state.Bindings.SelectMany(b => b.Hosts).ToHashSet(StringComparer.Ordinal);

        // Two grants cannot share a placeholder: 256 random bits. If the file says they do, it is
        // not a state this build wrote, and neither grant is honoured.
        _grantsByHash = state.Grants
            .GroupBy(g => g.PlaceholderSha256, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
    }

    public bool IsBoundHost(string host) => _hosts.Contains(host);

    public SecretProxyGrant? FindGrant(string placeholderSha256) =>
        _grantsByHash.GetValueOrDefault(placeholderSha256);

    public SecretProxyBinding? FindBinding(string key) => _bindings.GetValueOrDefault(key);

    /// <summary>True when the grant's fixed address is the address the request came from.</summary>
    public static bool SourceMatches(SecretProxyGrant grant, IPAddress? remote)
    {
        if (remote is null || !IPAddress.TryParse(grant.SourceIp, out var expected))
            return false;
        if (remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();
        return expected.Equals(remote);
    }
}

/// <summary>
/// Reads the orchestrator's state file on every request and re-parses it whenever its bytes change, so
/// a grant the orchestrator removed is gone on the very next request.
/// </summary>
/// <remarks>
/// <para>
/// It compares content, not timestamps: two writes inside one timestamp tick, of the same length, are
/// still two states. The file is small and replaced atomically, so a read sees one whole version.
/// </para>
/// <para>
/// It fails closed. A file that is missing, unreadable, invalid or older than
/// <see cref="SecretProxyOptions.MaxStateAgeSeconds"/> yields no snapshot, and with no snapshot the
/// forwarder refuses every request. The age bound matters because the file is the only channel a
/// revocation has: if the orchestrator can no longer replace or remove it, the grants in it must still
/// stop working. Age is checked on every request, against the time written inside the file.
/// </para>
/// </remarks>
internal sealed class StateStore(SecretProxyOptions options, ILogger<StateStore> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _maxAge = TimeSpan.FromSeconds(options.MaxStateAgeSeconds);
    private readonly object _lock = new();
    private byte[]? _bytes;
    private StateSnapshot? _snapshot;
    private string? _parseFault = "state_missing";
    private string? _fault = "state_missing";
    private string? _loggedFault;
    private string? _loggedContent;

    /// <summary>The fixed fault code of the current state, or null when a usable snapshot is loaded.</summary>
    public string? Fault
    {
        get
        {
            _ = Current;
            lock (_lock) return _fault;
        }
    }

    public StateSnapshot? Current
    {
        get
        {
            byte[]? bytes = null;
            var readFault = "state_missing";
            try
            {
                bytes = File.ReadAllBytes(options.StateFile);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Missing: refused below.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                readFault = "state_unreadable";
            }

            lock (_lock)
            {
                if (bytes is null)
                {
                    _bytes = null;
                    _snapshot = null;
                    _parseFault = readFault;
                }
                else if (_bytes is null || !bytes.AsSpan().SequenceEqual(_bytes))
                {
                    _bytes = bytes;
                    var state = SecretProxyState.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out var fault);
                    _snapshot = state is null ? null : new StateSnapshot(state);
                    _parseFault = fault;
                }

                // Judged now, not when the file was read: an unchanged file grows stale by itself.
                var usable = _snapshot is not null && _time.GetUtcNow() - _snapshot.GeneratedAtUtc <= _maxAge
                    ? _snapshot
                    : null;
                _fault = _snapshot is null ? _parseFault : usable is null ? "state_stale" : null;

                if (_fault != _loggedFault)
                {
                    _loggedFault = _fault;
                    if (_fault is not null)
                        logger.LogError("SecretProxy state unavailable ({Fault}); every request is refused", _fault);
                }
                // The file is rewritten every few seconds to stay fresh; say so only when what it grants changed.
                if (usable is not null && usable.Content != _loggedContent)
                {
                    _loggedContent = usable.Content;
                    logger.LogInformation(
                        "SecretProxy state loaded: bindings={Bindings} grants={Grants}", usable.BindingCount, usable.GrantCount);
                }

                return usable;
            }
        }
    }
}
