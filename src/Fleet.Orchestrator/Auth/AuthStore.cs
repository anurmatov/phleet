namespace Fleet.Orchestrator.Auth;

/// <summary>What a device is: an enrolled app holding a device secret, or a signed-in browser.</summary>
public enum DeviceKind
{
    /// <summary>Enrolled with a one-time code. Holds a device secret and mints access tokens.</summary>
    App,

    /// <summary>Signed in with the bootstrap token. Holds a session token in a cookie and nothing else.</summary>
    Browser,
}

/// <summary>What a token is for, and so how it may be presented.</summary>
public enum TokenKind
{
    /// <summary>Minted from a device secret, presented as a bearer, 15 minutes, never extended.</summary>
    Access,

    /// <summary>Issued to a browser at sign-in, presented as a cookie, extended while it is used.</summary>
    Session,
}

/// <summary>
/// A person. Devices belong to one, and its id is the principal every conversation is owned by.
/// </summary>
/// <remarks>
/// There is one today, <see cref="OwnerId"/>, seeded at startup. Nothing here adds or removes
/// people; the table exists so that a second person is a row and not a redesign.
/// </remarks>
public sealed record UserRecord
{
    /// <summary>The id of the one user startup seeds.</summary>
    public const string OwnerId = "owner";

    public required string UserId { get; init; }
    public string DisplayName { get; init; } = "";

    /// <summary>The same person on Telegram, when known. Null otherwise.</summary>
    public long? TelegramUserId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>An enrollment code, stored only as a salted hash.</summary>
public sealed record EnrollmentRecord
{
    public required string EnrollmentId { get; init; }
    public required string CodeHash { get; init; }
    public required string PrincipalId { get; init; }

    /// <summary>The name the issuer gave the device this code will register. May be empty.</summary>
    public string DeviceName { get; init; } = "";

    /// <summary>Absolute, stored at issue — never recomputed at check time.</summary>
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

/// <summary>A device. A secret is never stored, only its salted hash.</summary>
public sealed record DeviceRecord
{
    public required string DeviceId { get; init; }
    public required string PrincipalId { get; init; }
    public required DeviceKind Kind { get; init; }

    /// <summary>What the Devices list shows. Chosen by whoever added the device; may be empty.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// The hash of the device secret. Empty for a browser, which has no device secret: an empty
    /// hash verifies nothing, so a browser's device id can never mint an access token.
    /// </summary>
    public required string SecretHash { get; init; }

    /// <summary>The enrollment that produced an app device. Null for a browser.</summary>
    public string? EnrollmentId { get; init; }

    public required DateTimeOffset RegisteredAt { get; init; }

    /// <summary>
    /// When this device first successfully minted a token. This is what closes the registration
    /// recovery window — not the timer alone.
    /// </summary>
    public DateTimeOffset? FirstTokenMintedAt { get; init; }

    /// <summary>When a credential of this device last authenticated, to within a few minutes.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public bool IsActive => RevokedAt is null;
}

/// <summary>An issued token, stored only as a salted hash with an absolute expiry.</summary>
public sealed record TokenRecord
{
    public required string TokenId { get; init; }
    public required string TokenHash { get; init; }
    public required string DeviceId { get; init; }
    public required TokenKind Kind { get; init; }

    /// <summary>
    /// Absolute. Compared against <see cref="MonotonicClock"/> rather than raw wall time, and burned
    /// into <see cref="RevokedAt"/> the first time it is observed past — so neither a backward clock
    /// step nor a restart can make it valid again. Only a session token is ever moved forward, and
    /// only by a request that presented it while it was still valid.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }
}

/// <summary>Raised when the auth store cannot answer. Always becomes a 503 — never a pass.</summary>
public sealed class AuthStoreUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Reads and writes inside one atomic unit. Every policy decision in <see cref="AuthService"/> that
/// spans more than one record runs through here.
/// </summary>
public interface IAuthStoreTransaction
{
    Task<UserRecord?> FindUserAsync(string userId, CancellationToken ct);
    Task SaveUserAsync(UserRecord record, CancellationToken ct);

    Task<EnrollmentRecord?> FindEnrollmentAsync(string enrollmentId, CancellationToken ct);
    Task SaveEnrollmentAsync(EnrollmentRecord record, CancellationToken ct);

    Task<DeviceRecord?> FindDeviceAsync(string deviceId, CancellationToken ct);
    Task SaveDeviceAsync(DeviceRecord record, CancellationToken ct);
    Task<int> CountActiveDevicesAsync(string principalId, CancellationToken ct);

    /// <summary>
    /// One principal's devices, revoked ones included, oldest first. A list that hid revoked rows
    /// would answer "did the revocation take?" with silence — the same answer as a device that
    /// never existed. <see cref="DeviceRecord.IsActive"/> carries the distinction.
    /// </summary>
    Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(string principalId, CancellationToken ct);

    Task<TokenRecord?> FindTokenAsync(string tokenId, CancellationToken ct);
    Task SaveTokenAsync(TokenRecord record, CancellationToken ct);

    /// <summary>Whether a device still has a token that is neither revoked nor past its deadline.</summary>
    Task<bool> HasLiveTokenAsync(string deviceId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Remove registrations in the same transaction as their sign-in device's revocation.</summary>
    Task RemovePushDevicesAsync(string deviceId, CancellationToken ct);

    /// <summary>Revoke every token belonging to a device, in the same unit as the device itself.</summary>
    Task RevokeTokensForDeviceAsync(string deviceId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Delete what can no longer authenticate or be recovered: tokens whose deadline passed before
    /// <paramref name="before"/>, and enrollment codes that were consumed, burned or expired before
    /// it. Rows are removed, not marked — nothing reads them again.
    /// </summary>
    Task DeleteSpentAsync(DateTimeOffset before, CancellationToken ct);
}

/// <summary>
/// The persistence port for sign-in.
///
/// <para><b>Atomicity is part of the contract, not an implementation detail.</b> Consuming an
/// enrollment code and creating the device it produced must be all-or-nothing: a partial consume
/// that leaves a code both spent and unusable locks its holder out with no recovery. Every
/// multi-record decision therefore runs inside <see cref="InTransactionAsync{T}"/>. An
/// implementation may run <paramref name="body"/> more than once (a retried transaction), so a body
/// must derive everything it writes from what it reads inside the same call.</para>
/// </summary>
public interface IAuthStore
{
    Task<T> InTransactionAsync<T>(
        Func<IAuthStoreTransaction, CancellationToken, Task<T>> body, CancellationToken ct);
}
