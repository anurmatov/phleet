namespace Fleet.Comms.Auth;

/// <summary>What a revalidation pass concluded about a still-open connection.</summary>
public enum RevalidationOutcome
{
    /// <summary>The credential is still good. Keep the connection.</summary>
    Valid,

    /// <summary>The credential no longer authenticates. Close with 4401.</summary>
    Revoked,
}

/// <summary>
/// The component that makes revocation bounded rather than best-effort on a long-lived connection
/// (docs/first-party-api.md §3.5, §9).
///
/// <para>A socket authenticated at upgrade and never re-checked would keep a revoked device
/// connected for as long as it cared to stay. So the stream layer re-validates on a fixed
/// <b>30-second</b> interval and closes with <c>4401</c> when the check fails, rather than trusting
/// the value captured at upgrade. Thirty seconds is the worst case a revoking operator may rely on;
/// "eventually" would give the operator nothing to act on.</para>
///
/// <para><b>This slice ships the component, not the socket.</b> Issue #292 scope 6 asks for a
/// testable revalidation/connection-revocation component for the later WebSocket route and
/// explicitly forbids fabricating the Slice-4 stream or exposing a placeholder route — so there is
/// no endpoint here, and nothing registers this in a request pipeline yet.</para>
///
/// <para><b>The deadline is monotonic elapsed time, not wall time.</b> A connection records the
/// <see cref="TimeProvider.GetTimestamp"/> value of its last check, and the next check is due once
/// <see cref="TimeProvider.GetElapsedTime(long)"/> reaches the interval. Subtracting two
/// wall-clock readings would let a backward clock step push the next check minutes into the future
/// — silently turning the promised upper bound into no bound at all, on the one mechanism an
/// operator is told to rely on when revoking a stolen device.</para>
///
/// <para>Time is injected and the due-check is pure, so the 30-second bound is asserted by
/// arithmetic rather than by sleeping. A timer-based test of this would be slow and flaky, and
/// would get muted rather than fixed.</para>
/// </summary>
public sealed class CredentialRevalidator(AuthService auth, TimeProvider time)
{
    /// <summary>The fixed re-validation interval, and therefore the upper bound on revocation.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether a connection whose last check was <paramref name="sinceLastValidation"/> ago is due
    /// for another. Pure, so the bound is testable without a clock.
    /// </summary>
    public static bool IsDue(TimeSpan sinceLastValidation) => sinceLastValidation >= Interval;

    /// <summary>
    /// A monotonic stamp to record when a connection was last validated. The caller keeps this
    /// opaque value, not a <see cref="DateTimeOffset"/>.
    /// </summary>
    public long Stamp() => time.GetTimestamp();

    /// <summary>Whether this connection is due for a check right now.</summary>
    public bool IsDue(long lastValidatedStamp) => IsDue(time.GetElapsedTime(lastValidatedStamp));

    /// <summary>
    /// Re-validate the credential a connection was opened with.
    ///
    /// <para>Anything other than a clean success is <see cref="RevalidationOutcome.Revoked"/>,
    /// including a store outage: an auth lookup that cannot complete is never treated as a pass
    /// (§16, MUST NOT 18). On a live socket that means the connection closes rather than surviving
    /// on the strength of a check that did not happen.</para>
    /// </summary>
    public async Task<RevalidationOutcome> RevalidateAsync(string? token, CancellationToken ct = default)
    {
        try
        {
            var result = await auth.AuthenticateAsync(token, ct);
            return result.Succeeded ? RevalidationOutcome.Valid : RevalidationOutcome.Revoked;
        }
        catch (AuthStoreUnavailableException)
        {
            return RevalidationOutcome.Revoked;
        }
    }
}
