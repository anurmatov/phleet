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

    /// <summary>
    /// Set the first time this code is observed past a deadline — its issue TTL, or the end of the
    /// registration recovery window. Persisting the observation is what makes expiry survive a
    /// process restart, after which <see cref="MonotonicClock"/> necessarily re-anchors to whatever
    /// the host clock then says.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; init; }

    public bool IsUsable => RevokedAt is null;
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

    /// <summary>
    /// Absolute, stored at issue time (§3). Compared against <see cref="MonotonicClock"/> rather
    /// than raw wall time, and burned into <see cref="RevokedAt"/> the first time it is observed
    /// past — so neither a backward clock step nor a restart can make it valid again.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }
}

/// <summary>Raised when the auth store cannot answer. Always becomes a 503 — never a pass (§16).</summary>
public sealed class AuthStoreUnavailableException(string message) : Exception(message);

/// <summary>
/// A backup the store declined to take — the destination exists, another invocation holds it, the
/// destination is the live database, or what was written did not validate.
///
/// <para>Its own type so the operator command can present it as a sentence and exit 1. These were
/// <see cref="InvalidOperationException"/>, which nothing caught: losing a destination claim
/// aborted the process with a stack trace instead of reporting a condition the operator can act
/// on.</para>
/// </summary>
public sealed class BackupRefusedException(string message, Exception? inner = null)
    : Exception(message, inner);

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

    /// <summary>
    /// Every device record, or only one principal's when <paramref name="principalId"/> is given.
    ///
    /// <para><b>Revoked records are included</b>, and that is the point rather than an oversight:
    /// the operator reaching for this is answering "which device do I revoke?" or "did the
    /// revocation take?", and a list that silently omitted revoked rows would answer the second
    /// question by showing nothing — indistinguishable from a device that was never registered.
    /// <see cref="DeviceRecord.IsActive"/> carries the distinction.</para>
    ///
    /// <para>Ordered by registration time so the output is stable between calls; the operator is
    /// reading it, and a list that reorders itself is a list nobody trusts.</para>
    /// </summary>
    Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(string? principalId, CancellationToken ct);

    Task<TokenRecord?> FindTokenAsync(string tokenId, CancellationToken ct);
    Task SaveTokenAsync(TokenRecord record, CancellationToken ct);

    /// <summary>Revoke every token belonging to a device, in the same unit as the device itself.</summary>
    Task RevokeTokensForDeviceAsync(string deviceId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Burn every enrollment code that has not been consumed, and return how many.
    ///
    /// <para>For the disaster-restore path. A snapshot restores unconsumed codes alongside devices,
    /// and an unconsumed code is a live registration credential for its remaining TTL — revoking
    /// devices and tokens while leaving those behind would close the front door and restore a key
    /// to the back one.</para>
    /// </summary>
    Task<int> RevokeAllEnrollmentsAsync(DateTimeOffset at, CancellationToken ct);
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

    /// <summary>
    /// Write a consistent copy of the store to <paramref name="destinationPath"/> while it is in
    /// use.
    ///
    /// <para>On the port rather than in the operator command because only the implementation knows
    /// how to do it safely. For SQLite that is <c>VACUUM INTO</c>: copying the file with <c>cp</c>
    /// or <c>tar</c> captures a WAL database mid-checkpoint and produces something that restores
    /// into a plausible-looking database missing its most recent writes — a backup that fails only
    /// when it is finally needed.</para>
    /// </summary>
    Task BackupToAsync(string destinationPath, CancellationToken ct);
}
