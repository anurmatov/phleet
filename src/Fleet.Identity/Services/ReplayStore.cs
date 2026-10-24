using System.Text.Json;
using Fleet.Shared.Security;

namespace Fleet.Identity.Services;

public sealed class ReplayStore(LaunchIdentityStore launches)
{
    public Task ConsumeAsync(VerifiedAgentIdentity identity, string jti, string assertion, string? session, CancellationToken ct)
    {
        // Assertion has already passed the pinned verifier. Preserve its exact expiry, never extend replay lifetime.
        using var json = JsonDocument.Parse(JwtEncoding.Decode(assertion.Split('.')[1]));
        var expires = DateTimeOffset.FromUnixTimeSeconds(json.RootElement.GetProperty("exp").GetInt64()).UtcDateTime;
        return launches.ConsumeAsync(identity, jti, expires, session, ct);
    }
}
