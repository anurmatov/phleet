using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Fleet.Shared;
using Microsoft.Extensions.DependencyInjection;
using static Fleet.SecretProxy.Tests.ProxyHarness;

namespace Fleet.SecretProxy.Tests;

/// <summary>
/// A bound SSH key is used through a socket and never handed over. These run the real gate against
/// the real <c>ssh-agent</c> and <c>ssh-add</c>; a machine without them skips the socket tests (the
/// Docker harness in <c>tests/secret-proxy</c> runs them against a real sshd either way).
/// </summary>
public sealed class SshAgentGateTests
{
    private const string SshKey = "SSH_KEY_EXAMPLE";
    private const string Destination = "deploy@build.example.test";
    private const string Host = "build.example.test";

    private static readonly byte[] FailureAnswer = [SshAgentGate.Failure];

    private static readonly bool ToolsPresent = !OperatingSystem.IsWindows()
        && new[] { "ssh-agent", "ssh-add", "ssh-keygen" }.All(tool =>
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Any(dir => File.Exists(Path.Combine(dir, tool))));

    /// <summary>A throwaway user key and host key, made by ssh-keygen. Synthetic: generated per test.</summary>
    private sealed class Keys : IDisposable
    {
        private readonly string _directory = Path.Combine("/tmp", "fsk-" + Guid.NewGuid().ToString("N")[..8]);
        public string PrivateKey { get; }
        public byte[] PublicBlob { get; }
        public string KnownHostLine { get; }

        public Keys(string userKeyType = "ed25519")
        {
            Directory.CreateDirectory(_directory);
            Generate("user", userKeyType);
            Generate("host", "ed25519");
            PrivateKey = File.ReadAllText(Path.Combine(_directory, "user"));
            PublicBlob = Convert.FromBase64String(File.ReadAllText(Path.Combine(_directory, "user.pub")).Split(' ')[1]);
            var host = File.ReadAllText(Path.Combine(_directory, "host.pub")).Split(' ');
            KnownHostLine = $"{Host} {host[0]} {host[1].Trim()}";
        }

        private void Generate(string name, string type)
        {
            using var process = Process.Start(new ProcessStartInfo("ssh-keygen")
            {
                ArgumentList = { "-q", "-t", type, "-N", "", "-C", "synthetic", "-f", Path.Combine(_directory, name) },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }

    private static SecretProxyState SshState(ProxyHarness proxy, Keys keys, string? value = null, bool grant = true)
    {
        var state = proxy.DefaultState();
        return state with
        {
            Bindings =
            [
                .. state.Bindings,
                new SecretProxyBinding(SshKey, SecretBindingRules.KindSshKey, [Destination], "Authorization",
                    value ?? keys.PrivateKey, KnownHosts: [keys.KnownHostLine]),
            ],
            Grants = grant
                ? [.. state.Grants, new SecretProxyGrant("agent1", Loopback, SshKey, SecretPlaceholders.Hash(SecretPlaceholders.Create()))]
                : state.Grants,
        };
    }

    private static string SocketPath(ProxyHarness proxy, string agent = "agent1") =>
        Path.Combine(proxy.SshDirectory, agent, SecretProxySsh.SocketFile);

    private static SshAgentGate Gate(ProxyHarness proxy) => proxy.Services.GetRequiredService<SshAgentGate>();

    /// <summary>One request and its answer over the agent protocol, or null when the socket is closed on us.</summary>
    private static async Task<byte[]?> AskAsync(Socket socket, byte[] body)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        try
        {
            await socket.SendAsync(frame);
            var header = new byte[4];
            if (await ReadAsync(socket, header) < 4)
                return null;
            var answer = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
            return await ReadAsync(socket, answer) < answer.Length ? null : answer;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static async Task<int> ReadAsync(Socket socket, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(read));
            if (n == 0) break;
            read += n;
        }
        return read;
    }

    private static async Task<Socket?> ConnectAsync(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    private static byte[] Text(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        var result = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)bytes.Length);
        bytes.CopyTo(result, 4);
        return result;
    }

