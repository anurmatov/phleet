namespace Fleet.Orchestrator.Auth;

/// <summary>The two answers a caller can be given when sign-in refuses.</summary>
public enum AuthError
{
    /// <summary>Every credential failure. Deliberately one value: see <see cref="AuthService"/>.</summary>
    Unauthorized,

    /// <summary>The user already has <see cref="AuthService.MaxActiveDevices"/> active devices.</summary>
    DeviceLimit,
}

/// <summary>The outcome of an auth operation. Failure carries a code, never a reason string.</summary>
public readonly record struct AuthResult<T>(T? Value, AuthError? Error)
{
    public bool Succeeded => Error is null;

    public static AuthResult<T> Ok(T value) => new(value, null);

    public static AuthResult<T> Fail(AuthError code) => new(default, code);
}

public sealed record IssuedEnrollment(string EnrollmentCode, int ExpiresInSeconds);

public sealed record RegisteredDevice(string DeviceId, string DeviceSecret);

public sealed record MintedToken(string AccessToken, int ExpiresInSeconds);

public sealed record BrowserSession(string DeviceId, string SessionToken, DateTimeOffset ExpiresAt);

/// <summary>Who a verified token belongs to. Derived from stored records, never from input.</summary>
/// <param name="Extended">
/// A session token's deadline was moved forward by this check, so the cookie that carries it should
/// be sent again with the new lifetime.
/// </param>
public sealed record AuthenticatedDevice(
    string PrincipalId,
    string DeviceId,
    string TokenId,
    DeviceKind Kind,
    DateTimeOffset ExpiresAt,
    bool Extended);

