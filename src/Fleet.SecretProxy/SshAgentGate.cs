using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Fleet.Shared;

namespace Fleet.SecretProxy;

/// <summary>
/// Lets a granted agent use a bound SSH key without holding it. For each agent with an SSH grant it
/// keeps one socket in that agent's own directory (the only thing the agent's container mounts) and,
/// behind it, a stock <c>ssh-agent</c> that holds the key.
/// </summary>
/// <remarks>
/// <para>
/// The stock agent does the cryptography and the destination check: each key is loaded with
/// destination constraints (<c>ssh-add -h user@host</c>) against the binding's host keys, so it signs
/// only for a session whose server proved one of those host keys, for that user. A request to sign
/// anything else, or to sign with no session bound, is refused by OpenSSH itself.
/// </para>
/// <para>
/// This class is the gate in front of it. Every connection re-reads the state file first, so a
/// revoked grant, a missing state or a stale state closes the socket on the next use. Only three
/// requests are relayed (list keys, sign, bind session); adding, removing or locking keys from inside
/// the container is answered with a failure and never reaches the stock agent.
/// </para>
/// <para>
/// The key reaches <c>ssh-add</c> on standard input and is never written to disk. Nothing here logs a
/// key, a signature or a request body.
/// </para>
/// </remarks>
internal sealed class SshAgentGate(SecretProxyOptions options, StateStore state, ILogger<SshAgentGate> logger)
    : BackgroundService
{
    internal const int MaxFrameBytes = 256 * 1024;

    // draft-miller-ssh-agent message numbers.
    internal const byte Failure = 5;
    internal const byte RequestIdentities = 11;
    internal const byte SignRequest = 13;
    internal const byte Extension = 27;
    internal const string SessionBindExtension = "session-bind@openssh.com";

    private static readonly UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // The directory is mounted into one container only; whoever runs there may connect.
    private static readonly UnixFileMode SocketMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;

    private static readonly UnixFileMode KnownHostsMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _loggedFailures = new(StringComparer.Ordinal);
    private readonly string _privateRoot = Path.Combine(
        options.SshPrivateDirectory, "fsa-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());
    private CancellationToken _stopping;
    private int _nextSlot;

    public bool Enabled => !string.IsNullOrWhiteSpace(options.SshDirectory);

    /// <summary>The agents that currently have a socket. For tests and the health answer.</summary>
    internal IReadOnlyList<string> ActiveAgents
    {
        get { lock (_slots) return [.. _slots.Values.Select(s => s.Plan.Agent).Order(StringComparer.Ordinal)]; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
            return;

        _stopping = stoppingToken;
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.SshReconcileSeconds, 1, 60));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Never let one bad pass stop the forwarder: the HTTPS side shares this process.
                    logger.LogError("SecretProxy ssh reconcile failed ({Error})", ex.GetType().Name);
                }
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        finally
        {
            RemoveAll();
        }
    }

    /// <summary>
    /// Brings the sockets in line with the state: one per agent that holds an SSH grant, none for
    /// anyone else, and none at all while the state is missing, invalid or stale.
    /// </summary>
    internal async Task ReconcileAsync(CancellationToken ct)
    {
        await _reconcile.WaitAsync(ct);
        try
        {
            var desired = Desired(state.Current);

            List<Slot> stale;
            lock (_slots)
            {
                stale = [.. _slots.Values.Where(s =>
                    !desired.TryGetValue(s.Plan.Directory, out var plan) || plan.Fingerprint != s.Plan.Fingerprint)];
                foreach (var slot in stale)
                    _slots.Remove(slot.Plan.Directory);
            }
            foreach (var slot in stale)
            {
                slot.Dispose();
                logger.LogInformation("SecretProxy ssh socket removed: agent={Agent}", slot.Plan.Agent);
            }

            foreach (var plan in desired.Values)
            {
                lock (_slots)
                {
                    if (_slots.ContainsKey(plan.Directory))
                        continue;
                }

                try
                {
                    var slot = await CreateSlotAsync(plan, ct);
                    lock (_slots) _slots[plan.Directory] = slot;
                    _loggedFailures.Remove(plan.Directory);
                    logger.LogInformation(
                        "SecretProxy ssh socket ready: agent={Agent} keys={Keys}", plan.Agent, slot.LoadedKeys);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
                                           or InvalidOperationException or SocketException or TimeoutException)
                {
                    // Tried again on the next pass; said once per cause.
                    var cause = ex.GetType().Name;
                    if (!_loggedFailures.TryGetValue(plan.Directory, out var logged) || logged != cause)
                    {
                        _loggedFailures[plan.Directory] = cause;
                        logger.LogError(
                            "SecretProxy ssh socket for agent={Agent} could not be created ({Error})", plan.Agent, cause);
                    }
                }
            }
        }
        finally
        {
            _reconcile.Release();
        }
    }

    private void RemoveAll()
    {
        List<Slot> all;
        lock (_slots)
        {
            all = [.. _slots.Values];
            _slots.Clear();
        }
        foreach (var slot in all)
            slot.Dispose();
        try
        {
            if (Directory.Exists(_privateRoot))
                Directory.Delete(_privateRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing in it but dead sockets.
        }
    }

    // ── what the state asks for ────────────────────────────────────────────────

    private sealed record KeyPlan(string Key, string Pem, IReadOnlyList<string> Destinations, IReadOnlyList<string> KnownHosts);

    private sealed record AgentPlan(string Directory, string Agent, IReadOnlyList<KeyPlan> Keys, string Fingerprint);

    private static Dictionary<string, AgentPlan> Desired(StateSnapshot? snapshot)
    {
        var result = new Dictionary<string, AgentPlan>(StringComparer.Ordinal);
        if (snapshot is null)
            return result;

        foreach (var group in snapshot.SshGrants.GroupBy(g => g.Agent, StringComparer.Ordinal))
        {
            if (SecretProxySsh.AgentDirectoryName(group.Key) is not { } directory)
                continue;

            // A binding without a usable key still gets the agent a socket: it then offers no
            // identity, which ssh reports plainly, instead of pointing at a path that does not exist.
            var keys = group.Select(g => g.Binding)
                .DistinctBy(b => b.Key, StringComparer.Ordinal)
                .OrderBy(b => b.Key, StringComparer.Ordinal)
                .Select(b => NormalizeKey(b.Value) is { } pem ? new KeyPlan(b.Key, pem, b.Hosts, b.KnownHosts ?? []) : null)
                .OfType<KeyPlan>()
                .ToList();

            var material = new StringBuilder();
            foreach (var key in keys)
                material.Append(key.Key).Append('\n').Append(key.Pem).Append('\n')
                    .AppendJoin(',', key.Destinations).Append('\n')
                    .AppendJoin('\n', key.KnownHosts).Append("\n\n");
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));

            result[directory] = new AgentPlan(directory, group.Key, keys, fingerprint);
        }
        return result;
    }

    /// <summary>
    /// The private key as OpenSSH reads it, or null. Accepts the key text itself or its base64 (how
    /// a multi-line key is usually kept in <c>.env</c>).
    /// </summary>
    internal static string? NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "<secret>")
            return null;

        var text = value.Trim();
        if (!text.StartsWith("-----BEGIN ", StringComparison.Ordinal))
        {
            try
            {
                text = Encoding.UTF8.GetString(Convert.FromBase64String(text)).Trim();
            }
            catch (FormatException)
            {
                return null;
            }
            if (!text.StartsWith("-----BEGIN ", StringComparison.Ordinal))
                return null;
        }

        // A key kept on one line with literal \n between its lines.
        if (!text.Contains('\n'))
            text = text.Replace("\\n", "\n", StringComparison.Ordinal);
        return text.ReplaceLineEndings("\n") + "\n";
    }

    // ── one agent's socket ─────────────────────────────────────────────────────

    private sealed class Slot : IDisposable
    {
        public required AgentPlan Plan { get; init; }
        public required string SocketPath { get; init; }
        public required string KnownHostsPath { get; init; }
        public required string PrivateDirectory { get; init; }
        public required string PrivateSocket { get; init; }
        public Process? Agent { get; set; }
        public Socket? Listener { get; set; }
        public int LoadedKeys { get; set; }
        public CancellationTokenSource Stop { get; } = new();
        public ConcurrentDictionary<Socket, byte> Connections { get; } = new();

        public void Dispose()
        {
            Stop.Cancel();
            try { Listener?.Dispose(); } catch (SocketException) { }
            foreach (var connection in Connections.Keys)
            {
                try { connection.Dispose(); } catch (SocketException) { }
            }
            TryDelete(SocketPath);
            TryDelete(KnownHostsPath);
            try
            {
                if (Agent is { HasExited: false })
                    Agent.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
            Agent?.Dispose();
            try
            {
                if (Directory.Exists(PrivateDirectory))
                    Directory.Delete(PrivateDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<Slot> CreateSlotAsync(AgentPlan plan, CancellationToken ct)
    {
        var publicDirectory = Path.Combine(options.SshDirectory, plan.Directory);
        Directory.CreateDirectory(publicDirectory);

        var privateDirectory = Path.Combine(_privateRoot, Interlocked.Increment(ref _nextSlot).ToString());
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(privateDirectory);
        else
            Directory.CreateDirectory(privateDirectory, PrivateDirectoryMode);

        var slot = new Slot
        {
            Plan = plan,
            SocketPath = Path.Combine(publicDirectory, SecretProxySsh.SocketFile),
            KnownHostsPath = Path.Combine(publicDirectory, SecretProxySsh.KnownHostsFile),
            PrivateDirectory = privateDirectory,
            PrivateSocket = Path.Combine(privateDirectory, "a.sock"),
        };

        try
        {
            slot.Agent = await StartStockAgentAsync(slot.PrivateSocket, ct);

            // The host keys the stock agent binds the keys to, by plain host name.
            var hostsFile = Path.Combine(privateDirectory, "hosts");
            await File.WriteAllLinesAsync(hostsFile, plan.Keys
                .SelectMany(k => k.KnownHosts)
                .Select(SecretBindingRules.ParseKnownHost)
                .Where(p => p is not null)
                .Select(p => $"{p!.Value.Host} {p.Value.KeyType} {p.Value.Key}")
                .Distinct(StringComparer.Ordinal), ct);

            foreach (var key in plan.Keys)
            {
                var exit = await LoadKeyAsync(slot.PrivateSocket, hostsFile, key, ct);
                if (exit == 0)
                    slot.LoadedKeys++;
                else
                    logger.LogError(
                        "SecretProxy ssh key {Key} could not be loaded for agent={Agent} (ssh-add exit {Exit}); "
                        + "it must be an unencrypted private key, as text or base64",
                        key.Key, plan.Agent, exit);
            }

            // What the agent's own ssh checks the server against: the operator's lines, as given.
            var temporary = slot.KnownHostsPath + ".tmp";
            await File.WriteAllLinesAsync(
                temporary, plan.Keys.SelectMany(k => k.KnownHosts).Distinct(StringComparer.Ordinal), ct);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, KnownHostsMode);
            File.Move(temporary, slot.KnownHostsPath, overwrite: true);

            File.Delete(slot.SocketPath);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            slot.Listener = listener;
            listener.Bind(new UnixDomainSocketEndPoint(slot.SocketPath));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(slot.SocketPath, SocketMode);
            listener.Listen(16);
            _ = AcceptAsync(slot);
            return slot;
        }
        catch
        {
            slot.Dispose();
            throw;
        }
    }

    private async Task<Process> StartStockAgentAsync(string socket, CancellationToken ct)
    {
        var start = new ProcessStartInfo(options.SshAgentPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-D");
        start.ArgumentList.Add("-a");
        start.ArgumentList.Add(socket);
        start.Environment.Remove(SecretProxySsh.SocketVariable);

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("ssh-agent did not start");
        process.StandardInput.Close();
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(socket))
        {
            if (process.HasExited || DateTime.UtcNow > deadline)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose();
                throw new TimeoutException("ssh-agent did not open its socket");
            }
            await Task.Delay(25, ct);
        }
        return process;
    }

    /// <summary>Loads one key into the stock agent, constrained to its destinations. Returns ssh-add's exit code.</summary>
    private async Task<int> LoadKeyAsync(string socket, string hostsFile, KeyPlan key, CancellationToken ct)
    {
        var start = new ProcessStartInfo(options.SshAddPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-q");
        start.ArgumentList.Add("-H");
        start.ArgumentList.Add(hostsFile);
        foreach (var destination in key.Destinations)
        {
            start.ArgumentList.Add("-h");
            start.ArgumentList.Add(destination);
        }
        start.ArgumentList.Add("-");
        start.Environment[SecretProxySsh.SocketVariable] = socket;
        // A key with a passphrase must fail, not wait for someone to type it.
        start.Environment["SSH_ASKPASS_REQUIRE"] = "never";
        start.Environment.Remove("DISPLAY");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("ssh-add did not start");
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.StandardInput.WriteAsync(key.Pem.AsMemory(), ct);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return -1;
        }
    }

    // ── the gate ───────────────────────────────────────────────────────────────

    private bool IsCurrent(Slot slot)
    {
        lock (_slots)
            return _slots.TryGetValue(slot.Plan.Directory, out var current) && ReferenceEquals(current, slot);
    }

    private async Task AcceptAsync(Slot slot)
    {
        var listener = slot.Listener!;
        try
        {
            while (!slot.Stop.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(slot.Stop.Token);
                _ = ServeAsync(slot, client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The slot was removed.
        }
    }

    private async Task ServeAsync(Slot slot, Socket client)
    {
        Socket? upstream = null;
        try
        {
            // The state as it is now, not as it was at the last pass: a grant revoked a moment ago
            // must not sign once more.
            await ReconcileAsync(_stopping);
            if (!IsCurrent(slot))
                return;

            upstream = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            slot.Connections[client] = 0;
            slot.Connections[upstream] = 0;
            await upstream.ConnectAsync(new UnixDomainSocketEndPoint(slot.PrivateSocket), slot.Stop.Token);

            await using var agentSide = new NetworkStream(client, ownsSocket: false);
            await using var stockSide = new NetworkStream(upstream, ownsSocket: false);
            while (await ReadFrameAsync(agentSide, slot.Stop.Token) is { } request)
            {
                if (!IsCurrent(slot))
                    return;
                if (!IsRelayed(request))
                {
                    await WriteFrameAsync(agentSide, [Failure], slot.Stop.Token);
                    continue;
                }

                await WriteFrameAsync(stockSide, request, slot.Stop.Token);
                if (await ReadFrameAsync(stockSide, slot.Stop.Token) is not { } response)
                    return;
                await WriteFrameAsync(agentSide, response, slot.Stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                   or ObjectDisposedException or InvalidDataException)
        {
            // The client went away, sent something that is not the agent protocol, or the slot was removed.
        }
        finally
        {
            slot.Connections.TryRemove(client, out _);
            client.Dispose();
            if (upstream is not null)
            {
                slot.Connections.TryRemove(upstream, out _);
                upstream.Dispose();
            }
        }
    }

    /// <summary>
    /// The only requests that reach the stock agent: list the keys, sign, and bind a session to a
    /// host key. Everything else (add, remove, lock, any other extension) would let the container
    /// change what the agent holds.
    /// </summary>
    internal static bool IsRelayed(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0)
            return false;
        switch (frame[0])
        {
            case RequestIdentities:
                return frame.Length == 1;
            case SignRequest:
                return true;
            case Extension:
                if (frame.Length < 5)
                    return false;
                var length = BinaryPrimitives.ReadUInt32BigEndian(frame[1..5]);
                return length == SessionBindExtension.Length
                    && frame.Length >= 5 + length
                    && frame.Slice(5, (int)length).SequenceEqual(Encoding.ASCII.GetBytes(SessionBindExtension));
            default:
                return false;
        }
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, ct);
        if (read == 0)
            return null;
        if (read < 4)
            throw new InvalidDataException("truncated frame");

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MaxFrameBytes)
            throw new InvalidDataException("frame length out of range");

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return body;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
    }
}