    private static byte[] Blob(byte[] value) => [.. BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness((uint)value.Length)), .. value];

    private static async Task<int> IdentityCountAsync(Socket socket)
    {
        var answer = await AskAsync(socket, [SshAgentGate.RequestIdentities]);
        Assert.NotNull(answer);
        Assert.Equal(12, answer![0]); // SSH_AGENT_IDENTITIES_ANSWER
        return (int)BinaryPrimitives.ReadUInt32BigEndian(answer.AsSpan(1, 4));
    }

    // ── grant ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GrantedAgent_GetsASocketAndTheHostKeys_AndIsOfferedTheBoundKey()
    {
        if (!ToolsPresent) return;
        using var keys = new Keys();
        await using var proxy = await StartAsync();
        proxy.WriteState(SshState(proxy, keys));
        await Gate(proxy).ReconcileAsync(default);

        Assert.Equal(["agent1"], Gate(proxy).ActiveAgents);
        Assert.Equal(
            [keys.KnownHostLine],
            File.ReadAllLines(Path.Combine(proxy.SshDirectory, "agent1", SecretProxySsh.KnownHostsFile)));

        using var socket = await ConnectAsync(SocketPath(proxy));
        Assert.NotNull(socket);
        var answer = await AskAsync(socket!, [SshAgentGate.RequestIdentities]);
        Assert.Equal(12, answer![0]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(answer.AsSpan(1, 4)));
        var blobLength = (int)BinaryPrimitives.ReadUInt32BigEndian(answer.AsSpan(5, 4));
        Assert.Equal(keys.PublicBlob, answer.AsSpan(9, blobLength).ToArray());

        // Only the agent that holds the grant has a directory at all.
        Assert.Equal(["agent1"], Directory.GetDirectories(proxy.SshDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task KeyKeptAsBase64_IsLoadedTheSame()
    {
        if (!ToolsPresent) return;
        using var keys = new Keys();
        await using var proxy = await StartAsync();
        proxy.WriteState(SshState(proxy, keys, Convert.ToBase64String(Encoding.UTF8.GetBytes(keys.PrivateKey))));
        await Gate(proxy).ReconcileAsync(default);

        using var socket = await ConnectAsync(SocketPath(proxy));
        Assert.Equal(1, await IdentityCountAsync(socket!));
    }

    [Fact]
    public async Task RsaKeyKeptAsBase64_IsLoadedAndOfferedLikeAnyOther()
    {
        if (!ToolsPresent) return;
        using var keys = new Keys("rsa");
        await using var proxy = await StartAsync();
        proxy.WriteState(SshState(proxy, keys, Convert.ToBase64String(Encoding.UTF8.GetBytes(keys.PrivateKey))));
        await Gate(proxy).ReconcileAsync(default);

        using var socket = await ConnectAsync(SocketPath(proxy));
        var answer = await AskAsync(socket!, [SshAgentGate.RequestIdentities]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(answer!.AsSpan(1, 4)));
        var blobLength = (int)BinaryPrimitives.ReadUInt32BigEndian(answer.AsSpan(5, 4));
        Assert.Equal(keys.PublicBlob, answer.AsSpan(9, blobLength).ToArray());
    }

    // ── what the socket will not do ────────────────────────────────────────────

    [Fact]
    public async Task SigningWithoutASessionBoundToAnAllowedHost_IsRefused()
    {
        if (!ToolsPresent) return;
        using var keys = new Keys();
        await using var proxy = await StartAsync();
        proxy.WriteState(SshState(proxy, keys));
        await Gate(proxy).ReconcileAsync(default);

        using var socket = await ConnectAsync(SocketPath(proxy));
        byte[] request = [SshAgentGate.SignRequest, .. Blob(keys.PublicBlob), .. Text("data chosen by the caller"), 0, 0, 0, 0];
        var answer = await AskAsync(socket!, request);

        // The stock agent's own refusal: the key is destination-constrained and no session was bound.
        Assert.Equal(FailureAnswer, answer);
    }

    [Theory]
    [InlineData(17)] // SSH_AGENTC_ADD_IDENTITY
    [InlineData(18)] // SSH_AGENTC_REMOVE_IDENTITY
    [InlineData(19)] // SSH_AGENTC_REMOVE_ALL_IDENTITIES
    [InlineData(20)] // SSH_AGENTC_ADD_SMARTCARD_KEY
    [InlineData(22)] // SSH_AGENTC_LOCK
    [InlineData(23)] // SSH_AGENTC_UNLOCK
    [InlineData(25)] // SSH_AGENTC_ADD_ID_CONSTRAINED
    public async Task RequestsThatWouldChangeWhatTheAgentHolds_NeverReachIt(byte type)
    {
        if (!ToolsPresent) return;
        using var keys = new Keys();
        await using var proxy = await StartAsync();
        proxy.WriteState(SshState(proxy, keys));
        await Gate(proxy).ReconcileAsync(default);

        using var socket = await ConnectAsync(SocketPath(proxy));
        byte[] request = [type, .. Text("synthetic-passphrase")];
        Assert.Equal(FailureAnswer, await AskAsync(socket!, request));

        // Still there, still unlocked: the request was answered by the gate, not relayed.
        Assert.Equal(1, await IdentityCountAsync(socket!));
    }

    [Fact]
    public void OnlyListSignAndSessionBind_AreRelayed()
    {
        Assert.True(SshAgentGate.IsRelayed([SshAgentGate.RequestIdentities]));
        Assert.True(SshAgentGate.IsRelayed([SshAgentGate.SignRequest, 0, 0, 0, 0]));
        Assert.True(SshAgentGate.IsRelayed([SshAgentGate.Extension, .. Text(SshAgentGate.SessionBindExtension), 1, 2, 3]));

        Assert.False(SshAgentGate.IsRelayed([]));
        Assert.False(SshAgentGate.IsRelayed([SshAgentGate.RequestIdentities, 0]));
        Assert.False(SshAgentGate.IsRelayed([SshAgentGate.Extension]));
        Assert.False(SshAgentGate.IsRelayed([SshAgentGate.Extension, .. Text("query")]));
        Assert.False(SshAgentGate.IsRelayed([SshAgentGate.Extension, .. Text(SshAgentGate.SessionBindExtension + "x")]));
        Assert.False(SshAgentGate.IsRelayed([SshAgentGate.Extension, 0, 0, 0, 200, (byte)'s']));
        for (var type = 0; type < 256; type++)
            if (type is not (SshAgentGate.RequestIdentities or SshAgentGate.SignRequest or SshAgentGate.Extension))
                Assert.False(SshAgentGate.IsRelayed([(byte)type, 0, 0, 0, 0]));
    }

    [Fact]
    public async Task AnOversizedOrMalformedFrame_ClosesTheConnection()
    {
        if (!ToolsPresent) return;
        using var keys = new Keys();
        await using var proxy = await StartAsync();
        proxy.WriteState(SshState(proxy, keys));
        await Gate(proxy).ReconcileAsync(default);

        using var socket = await ConnectAsync(SocketPath(proxy));
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, SshAgentGate.MaxFrameBytes + 1);
        await socket!.SendAsync(header);
        Assert.Equal(0, await ReadAsync(socket, new byte[1]));
    }

    // ── revocation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokedGrant_IsRefusedOnTheVeryNextConnection()
    {
        if (!ToolsPresent) return;
        using var keys = new Keys();
        await using var proxy = await StartAsync();
        proxy.WriteState(SshState(proxy, keys));
        await Gate(proxy).ReconcileAsync(default);
        using (var before = await ConnectAsync(SocketPath(proxy)))
            Assert.Equal(1, await IdentityCountAsync(before!));

        // The orchestrator removes the grant; nothing else happens before the agent tries again.
        proxy.WriteState(SshState(proxy, keys, grant: false));
        using var after = await ConnectAsync(SocketPath(proxy));
        if (after is not null)
            Assert.Null(await AskAsync(after, [SshAgentGate.RequestIdentities]));

        await Gate(proxy).ReconcileAsync(default);
        Assert.False(File.Exists(SocketPath(proxy)));
        Assert.Empty(Gate(proxy).ActiveAgents);
    }

    [Fact]
    public async Task AnOpenConnection_StopsWorkingWhenTheGrantIsRevoked()
    {
        if (!ToolsPresent) return;
        using var keys = new Keys();
        await using var proxy = await StartAsync();
        proxy.WriteState(SshState(proxy, keys));
        await Gate(proxy).ReconcileAsync(default);

        using var socket = await ConnectAsync(SocketPath(proxy));
        Assert.Equal(1, await IdentityCountAsync(socket!));

        proxy.WriteState(SshState(proxy, keys, grant: false));
        await Gate(proxy).ReconcileAsync(default);
        Assert.Null(await AskAsync(socket!, [SshAgentGate.RequestIdentities]));
    }

    [Fact]
    public async Task MissingState_RemovesEverySocket()
    {
        if (!ToolsPresent) return;
        using var keys = new Keys();
        await using var proxy = await StartAsync();
        proxy.WriteState(SshState(proxy, keys));
        await Gate(proxy).ReconcileAsync(default);
        Assert.True(File.Exists(SocketPath(proxy)));

        proxy.DeleteState();
        using var after = await ConnectAsync(SocketPath(proxy));
        if (after is not null)
            Assert.Null(await AskAsync(after, [SshAgentGate.RequestIdentities]));
        await Gate(proxy).ReconcileAsync(default);
        Assert.False(File.Exists(SocketPath(proxy)));
    }

    [Fact]
    public async Task StaleState_RemovesEverySocket_WithNoWriteAtAll()
    {
        if (!ToolsPresent) return;
        using var keys = new Keys();
        await using var proxy = await StartAsync();
        proxy.WriteState(SshState(proxy, keys));
        await Gate(proxy).ReconcileAsync(default);
        Assert.True(File.Exists(SocketPath(proxy)));

        // The orchestrator stopped refreshing the file: the grants in it end at the age bound.
        proxy.Time.Advance(TimeSpan.FromSeconds(SecretProxyState.DefaultMaxAgeSeconds + 1));
        using var after = await ConnectAsync(SocketPath(proxy));
        if (after is not null)
            Assert.Null(await AskAsync(after, [SshAgentGate.RequestIdentities]));
        await Gate(proxy).ReconcileAsync(default);
        Assert.False(File.Exists(SocketPath(proxy)));
    }

    [Fact]
    public async Task RotatedKey_ReplacesTheOldOne()
    {
        if (!ToolsPresent) return;
        using var keys = new Keys();
        using var rotated = new Keys();
        await using var proxy = await StartAsync();
        proxy.WriteState(SshState(proxy, keys));
        await Gate(proxy).ReconcileAsync(default);

        proxy.WriteState(SshState(proxy, keys, rotated.PrivateKey));
        await Gate(proxy).ReconcileAsync(default);

        using var socket = await ConnectAsync(SocketPath(proxy));
        var answer = await AskAsync(socket!, [SshAgentGate.RequestIdentities]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(answer!.AsSpan(1, 4)));
        var blobLength = (int)BinaryPrimitives.ReadUInt32BigEndian(answer.AsSpan(5, 4));
        Assert.Equal(rotated.PublicBlob, answer.AsSpan(9, blobLength).ToArray());
    }

    // ── unusable keys and leaks ────────────────────────────────────────────────

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("<secret>")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nsynthetic-garbage\n-----END OPENSSH PRIVATE KEY-----")]
    public async Task UnusableKey_GivesASocketThatOffersNothing_AndTheValueIsNotLogged(string value)
    {
        if (!ToolsPresent) return;
        using var keys = new Keys();
        await using var proxy = await StartAsync();
        proxy.WriteState(SshState(proxy, keys, value));
        await Gate(proxy).ReconcileAsync(default);

        using var socket = await ConnectAsync(SocketPath(proxy));
        Assert.Equal(0, await IdentityCountAsync(socket!));
        Assert.DoesNotContain("synthetic-garbage", proxy.Logs.All);
        Assert.DoesNotContain("not-a-key", proxy.Logs.All);
    }

    [Fact]
    public async Task ThePrivateKey_IsNeverLoggedOrWrittenWhereTheAgentCanRead()
    {
        if (!ToolsPresent) return;
        using var keys = new Keys();
        await using var proxy = await StartAsync();
        proxy.WriteState(SshState(proxy, keys));
        await Gate(proxy).ReconcileAsync(default);
        using (var socket = await ConnectAsync(SocketPath(proxy)))
            await IdentityCountAsync(socket!);

        var body = keys.PrivateKey.Split('\n')[1];
        Assert.DoesNotContain(body, proxy.Logs.All);
        foreach (var file in Directory.GetFiles(proxy.SshDirectory, "*", SearchOption.AllDirectories))
            if (Path.GetFileName(file) != SecretProxySsh.SocketFile)
                Assert.DoesNotContain(body, File.ReadAllText(file));
    }

    [Fact]
    public void KeyText_IsAcceptedAsTextBase64OrOneLine_AndNothingElse()
    {
        const string pem = "-----BEGIN OPENSSH PRIVATE KEY-----\nc3ludGhldGlj\n-----END OPENSSH PRIVATE KEY-----";
        Assert.Equal(pem + "\n", SshAgentGate.NormalizeKey(pem));
        Assert.Equal(pem + "\n", SshAgentGate.NormalizeKey("  " + pem + "\r\n"));
        Assert.Equal(pem + "\n", SshAgentGate.NormalizeKey(Convert.ToBase64String(Encoding.UTF8.GetBytes(pem))));
        Assert.Equal(pem + "\n", SshAgentGate.NormalizeKey(pem.Replace("\n", "\\n")));
        Assert.Null(SshAgentGate.NormalizeKey(null));
        Assert.Null(SshAgentGate.NormalizeKey(""));
        Assert.Null(SshAgentGate.NormalizeKey("<secret>"));
        Assert.Null(SshAgentGate.NormalizeKey("not base64 at all!"));
        Assert.Null(SshAgentGate.NormalizeKey(Convert.ToBase64String(Encoding.UTF8.GetBytes("plain text"))));
    }

    // ── an SSH grant is not an HTTPS grant ─────────────────────────────────────

    [Fact]
    public async Task ThePlaceholderOfAnSshGrant_IsNeverSwappedIntoAnHttpsRequest()
    {
        await using var proxy = await StartAsync();
        var placeholder = SecretPlaceholders.Create();
        var state = proxy.DefaultState();
        proxy.WriteState(state with
        {
            Bindings =
            [
                .. state.Bindings,
                new SecretProxyBinding(SshKey, SecretBindingRules.KindSshKey, [Destination], "Authorization",
                    "synthetic-ssh-key-text", KnownHosts: [$"{Host} ssh-ed25519 {new string('A', 68)}"]),
            ],
            Grants = [.. state.Grants, new SecretProxyGrant("agent1", Loopback, SshKey, SecretPlaceholders.Hash(placeholder))],
        });
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{HostA}/collect", "Bearer " + placeholder));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("secret_destination_mismatch", response.Headers.GetValues(InjectingForwarder.ErrorHeader).Single());
        Assert.Empty(proxy.Upstream.Calls);
    }
}