/// <summary>
/// Every fixed behaviour of sign-in, in one place (docs/sign-in.md).
///
/// <para>Policy lives here rather than in the store so that a store implementation has to satisfy
/// an interface about records, not about rules.</para>
///
/// <para><b>Two properties are structural here and easy to break by rearranging a method.</b></para>
///
/// <para><i>Every credential lookup spends exactly one KDF evaluation.</i> The verify call comes
/// immediately after the lookup and before any state test, so "no such record", "revoked",
/// "expired" and "wrong secret" all cost the same. Moving a cheap state test above the verify
/// re-opens a timing oracle for record existence, which the identical answers alone do not
/// close.</para>
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
    /// <summary>Enrollment-code absolute lifetime from issue.</summary>
    public static readonly TimeSpan EnrollmentCodeTtl = TimeSpan.FromMinutes(15);

    /// <summary>Access-token absolute lifetime from issue. Never extended.</summary>
    public static readonly TimeSpan AccessTokenTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The registration-recovery window, measured from FIRST REGISTRATION and deliberately
    /// independent of the code's own issue lifetime. Applying both would give a registration made
    /// late in a code's life a truncated — possibly zero-length — window, which is exactly when a
    /// client most needs one.
    /// </summary>
    public static readonly TimeSpan RegistrationRecoveryWindow = TimeSpan.FromMinutes(15);

    /// <summary>How long a browser stays signed in without being used.</summary>
    public static readonly TimeSpan SessionTtl = TimeSpan.FromDays(30);

    /// <summary>A session presented with less than this left is given a full lifetime again.</summary>
    public static readonly TimeSpan SessionExtendBelow = TimeSpan.FromDays(15);

    /// <summary>
    /// Active devices one user may hold. A person's browsers, phones and tablets fit many times
    /// over; the cap exists so that a leaked way of adding devices cannot add them without end.
    /// </summary>
    public const int MaxActiveDevices = 20;

    /// <summary>Longest a device name is stored. Longer names are cut, not refused.</summary>
    public const int MaxDeviceNameLength = 64;

    /// <summary>How stale <see cref="DeviceRecord.LastSeenAt"/> may be before a check rewrites it.</summary>
    public static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(5);

    /// <summary>How long a spent token or enrollment row is kept before it is deleted.</summary>
    public static readonly TimeSpan SpentRetention = TimeSpan.FromDays(1);

    // ── users ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Make sure the one user exists, and that its Telegram id matches the deployment's.
    ///
    /// <para><paramref name="telegramUserId"/> is the deployment's primary human
    /// (<c>FLEET_PRIMARY_HUMAN_USER_ID</c>). When it is set it replaces the stored value, because
    /// that setting is what provisioning already treats as the owner on Telegram and the two must
    /// not disagree. When it is not set the stored value is left alone.</para>
    /// </summary>
    public Task EnsureOwnerAsync(long? telegramUserId, CancellationToken ct = default) =>
        store.InTransactionAsync<object?>(async (tx, token) =>
        {
            var owner = await tx.FindUserAsync(UserRecord.OwnerId, token);
            if (owner is null)
            {
                await tx.SaveUserAsync(new UserRecord
                {
                    UserId = UserRecord.OwnerId,
                    DisplayName = "Owner",
                    TelegramUserId = telegramUserId,
                    CreatedAt = clock.GetUtcNow(),
                }, token);
                logger.LogInformation("Sign-in: the owner user was created");
            }
            else if (telegramUserId is not null && owner.TelegramUserId != telegramUserId)
            {
                await tx.SaveUserAsync(owner with { TelegramUserId = telegramUserId }, token);
                logger.LogInformation("Sign-in: the owner's Telegram id was updated from the deployment setting");
            }

            return null;
        }, ct);

    public Task<UserRecord?> FindUserAsync(string userId, CancellationToken ct = default) =>
        store.InTransactionAsync((tx, token) => tx.FindUserAsync(userId, token), ct);

    // ── enrollment ───────────────────────────────────────────────────────────

    /// <summary>
    /// Issue a one-time code that registers one app device for <paramref name="principalId"/>.
    ///
    /// <para>Refused with <see cref="AuthError.DeviceLimit"/> when the user is already at the cap,
    /// so the person adding a device is told now and not by the device a minute later. The
    /// registration-time check stays: it is the guard against a code issued below the cap being
    /// presented at it.</para>
    /// </summary>
    public async Task<AuthResult<IssuedEnrollment>> IssueEnrollmentCodeAsync(
        string principalId, string? deviceName, CancellationToken ct = default)
    {
        var enrollmentId = Credentials.NewRecordId();
        var secret = Credentials.NewSecret();
        var codeHash = hasher.Hash(secret);
        var name = CleanName(deviceName);

        var error = await store.InTransactionAsync<AuthError?>(async (tx, token) =>
        {
            if (await tx.FindUserAsync(principalId, token) is null)
                return AuthError.Unauthorized;

            var now = clock.GetUtcNow();
            await tx.DeleteSpentAsync(now - SpentRetention, token);
            await RetireAbandonedBrowsersAsync(tx, principalId, now, token);

            if (await tx.CountActiveDevicesAsync(principalId, token) >= MaxActiveDevices)
                return AuthError.DeviceLimit;

            await tx.SaveEnrollmentAsync(new EnrollmentRecord
            {
                EnrollmentId = enrollmentId,
                CodeHash = codeHash,
                PrincipalId = principalId,
                DeviceName = name,
                ExpiresAt = now + EnrollmentCodeTtl,
            }, token);
            return null;
        }, ct);

        if (error is not null)
        {
            logger.LogInformation("Enrollment code refused: {Reason}", error.Value);
            return AuthResult<IssuedEnrollment>.Fail(error.Value);
        }

        logger.LogInformation("Enrollment code issued");
        return AuthResult<IssuedEnrollment>.Ok(new IssuedEnrollment(
            Credentials.Compose(enrollmentId, secret), (int)EnrollmentCodeTtl.TotalSeconds));
    }

    /// <summary>
    /// Register a device, or recover a registration whose response was lost.
    ///
    /// <para>Both outcomes come out of one transaction, because the decision between them depends
    /// on state that must not move underneath it.</para>
    /// </summary>
    /// <param name="deviceName">
    /// What the device calls itself. Used only when the code was issued without a name: the person
    /// who issued the code is the one who knows what they are adding.
    /// </param>
    public Task<AuthResult<RegisteredDevice>> RegisterDeviceAsync(
        string? enrollmentCode, string? deviceName, CancellationToken ct = default)
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

        var presentedName = CleanName(deviceName);

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
                ? await FirstRegistrationAsync(tx, enrollment, presentedName, now, token)
                : await RecoverRegistrationAsync(tx, enrollment, now, token);
        }, ct);
    }

    private async Task<AuthResult<RegisteredDevice>> FirstRegistrationAsync(
        IAuthStoreTransaction tx, EnrollmentRecord enrollment, string presentedName,
        DateTimeOffset now, CancellationToken ct)
    {
        // The issue lifetime bounds how long an UNUSED code may sit around.
        if (now >= enrollment.ExpiresAt)
            return await BurnAsync(tx, enrollment, now, "enrollment_expired", ct);

        await RetireAbandonedBrowsersAsync(tx, enrollment.PrincipalId, now, ct);

        // A new device never displaces an existing one. Making room is an explicit revoke by the
        // person who owns the devices — an implicit one would let a stolen code silently remove a
        // working device.
        if (await tx.CountActiveDevicesAsync(enrollment.PrincipalId, ct) >= MaxActiveDevices)
        {
            logger.LogInformation("Device registration refused: the user is at the device limit");
            return AuthResult<RegisteredDevice>.Fail(AuthError.DeviceLimit);
        }

        var deviceId = Credentials.NewRecordId();
        var secret = Credentials.NewSecret();

        await tx.SaveDeviceAsync(new DeviceRecord
        {
            DeviceId = deviceId,
            PrincipalId = enrollment.PrincipalId,
            Kind = DeviceKind.App,
            Name = enrollment.DeviceName.Length > 0 ? enrollment.DeviceName : presentedName,
            SecretHash = hasher.Hash(secret),
            EnrollmentId = enrollment.EnrollmentId,
            RegisteredAt = now,
        }, ct);

        // Consumed in the SAME transaction as the device it produced. A partial consume would
        // leave the code both spent and unusable, and lock its holder out with no recovery.
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

    // ── tokens ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Mint an access token. There is no refresh token: refresh is another <c>deviceId</c> +
    /// <c>deviceSecret</c> exchange at this same entry point.
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
            // A browser has an empty hash, which is treated as no hash at all.
            var verified = hasher.VerifyOrDummy(
                deviceSecret, string.IsNullOrEmpty(device?.SecretHash) ? null : device.SecretHash);

            if (device is null)
                return Rejected<MintedToken>("device_unknown");

            if (!verified)
                return Rejected<MintedToken>("device_secret_mismatch");

            if (!device.IsActive)
                return Rejected<MintedToken>("device_revoked");

            var now = clock.GetUtcNow();
            var tokenId = Credentials.NewRecordId();
            var secret = Credentials.NewSecret();

            await tx.DeleteSpentAsync(now - SpentRetention, token);
            await tx.SaveTokenAsync(new TokenRecord
            {
                TokenId = tokenId,
                TokenHash = hasher.Hash(secret),
                DeviceId = device.DeviceId,
                Kind = TokenKind.Access,
                // Absolute, stored at issue time, and read back against the monotonic clock — so
                // neither recomputation nor a backward clock step can extend a token's life.
                ExpiresAt = now + AccessTokenTtl,
            }, token);

            await tx.SaveDeviceAsync(device with
            {
                FirstTokenMintedAt = device.FirstTokenMintedAt ?? now,
                LastSeenAt = now,
            }, token);

            logger.LogInformation("Access token minted");
            return AuthResult<MintedToken>.Ok(new MintedToken(
                Credentials.Compose(tokenId, secret), (int)AccessTokenTtl.TotalSeconds));
        }, ct);
    }

    /// <summary>
    /// Sign a browser in: a new browser device and the session token its cookie will carry.
    ///
    /// <para>The caller has already established that this browser may act as
    /// <paramref name="principalId"/> — by checking the bootstrap token, or because the
    /// orchestrator gates nothing. That decision is not made here.</para>
    /// </summary>
    public Task<AuthResult<BrowserSession>> SignInBrowserAsync(
        string principalId, string? deviceName, CancellationToken ct = default)
    {
        var name = CleanName(deviceName);

        return store.InTransactionAsync(async (tx, token) =>
        {
            if (await tx.FindUserAsync(principalId, token) is null)
                return Rejected<BrowserSession>("browser_user_unknown");

            var now = clock.GetUtcNow();
            await tx.DeleteSpentAsync(now - SpentRetention, token);
            await RetireAbandonedBrowsersAsync(tx, principalId, now, token);

            if (await tx.CountActiveDevicesAsync(principalId, token) >= MaxActiveDevices)
            {
                logger.LogInformation("Browser sign-in refused: the user is at the device limit");
                return AuthResult<BrowserSession>.Fail(AuthError.DeviceLimit);
            }

            var deviceId = Credentials.NewRecordId();
            var tokenId = Credentials.NewRecordId();
            var secret = Credentials.NewSecret();
            var expiresAt = now + SessionTtl;

            await tx.SaveDeviceAsync(new DeviceRecord
            {
                DeviceId = deviceId,
                PrincipalId = principalId,
                Kind = DeviceKind.Browser,
                Name = name,
                SecretHash = "",
                RegisteredAt = now,
                LastSeenAt = now,
            }, token);

            await tx.SaveTokenAsync(new TokenRecord
            {
                TokenId = tokenId,
                TokenHash = hasher.Hash(secret),
                DeviceId = deviceId,
                Kind = TokenKind.Session,
                ExpiresAt = expiresAt,
            }, token);

            logger.LogInformation("Browser signed in");
            return AuthResult<BrowserSession>.Ok(
                new BrowserSession(deviceId, Credentials.Compose(tokenId, secret), expiresAt));
        }, ct);
    }

    /// <summary>
    /// Resolve a presented token to its device and user.
    ///
    /// <para><paramref name="expected"/> is how the token arrived: a bearer must be an access token
    /// and a cookie must be a session token. A session token copied into a header, or an access
    /// token planted in a cookie, is refused like any unknown value — each kind keeps the
    /// protections of the place it is meant to live.</para>
    /// </summary>
    public Task<AuthResult<AuthenticatedDevice>> AuthenticateAsync(
        string? presentedToken, TokenKind expected, CancellationToken ct = default)
    {
        if (!Credentials.TryParse(presentedToken, out var tokenId, out var secret))
            return Reject<AuthenticatedDevice>("token_malformed");

        return store.InTransactionAsync(async (tx, ctx) =>
        {
            var record = await tx.FindTokenAsync(tokenId, ctx);
            var verified = hasher.VerifyOrDummy(secret, record?.TokenHash);

            if (record is null)
                return Rejected<AuthenticatedDevice>("token_unknown");

            if (!verified)
                return Rejected<AuthenticatedDevice>("token_secret_mismatch");

            if (record.Kind != expected)
                return Rejected<AuthenticatedDevice>("token_wrong_carrier");

            if (record.RevokedAt is not null)
                return Rejected<AuthenticatedDevice>("token_revoked");

            var now = clock.GetUtcNow();
            if (now >= record.ExpiresAt)
            {
                // Burned, not merely refused. Without this, a restart re-anchors the clock and an
                // already-refused token could authenticate again on a host whose time moved back.
                await tx.SaveTokenAsync(record with { RevokedAt = now }, ctx);
                return Rejected<AuthenticatedDevice>("token_expired");
            }

            var device = await tx.FindDeviceAsync(record.DeviceId, ctx);
            if (device is null || !device.IsActive)
                return Rejected<AuthenticatedDevice>("token_device_revoked");

            var expiresAt = record.ExpiresAt;
            var extended = false;
            if (record.Kind == TokenKind.Session && expiresAt - now < SessionExtendBelow)
            {
                // Only a session, only while it is still valid, and only by the request that
                // presented it. An access token's deadline never moves.
                expiresAt = now + SessionTtl;
                extended = true;
                await tx.SaveTokenAsync(record with { ExpiresAt = expiresAt }, ctx);
            }

            if (device.LastSeenAt is null || now - device.LastSeenAt.Value >= LastSeenResolution)
                await tx.SaveDeviceAsync(device with { LastSeenAt = now }, ctx);

            return AuthResult<AuthenticatedDevice>.Ok(new AuthenticatedDevice(
                device.PrincipalId, device.DeviceId, tokenId, device.Kind, expiresAt, extended));
        }, ct);
    }

    // ── devices ──────────────────────────────────────────────────────────────

    /// <summary>One user's devices, revoked ones included, oldest first.</summary>
    public Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(
        string principalId, CancellationToken ct = default) =>
        store.InTransactionAsync((tx, token) => tx.ListDevicesAsync(principalId, token), ct);

    /// <summary>
    /// Revoke a device and every token it holds, in one transaction.
    ///
    /// <para>Scoped to <paramref name="principalId"/>: a device that belongs to someone else is
    /// answered exactly as one that does not exist. Returns false for those; revoking a device that
    /// is already revoked succeeds, because the caller's intent is a state, not a transition.</para>
    /// </summary>
    public Task<bool> RevokeDeviceAsync(
        string principalId, string deviceId, CancellationToken ct = default) =>
        store.InTransactionAsync(async (tx, token) =>
        {
            var device = await tx.FindDeviceAsync(deviceId, token);
            if (device is null || !string.Equals(device.PrincipalId, principalId, StringComparison.Ordinal))
                return false;

            var now = clock.GetUtcNow();
            if (device.IsActive)
                await tx.SaveDeviceAsync(device with { RevokedAt = now }, token);

            // Same transaction as the device itself: a device marked revoked while its tokens
            // still authenticate is not a revocation.
            await tx.RevokeTokensForDeviceAsync(deviceId, now, token);
            await tx.RemovePushDevicesAsync(deviceId, token);

            logger.LogInformation("Device revoked");
            return true;
        }, ct);

    /// <summary>
    /// Revoke browser devices that can no longer be used: every session they held has expired or
    /// been revoked. A browser gets a new device each time it signs in, so without this a person
    /// who simply stopped using a browser would hold a slot of the cap forever.
    /// </summary>
    private static async Task RetireAbandonedBrowsersAsync(
        IAuthStoreTransaction tx, string principalId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var device in await tx.ListDevicesAsync(principalId, ct))
        {
            if (device.Kind != DeviceKind.Browser || !device.IsActive)
                continue;

            if (!await tx.HasLiveTokenAsync(device.DeviceId, now, ct))
                await tx.SaveDeviceAsync(device with { RevokedAt = now }, ct);
        }
    }

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
    /// <para>An unknown code, an unknown device, a bad secret, an expired token and a revoked token
    /// are indistinguishable to the caller. The distinguishing detail is logged — as a fixed reason
    /// label, never the presented value and never a prefix of it.</para>
    /// </summary>
    private AuthResult<T> Rejected<T>(string reason)
    {
        logger.LogInformation("Authentication rejected: {Reason}", reason);
        return AuthResult<T>.Fail(AuthError.Unauthorized);
    }

    private Task<AuthResult<T>> Reject<T>(string reason) => Task.FromResult(Rejected<T>(reason));

    /// <summary>A name fit to store and show: one line, no control characters, bounded.</summary>
    internal static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var kept = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return kept.Length <= MaxDeviceNameLength ? kept : kept[..MaxDeviceNameLength].TrimEnd();
    }
}
