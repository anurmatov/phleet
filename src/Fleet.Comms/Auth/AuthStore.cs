namespace Fleet.Comms.Auth;

/// <summary>An enrollment code the operator issued, stored only as a salted hash.</summary>
public sealed record EnrollmentRecord
{
    public required string EnrollmentId { get; init; }
    public required string CodeHash { get; init; }
    public required string PrincipalId { get; init; }

    /// <summary>Absolute, stored at issue — never recomputed at check time (§3, clock skew).</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Null until the code is consumed. Set in the same transaction as the device record.</summary>
    public DateTimeOffset? ConsumedAt { get; init; }

    /// <summary>The device this code produced. Null until consumed.</summary>
    public string? DeviceId { get; init; }
}

/// <summary>A registered device. The secret itself is never stored, only its salted hash.</summary>
public sealed record DeviceRecord
{
    public required string DeviceId { get; init; }
    public required string PrincipalId { get; init; }
    public required string SecretHash { get; init; }
    public required string EnrollmentId { get; init; }
    public required DateTimeOffset RegisteredAt { get; init; }

    /// <summary>
    /// When this device first successfully minted a token. This is what closes the registration
    /// recovery window (§3.4) — not the timer alone.
    /// </summary>
    public DateTimeOffset? FirstTokenMintedAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public bool IsActive => RevokedAt is null;
}

/// <summary>An issued access token, stored only as a salted hash with an absolute expiry.</summary>
public sealed record TokenRecord
{
    public required string TokenId { get; init; }
    public required string TokenHash { get; init; }
    public required string DeviceId { get; init; }

    /// <summary>Absolute, stored at issue time (§3). A backward clock jump therefore fails closed.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }
}

/// <summary>Raised when the auth store cannot answer. Always becomes a 503 — never a pass (§16).</summary>
public sealed class AuthStoreUnavailableException(string message) : Exception(message);

/// <summary>
/// Reads and writes inside one atomic unit. Every policy decision in <see cref="AuthService"/> that
/// spans more than one record runs through here.
/// </summary>
public interface IAuthStoreTransaction
{
    Task<EnrollmentRecord?> FindEnrollmentAsync(string enrollmentId, CancellationToken ct);
    Task SaveEnrollmentAsync(EnrollmentRecord record, CancellationToken ct);

    Task<DeviceRecord?> FindDeviceAsync(string deviceId, CancellationToken ct);
    Task SaveDeviceAsync(DeviceRecord record, CancellationToken ct);
    Task<int> CountActiveDevicesAsync(string principalId, CancellationToken ct);

    Task<TokenRecord?> FindTokenAsync(string tokenId, CancellationToken ct);
    Task SaveTokenAsync(TokenRecord record, CancellationToken ct);

    /// <summary>Revoke every token belonging to a device, in the same unit as the device itself.</summary>
    Task RevokeTokensForDeviceAsync(string deviceId, DateTimeOffset at, CancellationToken ct);
}

/// <summary>
/// The persistence port for the auth slice.
///
/// <para><b>Atomicity is part of the contract, not an implementation detail.</b> Consuming an
/// enrollment code and creating the device it produced must be all-or-nothing: a partial consume
/// that leaves a code both spent and unusable locks the owner out with no recovery, and the
/// contract forbids it (§16). Every multi-record decision therefore runs inside
/// <see cref="InTransactionAsync{T}"/>, so a SQL implementation maps it to a database transaction
/// and an in-process implementation maps it to a critical section — rather than each call site
/// inventing its own ordering.</para>
/// </summary>
public interface IAuthStore
{
    Task<T> InTransactionAsync<T>(
        Func<IAuthStoreTransaction, CancellationToken, Task<T>> body, CancellationToken ct);
}
