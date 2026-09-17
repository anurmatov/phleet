using Fleet.Protocol;
using Microsoft.Extensions.Logging;

namespace Fleet.Comms.Auth;

/// <summary>The outcome of an auth operation. Failure carries a code, never a reason string.</summary>
public readonly record struct AuthResult<T>(T? Value, ProtocolErrorCode? Error)
{
    public bool Succeeded => Error is null;

    public static AuthResult<T> Ok(T value) => new(value, null);

    public static AuthResult<T> Fail(ProtocolErrorCode code) => new(default, code);
}

public sealed record RegisteredDevice(string DeviceId, string DeviceSecret);

public sealed record MintedToken(string AccessToken, int ExpiresInSeconds);

public sealed record AuthenticatedPrincipal(string PrincipalId, string DeviceId, string TokenId);

/// <summary>
/// Every fixed behaviour of docs/first-party-api.md §3, in one place.
///
/// <para>Policy lives here rather than in the store so that a durable store implementation has to
/// satisfy an interface about records, not about rules.</para>
/// </summary>
public sealed class AuthService(
    IAuthStore store,
    ISecretHasher hasher,
    TimeProvider time,
    ILogger<AuthService> logger)
{
    /// <summary>Enrollment-code absolute TTL from issue (§3).</summary>
    public static readonly TimeSpan EnrollmentCodeTtl = TimeSpan.FromMinutes(15);

    /// <summary>Access-token absolute TTL from issue (§3).</summary>
    public static readonly TimeSpan AccessTokenTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The registration-recovery window (§3.4), measured from FIRST REGISTRATION and deliberately
    /// independent of the code's own issue TTL. Applying both would give a registration made late
    /// in a code's life a truncated — possibly zero-length — window, which is exactly when a client
    /// most needs one.
    /// </summary>
    public static readonly TimeSpan RegistrationRecoveryWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Issue an enrollment code. **Deliberately not reachable from any north route** — the operator
    /// issues codes out of band and that administrative path is not part of this boundary (§5.5).
    /// This exists as a library entry point for that path and for tests.
    /// </summary>
    public async Task<string> IssueEnrollmentCodeAsync(string principalId, CancellationToken ct = default)
    {
        var enrollmentId = Credentials.NewRecordId();
        var secret = Credentials.NewSecret();
        var now = time.GetUtcNow();

        await store.InTransactionAsync<object?>(async (tx, token) =>
        {
            await tx.SaveEnrollmentAsync(new EnrollmentRecord
            {
                EnrollmentId = enrollmentId,
                CodeHash = hasher.Hash(secret),
                PrincipalId = principalId,
                ExpiresAt = now + EnrollmentCodeTtl,
            }, token);
            return null;
        }, ct);

        logger.LogInformation("Enrollment code issued for a principal");
        return Credentials.Compose(enrollmentId, secret);
    }

    /// <summary>
    /// Register a device, or recover a registration whose response was lost (§3.2, §3.4).
    ///
    /// <para>Both outcomes come out of one transaction, because the decision between them depends
    /// on state that must not move underneath it.</para>
    /// </summary>
    public Task<AuthResult<RegisteredDevice>> RegisterDeviceAsync(
        string? enrollmentCode, CancellationToken ct = default)
    {
        if (!Credentials.TryParse(enrollmentCode, out var enrollmentId, out var presentedSecret))
        {
            // Malformed is answered exactly as unknown: a caller must not learn that its value was
            // the wrong *shape* rather than the wrong *value*.
            return Reject<RegisteredDevice>("enrollment_code_malformed");
        }

        return store.InTransactionAsync(async (tx, token) =>
        {
            var enrollment = await tx.FindEnrollmentAsync(enrollmentId, token);
            if (enrollment is null)
                return Rejected<RegisteredDevice>("enrollment_unknown");

            if (!hasher.Verify(presentedSecret, enrollment.CodeHash))
                return Rejected<RegisteredDevice>("enrollment_secret_mismatch");

            var now = time.GetUtcNow();

            return enrollment.ConsumedAt is null
                ? await FirstRegistrationAsync(tx, enrollment, now, token)
                : await RecoverRegistrationAsync(tx, enrollment, now, token);
        }, ct);
    }

    private async Task<AuthResult<RegisteredDevice>> FirstRegistrationAsync(
        IAuthStoreTransaction tx, EnrollmentRecord enrollment, DateTimeOffset now, CancellationToken ct)
    {
        // The issue TTL bounds how long an UNUSED code may sit around.
        if (now >= enrollment.ExpiresAt)
            return Rejected<RegisteredDevice>("enrollment_expired");

        // Owner-only: exactly one active device. Rotation is revoke-then-enroll, never an implicit
        // replacement — an implicit one would let a stolen code silently displace a working device.
        if (await tx.CountActiveDevicesAsync(enrollment.PrincipalId, ct) > 0)
        {
            logger.LogInformation("Device registration refused: an active device already exists");
            return AuthResult<RegisteredDevice>.Fail(ProtocolErrorCode.DeviceLimit);
        }

        var deviceId = Credentials.NewRecordId();
        var secret = Credentials.NewSecret();

        await tx.SaveDeviceAsync(new DeviceRecord
        {
            DeviceId = deviceId,
            PrincipalId = enrollment.PrincipalId,
            SecretHash = hasher.Hash(secret),
            EnrollmentId = enrollment.EnrollmentId,
            RegisteredAt = now,
        }, ct);

        // Consumed in the SAME transaction as the device it produced. A partial consume would
        // leave the code both spent and unusable, and lock the owner out with no recovery.
        await tx.SaveEnrollmentAsync(enrollment with { ConsumedAt = now, DeviceId = deviceId }, ct);

        logger.LogInformation("Device registered");
        return AuthResult<RegisteredDevice>.Ok(new RegisteredDevice(deviceId, secret));
    }

    private async Task<AuthResult<RegisteredDevice>> RecoverRegistrationAsync(
        IAuthStoreTransaction tx, EnrollmentRecord enrollment, DateTimeOffset now, CancellationToken ct)
    {
        var device = enrollment.DeviceId is null
            ? null
            : await tx.FindDeviceAsync(enrollment.DeviceId, ct);

        if (device is null || !device.IsActive)
            return Rejected<RegisteredDevice>("recovery_device_gone");

        // The window closes on FIRST SUCCESSFUL TOKEN MINT, not on the timer alone. Once the device
        // has proven it holds the secret, the only party who could benefit from another copy is an
        // attacker — a timer-only window is a re-issue oracle.
        if (device.FirstTokenMintedAt is not null)
            return Rejected<RegisteredDevice>("recovery_window_closed_by_token_mint");

        if (now >= enrollment.ConsumedAt!.Value + RegistrationRecoveryWindow)
            return Rejected<RegisteredDevice>("recovery_window_expired");

        var rotated = Credentials.NewSecret();
        await tx.SaveDeviceAsync(device with { SecretHash = hasher.Hash(rotated) }, ct);

        // Logged as its own event, so repeated rotation on one code is visible rather than silent.
        logger.LogInformation("Device secret rotated through the registration recovery window");
        return AuthResult<RegisteredDevice>.Ok(new RegisteredDevice(device.DeviceId, rotated));
    }

    /// <summary>
    /// Mint an access token (§3.3). There is no refresh token: refresh is another
    /// <c>deviceId</c> + <c>deviceSecret</c> exchange at this same entry point.
    /// </summary>
    public Task<AuthResult<MintedToken>> MintTokenAsync(
        string? deviceId, string? deviceSecret, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(deviceSecret))
            return Reject<MintedToken>("token_request_malformed");

        return store.InTransactionAsync(async (tx, token) =>
        {
            var device = await tx.FindDeviceAsync(deviceId, token);
            if (device is null)
                return Rejected<MintedToken>("device_unknown");

            if (!device.IsActive)
                return Rejected<MintedToken>("device_revoked");

            if (!hasher.Verify(deviceSecret, device.SecretHash))
                return Rejected<MintedToken>("device_secret_mismatch");

            var now = time.GetUtcNow();
            var tokenId = Credentials.NewRecordId();
            var secret = Credentials.NewSecret();

            await tx.SaveTokenAsync(new TokenRecord
            {
                TokenId = tokenId,
                TokenHash = hasher.Hash(secret),
                DeviceId = device.DeviceId,
                // Absolute, stored at issue time. A backward clock jump therefore cannot extend a
                // token's life, because nothing recomputes this at check time.
                ExpiresAt = now + AccessTokenTtl,
            }, token);

            if (device.FirstTokenMintedAt is null)
                await tx.SaveDeviceAsync(device with { FirstTokenMintedAt = now }, token);

            logger.LogInformation("Access token minted");
            return AuthResult<MintedToken>.Ok(new MintedToken(
                Credentials.Compose(tokenId, secret), (int)AccessTokenTtl.TotalSeconds));
        }, ct);
    }

    /// <summary>
    /// Resolve a bearer token to its principal. The principal comes from the device record and is
    /// never accepted from input (§1, §4).
    /// </summary>
    public Task<AuthResult<AuthenticatedPrincipal>> AuthenticateAsync(
        string? presentedToken, CancellationToken ct = default)
    {
        if (!Credentials.TryParse(presentedToken, out var tokenId, out var secret))
            return Reject<AuthenticatedPrincipal>("token_malformed");

        return store.InTransactionAsync(async (tx, ctx) =>
        {
            var record = await tx.FindTokenAsync(tokenId, ctx);
            if (record is null)
                return Rejected<AuthenticatedPrincipal>("token_unknown");

            if (record.RevokedAt is not null)
                return Rejected<AuthenticatedPrincipal>("token_revoked");

            if (time.GetUtcNow() >= record.ExpiresAt)
                return Rejected<AuthenticatedPrincipal>("token_expired");

            if (!hasher.Verify(secret, record.TokenHash))
                return Rejected<AuthenticatedPrincipal>("token_secret_mismatch");

            var device = await tx.FindDeviceAsync(record.DeviceId, ctx);
            if (device is null || !device.IsActive)
                return Rejected<AuthenticatedPrincipal>("token_device_revoked");

            return AuthResult<AuthenticatedPrincipal>.Ok(
                new AuthenticatedPrincipal(device.PrincipalId, device.DeviceId, tokenId));
        }, ct);
    }

    /// <summary>
    /// Self-revoke (§5.5). The device revokes itself and every token it holds.
    ///
    /// <para>There is deliberately no path here to revoke a device without authenticating as it:
    /// the owner has exactly one active device, so an unauthenticated revoke would be a
    /// one-request denial of service against the only way in.</para>
    /// </summary>
    public Task<AuthResult<bool>> RevokeSelfAsync(
        AuthenticatedPrincipal caller, string routeDeviceId, CancellationToken ct = default)
    {
        if (!string.Equals(caller.DeviceId, routeDeviceId, StringComparison.Ordinal))
        {
            // Answered as unauthorized rather than as a distinct code: telling a caller that the
            // device exists but is not theirs is an existence oracle.
            return Reject<bool>("revoke_device_mismatch");
        }

        return store.InTransactionAsync(async (tx, token) =>
        {
            var device = await tx.FindDeviceAsync(routeDeviceId, token);
            if (device is null)
                return Rejected<bool>("revoke_device_unknown");

            var now = time.GetUtcNow();
            if (device.IsActive)
                await tx.SaveDeviceAsync(device with { RevokedAt = now }, token);

            await tx.RevokeTokensForDeviceAsync(routeDeviceId, now, token);

            logger.LogInformation("Device self-revoked");
            return AuthResult<bool>.Ok(true);
        }, ct);
    }

    /// <summary>
    /// Log which check failed and return the single caller-visible answer.
    ///
    /// <para>Enrollment failure, unknown device, bad secret, expired token and revoked token are
    /// indistinguishable to the caller (§3.6). The distinguishing detail is logged — as a fixed
    /// reason label, never the presented value and never a prefix of it.</para>
    /// </summary>
    private AuthResult<T> Rejected<T>(string reason)
    {
        logger.LogInformation("Authentication rejected: {Reason}", reason);
        return AuthResult<T>.Fail(ProtocolErrorCode.Unauthorized);
    }

    private Task<AuthResult<T>> Reject<T>(string reason) => Task.FromResult(Rejected<T>(reason));
}
