using System.Security.Cryptography;
using System.Text;
using Fleet.Agent.Configuration;
using Fleet.Protocol;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>Outcome of a principal-binding attempt (D9).</summary>
public readonly record struct PrincipalBindingResult(bool Success, string PrincipalId, long OwnerUserId, ProtocolErrorCode? Error)
{
    public static PrincipalBindingResult Fail(ProtocolErrorCode code) => new(false, "", 0, code);
    public static PrincipalBindingResult Ok(string principalId, long ownerUserId) => new(true, principalId, ownerUserId, null);
}

/// <summary>
/// Resolves an opaque client principal against the existing numeric allowlist (D9).
///
/// <para><b>This is a binding, not authentication.</b> A shared operator-set token is not an auth
/// scheme; it exists so the owner check is deterministic rather than a guess, and because Phase 0's
/// only permitted adapter is an in-memory loopback in the test project. Real authentication —
/// device registration, per-principal credentials, revocation, replay resistance — is a separate
/// issue.</para>
///
/// <para>The binder NEVER parses a numeric id out of client input (Constraint 22). Everything is
/// resolved against operator configuration, so a client cannot nominate which user it is.</para>
/// </summary>
public sealed class PrincipalBinder
{
    private readonly ClientChannelOptions _options;
    private readonly AllowlistHolder _allowlist;
    private readonly string _agentName;

    public PrincipalBinder(
        IOptions<ClientChannelOptions> options,
        IOptions<AgentOptions> agentConfig,
        AllowlistHolder allowlist)
    {
        _options = options.Value;
        _allowlist = allowlist;
        _agentName = agentConfig.Value.Name;
    }

    /// <summary>True when the operator has configured the client channel at all.</summary>
    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(_options.OwnerPrincipalToken) && _options.OwnerUserId != 0;

    /// <summary>
    /// The five-step resolution rule, evaluated in order. Any failure yields
    /// <c>unauthorized</c> (or <c>unsupported_role</c>) and the caller creates NO registry entry.
    /// </summary>
    public PrincipalBindingResult Bind(PrincipalBinding? binding, PrincipalRole role)
    {
        // 1. The client channel must be configured. Absent configuration disables it entirely —
        //    there is deliberately no default token.
        if (!IsEnabled)
            return PrincipalBindingResult.Fail(ProtocolErrorCode.Unauthorized);

        if (binding is null)
            return PrincipalBindingResult.Fail(ProtocolErrorCode.Unauthorized);

        // 2. Only one scheme exists in v1. `scheme` exists rather than a bare string so future
        //    schemes (device, federated) are additive.
        if (!string.Equals(binding.Scheme, PrincipalBinding.LegacyOwnerScheme, StringComparison.Ordinal))
            return PrincipalBindingResult.Fail(ProtocolErrorCode.Unauthorized);

        // 3. Fixed-time comparison. A `==` or length-varying comparison leaks the token a
        //    character at a time and is a review rejection (Constraint 22).
        if (!FixedTimeTokenEquals(binding.Value, _options.OwnerPrincipalToken))
            return PrincipalBindingResult.Fail(ProtocolErrorCode.Unauthorized);

        // 4. Re-checked at OPEN time against the LIVE allowlist, so revoking the owner also closes
        //    the client channel with no reprovision.
        if (!_allowlist.IsUserAllowed(_options.OwnerUserId))
            return PrincipalBindingResult.Fail(ProtocolErrorCode.Unauthorized);

        // 5. v1 is owner-only. `member`/`guest` exist in the enum as reserved values the runtime
        //    rejects, so a future multi-principal phase is additive rather than a major bump.
        if (role != PrincipalRole.Owner)
            return PrincipalBindingResult.Fail(ProtocolErrorCode.UnsupportedRole);

        return PrincipalBindingResult.Ok(DerivePrincipalId(), _options.OwnerUserId);
    }

    /// <summary>
    /// <c>PrincipalId = "p_" + Base32Lower(SHA256(token || 0x1F || agentName))[0..16]</c>
    ///
    /// Deterministic across restarts (so no random salt), non-reversible, and derived from
    /// nothing the client sends beyond the matched token. The numeric owner id is never emitted
    /// in any event.
    /// </summary>
    public string DerivePrincipalId()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Encoding.UTF8.GetBytes(_options.OwnerPrincipalToken));
        buffer.Add(0x1F);
        buffer.AddRange(Encoding.UTF8.GetBytes(_agentName));

        var hash = SHA256.HashData(buffer.ToArray());
        return "p_" + Base32Lower(hash)[..16];
    }

    /// <summary>
    /// Fixed-time equality over UTF-8 bytes.
    ///
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> requires equal-length spans, and
    /// returning early on a length mismatch would itself be a (much weaker) length oracle. Both
    /// sides are therefore hashed to a fixed 32 bytes first, so the comparison is always over the
    /// same length regardless of the candidate's size.
    /// </summary>
    internal static bool FixedTimeTokenEquals(string candidate, string expected)
    {
        Span<byte> candidateHash = stackalloc byte[32];
        Span<byte> expectedHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(candidate), candidateHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), expectedHash);
        return CryptographicOperations.FixedTimeEquals(candidateHash, expectedHash);
    }

    private static string Base32Lower(ReadOnlySpan<byte> data)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var builder = new StringBuilder();
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                builder.Append(alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0)
            builder.Append(alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return builder.ToString();
    }
}
