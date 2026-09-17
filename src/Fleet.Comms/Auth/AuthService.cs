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
///
/// <para><b>Two properties are structural here and easy to break by rearranging a method.</b></para>
///
/// <para><i>Every credential lookup spends exactly one KDF evaluation.</i> The verify call comes
/// immediately after the lookup and before any state test, so "no such record", "revoked",
/// "expired" and "wrong secret" all cost the same. Moving a cheap state test above the verify
/// re-opens a timing oracle for record existence (§12: "no oracle to grind against"), which the
/// byte-identical bodies alone do not close.</para>
///
/// <para><i>Expiry is irreversible.</i> Deadlines are compared against <see cref="MonotonicClock"/>
/// rather than raw wall time, and a credential observed past its deadline is burned in the store on
/// the spot. The clock covers a backward step while the process lives; the burn covers the restart
/// after which the clock must re-anchor to the host.</para>
/// </summary>
public sealed class AuthService(
    IAuthStore store,
    ISecretHasher hasher,
    MonotonicClock clock,
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
        var now = clock.GetUtcNow();

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
            //
            // No dummy verify here, unlike the lookup paths below. The equalisation exists to hide
            // whether a *record* exists; whether the caller's own input parsed is something the
            // caller already knows, and spending a KDF on unparseable junk would only hand an
            // unauthenticated caller a cheap way to buy server work.
            return Reject<RegisteredDevice>("enrollment_code_malformed");
        }

        return store.InTransactionAsync(async (tx, token) =>
        {
            var enrollment = await tx.FindEnrollmentAsync(enrollmentId, token);

            // One KDF evaluation, always, before anything branches on what was found.
            var verified = hasher.VerifyOrDummy(presentedSecret, enrollment?.CodeHash);

            if (enrollment is null)
                return Rejected<RegisteredDevice>("enrollment_unknown");

            if (!verified)
                return Rejected<RegisteredDevice>("enrollment_secret_mismatch");

            if (!enrollment.IsUsable)
                return Rejected<RegisteredDevice>("enrollment_burned");

            var now = clock.GetUtcNow();

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
            return await BurnAsync(tx, enrollment, now, "enrollment_expired", ct);

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
            return await BurnAsync(tx, enrollment, now, "recovery_window_expired", ct);

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

            // Before the IsActive test, not after: a revoked device that answered faster than a
            // live one with a bad secret would say "this id is real" just as loudly as a 200.
            var verified = hasher.VerifyOrDummy(deviceSecret, device?.SecretHash);

            if (device is null)
                return Rejected<MintedToken>("device_unknown");

            if (!verified)
                return Rejected<MintedToken>("device_secret_mismatch");

            if (!device.IsActive)
                return Rejected<MintedToken>("device_revoked");

            var now = clock.GetUtcNow();
            var tokenId = Credentials.NewRecordId();
            var secret = Credentials.NewSecret();

            await tx.SaveTokenAsync(new TokenRecord
            {
                TokenId = tokenId,
                TokenHash = hasher.Hash(secret),
                DeviceId = device.DeviceId,
                // Absolute, stored at issue time, and read back against the monotonic clock — so
                // neither recomputation nor a backward clock step can extend a token's life.
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
            var verified = hasher.VerifyOrDummy(secret, record?.TokenHash);

            if (record is null)
                return Rejected<AuthenticatedPrincipal>("token_unknown");

            if (!verified)
                return Rejected<AuthenticatedPrincipal>("token_secret_mismatch");

            if (record.RevokedAt is not null)
                return Rejected<AuthenticatedPrincipal>("token_revoked");

            var now = clock.GetUtcNow();
            if (now >= record.ExpiresAt)
            {
                // Burned, not merely refused. Without this, a restart re-anchors the clock and an
                // already-refused token could authenticate again on a host whose time moved back.
                await tx.SaveTokenAsync(record with { RevokedAt = now }, ctx);
                return Rejected<AuthenticatedPrincipal>("token_expired");
            }

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

            var now = clock.GetUtcNow();
            if (device.IsActive)
                await tx.SaveDeviceAsync(device with { RevokedAt = now }, token);

            await tx.RevokeTokensForDeviceAsync(routeDeviceId, now, token);

            logger.LogInformation("Device self-revoked");
            return AuthResult<bool>.Ok(true);
        }, ct);
    }

    // ── operator path ────────────────────────────────────────────────────────
    //
    // Reachable only from the CLI subcommands, never from a route (docs/first-party-api.md §5.5).
    // They live here rather than in the command so the credential format, the Argon2id parameters
    // and the transaction boundaries stay in exactly one place — a second implementation of any of
    // those is the defect that fails silently, permanently, and only for real users.

    /// <summary>
    /// Issue an enrollment code, refusing when the principal already holds an active device.
    ///
    /// <para>Returns null rather than throwing: "already has a device" is an answer the operator
    /// asked for, and the command turns it into an actionable non-zero exit. The check and the
    /// insert share one transaction, so a refusal leaves no enrollment row behind and a race
    /// cannot slip a second code past a concurrent registration.</para>
    ///
    /// <para>This does not replace the registration-time limit — that one still rejects a second
    /// device with <c>409 device_limit</c>, and remains the guard against a code issued before a
    /// device existed being presented after one does.</para>
    /// </summary>
    public async Task<string?> IssueEnrollmentCodeForOperatorAsync(
        string principalId, CancellationToken ct = default)
    {
        var enrollmentId = Credentials.NewRecordId();
        var secret = Credentials.NewSecret();
        var now = clock.GetUtcNow();

        var issued = await store.InTransactionAsync(async (tx, token) =>
        {
            if (await tx.CountActiveDevicesAsync(principalId, token) > 0)
                return false;

            await tx.SaveEnrollmentAsync(new EnrollmentRecord
            {
                EnrollmentId = enrollmentId,
                CodeHash = hasher.Hash(secret),
                PrincipalId = principalId,
                ExpiresAt = now + EnrollmentCodeTtl,
            }, token);
            return true;
        }, ct);

        if (!issued)
        {
            logger.LogInformation("Enrollment refused: the principal already has an active device");
            return null;
        }

        logger.LogInformation("Enrollment code issued for a principal");
        return Credentials.Compose(enrollmentId, secret);
    }

    /// <summary>
    /// Every device the operator may need to act on, optionally narrowed to one principal.
    ///
    /// <para>Revoked devices are included. The operator is either choosing a device to revoke or
    /// checking that a revocation took, and a list that hid revoked rows would answer the second
    /// question with silence — the same output as a device that never existed.</para>
    /// </summary>
    public Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(
        string? principalId = null, CancellationToken ct = default) =>
        store.InTransactionAsync((tx, token) => tx.ListDevicesAsync(principalId, token), ct);

    /// <summary>
    /// Revoke a device the operator names, without authenticating as it — the lost, stolen or
    /// bricked phone (§5.5, and the row <c>docs/first-party-api.md</c> defers to "the deployment's
    /// own administrative path").
    ///
    /// <para><b>This is why it is not an HTTP route.</b> The owner has exactly one active device,
    /// so an unauthenticated revoke endpoint would be a one-request denial of service against the
    /// only way in. As a subcommand the authorisation is possession of the store itself, which is
    /// the same thing as being the operator.</para>
    ///
    /// <para>Returns false for an unknown device — not an exception, because "there is no such
    /// device" is an answer the operator asked for, and the command turns it into a clear non-zero
    /// exit. Revoking an already-revoked device succeeds: the operator's intent is a state, not a
    /// transition, and the alternative is a scary error for the safest possible retry.</para>
    /// </summary>
    public Task<bool> RevokeDeviceAsync(string deviceId, CancellationToken ct = default) =>
        store.InTransactionAsync(async (tx, token) =>
        {
            var device = await tx.FindDeviceAsync(deviceId, token);
            if (device is null)
                return false;

            var now = clock.GetUtcNow();
            if (device.IsActive)
                await tx.SaveDeviceAsync(device with { RevokedAt = now }, token);

            // Same transaction as the device itself, exactly as RevokeSelfAsync does: a device
            // marked revoked while its tokens still authenticate is not a revocation.
            await tx.RevokeTokensForDeviceAsync(deviceId, now, token);

            logger.LogInformation("Device revoked by the operator");
            return true;
        }, ct);

    /// <summary>
    /// Revoke every device and every token the store holds, in one transaction.
    ///
    /// <para>For the disaster-restore path. A restore reinstates whatever credentials the snapshot
    /// contained, including devices revoked after it was taken, and a device secret is long-lived —
    /// so expiring the restored access tokens is not enough, the restored device can mint more.
    /// Revoking device by device after the service is reachable leaves a window; this closes all of
    /// them before ingress reopens.</para>
    ///
    /// <para>Returns how many devices were still active and how many unconsumed enrollment codes
    /// were burned, so the operator sees what the snapshot actually brought back rather than a bare
    /// success.</para>
    /// </summary>
    public Task<(int Devices, int Enrollments)> RevokeAllDevicesAsync(CancellationToken ct = default) =>
        store.InTransactionAsync(async (tx, token) =>
        {
            var now = clock.GetUtcNow();
            var revoked = 0;

            foreach (var device in await tx.ListDevicesAsync(null, token))
            {
                if (device.IsActive)
                {
                    await tx.SaveDeviceAsync(device with { RevokedAt = now }, token);
                    revoked++;
                }

                // Tokens for every device, active or already revoked: a device revoked before the
                // snapshot still has its tokens restored alongside it.
                await tx.RevokeTokensForDeviceAsync(device.DeviceId, now, token);
            }

            // Unconsumed enrollment codes too. A snapshot restores those alongside devices, and an
            // unconsumed code registers a device for the rest of its TTL — closing the front door
            // while restoring a key to the back one is not invalidation.
            var burned = await tx.RevokeAllEnrollmentsAsync(now, token);

            logger.LogInformation("All devices and enrollment codes revoked by the operator");
            return (revoked, burned);
        }, ct);

    /// <summary>
    /// Record that a credential has passed a deadline, then refuse it like any other failure.
    ///
    /// <para>The write is the point. A refusal that leaves the record untouched is only as durable
    /// as the clock that produced it.</para>
    /// </summary>
    private async Task<AuthResult<RegisteredDevice>> BurnAsync(
        IAuthStoreTransaction tx, EnrollmentRecord enrollment, DateTimeOffset now,
        string reason, CancellationToken ct)
    {
        await tx.SaveEnrollmentAsync(enrollment with { RevokedAt = now }, ct);
        return Rejected<RegisteredDevice>(reason);
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
