using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Fleet.Comms.Auth;

/// <summary>Hashes and verifies a credential secret. One implementation; the seam exists for tests.</summary>
public interface ISecretHasher
{
    /// <summary>Hash a secret with a freshly generated per-record salt.</summary>
    string Hash(string secret);

    /// <summary>Constant-time verification against a stored encoded hash.</summary>
    bool Verify(string secret, string encodedHash);

    /// <summary>
    /// Verify against <paramref name="encodedHash"/>, or — when there is no record to verify
    /// against — do the <b>same work</b> against a fixed placeholder and return <c>false</c>.
    ///
    /// <para>This is what makes "no such record" and "record exists, wrong secret" cost the same.
    /// Without it the two answers are byte-identical but not time-identical, and a deliberately
    /// slow KDF turns that gap into an existence oracle for a device, enrollment or token id.</para>
    /// </summary>
    bool VerifyOrDummy(string secret, string? encodedHash);
}

/// <summary>
/// Argon2id with a per-record salt, as the contract requires for all three credential kinds
/// (docs/first-party-api.md §3, issue #292 scope 4).
///
/// <para><b>Parameters are OWASP's minimum recommendation</b> — 19 MiB, 2 iterations, 1 lane —
/// and are stored inside the encoded hash rather than assumed at verify time, so raising them
/// later does not invalidate existing records.</para>
///
/// <para><b>Why Argon2id for a 256-bit random token at all.</b> A memory-hard KDF earns its cost
/// against low-entropy secrets, and these are not: brute-forcing 256 bits is infeasible regardless
/// of the hash. The contract mandates it anyway, and the cost is real — roughly tens of
/// milliseconds on every authenticated request. That is a deliberate spec-fidelity choice, not an
/// oversight, and it is the reason the verify path takes a record handle rather than scanning.</para>
/// </summary>
public sealed class Argon2idSecretHasher : ISecretHasher
{
    /// <summary>Salt length. Per record, never shared, never derived from the record's identity.</summary>
    public const int SaltBytes = 16;

    /// <summary>Output length, matching the 256-bit secrets being hashed.</summary>
    public const int HashBytes = 32;

    private const string Prefix = "$argon2id$v=19$";

    private readonly int _memoryKib;
    private readonly int _iterations;
    private readonly int _parallelism;

    /// <summary>
    /// A hash of a value nobody holds, used only to spend the same work on a lookup that found
    /// nothing. Derived with this instance's parameters so the placeholder and the real thing cost
    /// the same; a hard-coded constant would drift the moment the parameters were raised.
    /// </summary>
    private readonly string _dummyHash;

    public Argon2idSecretHasher(int memoryKib = 19456, int iterations = 2, int parallelism = 1)
    {
        _memoryKib = memoryKib;
        _iterations = iterations;
        _parallelism = parallelism;
        _dummyHash = Hash(Base64Url.Encode(RandomNumberGenerator.GetBytes(Credentials.SecretBytes)));
    }

    public string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(secret, salt, _memoryKib, _iterations, _parallelism);
        return $"{Prefix}m={_memoryKib},t={_iterations},p={_parallelism}${Base64Url.Encode(salt)}${Base64Url.Encode(hash)}";
    }

    public bool Verify(string secret, string encodedHash)
    {
        if (!TryParse(encodedHash, out var memoryKib, out var iterations, out var parallelism,
                out var salt, out var expected))
        {
            return false;
        }

        var actual = Derive(secret, salt, memoryKib, iterations, parallelism);

        // Fixed-time comparison. The values being compared are derived rather than presented, so
        // a timing leak here is a narrow one — but a narrow leak in an auth path is still a leak.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public bool VerifyOrDummy(string secret, string? encodedHash)
    {
        // The `false` is discarded by the caller either way; the point of the call is the elapsed
        // time, not the answer. Written as one branch rather than an `if` at each call site so a
        // future path cannot forget it.
        var result = Verify(secret, encodedHash ?? _dummyHash);
        return encodedHash is not null && result;
    }

    private static byte[] Derive(string secret, byte[] salt, int memoryKib, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(secret))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(HashBytes);
    }

    private static bool TryParse(
        string encoded, out int memoryKib, out int iterations, out int parallelism,
        out byte[] salt, out byte[] hash)
    {
        memoryKib = iterations = parallelism = 0;
        salt = [];
        hash = [];

        if (string.IsNullOrEmpty(encoded) || !encoded.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var parts = encoded[Prefix.Length..].Split('$');
        if (parts.Length != 3)
            return false;

        foreach (var parameter in parts[0].Split(','))
        {
            var pair = parameter.Split('=');
            if (pair.Length != 2 || !int.TryParse(pair[1], out var value))
                return false;
            switch (pair[0])
            {
                case "m": memoryKib = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
                default: return false;
            }
        }

        if (memoryKib <= 0 || iterations <= 0 || parallelism <= 0)
            return false;

        try
        {
            salt = Base64Url.Decode(parts[1]);
            hash = Base64Url.Decode(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length > 0 && hash.Length > 0;
    }
}
