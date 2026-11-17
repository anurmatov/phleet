using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;

namespace Fleet.Shared;

/// <summary>
/// A Temporal bearer token setting that cannot be used. The message names the setting and never
/// the value or any part of it.
/// </summary>
public sealed class TemporalBearerTokenException(string message) : Exception(message);

/// <summary>Where a token's own <c>exp</c> claim puts it, measured against this service's clock.</summary>
public enum TemporalTokenExpiryState
{
    /// <summary>More than <see cref="TemporalBearerToken.ExpiryWarning"/> away.</summary>
    Valid,

    /// <summary>Less than <see cref="TemporalBearerToken.ExpiryWarning"/> away.</summary>
    ExpiresSoon,

    /// <summary>Already past.</summary>
    Expired,
}

/// <summary>What a startup log says of a token's expiry: the instant, and nothing else from the token.</summary>
public sealed record TemporalTokenExpiryNotice(TemporalTokenExpiryState State, DateTimeOffset ExpiresAt, string Message);

/// <summary>
/// The one rule set for the optional bearer token a service sends to Temporal, shared by the
/// orchestrator (<c>Temporal:BearerToken</c>) and the Temporal bridge
/// (<c>TemporalBridge:BearerToken</c>). Plain .NET: each service puts the header on the SDK's
/// options in its own <c>TemporalConnect</c>, the only place it constructs a Temporal client.
///
/// Not set (absent or empty, which is how an unset compose variable arrives): no header, as before
/// the setting existed. Set: it must be the token alone, usable as it is in an HTTP header; anything
/// else is a fault, and there is no shape that quietly connects without the header. The token is
/// opaque here: it need not be a JWT, and nothing is verified. Transport is not this rule's
/// business and is unchanged by it.
/// </summary>
public static class TemporalBearerToken
{
    /// <summary>The setting's name within each service's Temporal section.</summary>
    public const string SettingName = "BearerToken";

    /// <summary>A token whose own expiry is nearer than this is warned about at startup.</summary>
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(30);

    private const string Scheme = "Bearer";

    /// <summary>Whether a token is configured. Empty is not: an unset variable arrives as an empty string.</summary>
    public static bool IsSet(string? token) => !string.IsNullOrEmpty(token);

    /// <summary>
    /// What makes the configured token unusable, or null (also null when none is configured).
    /// </summary>
    /// <param name="setting">The setting as the message names it (<c>Temporal:BearerToken</c>).</param>
    public static string? DescribeFault(string? token, string setting)
    {
        if (!IsSet(token)) return null;

        if (string.IsNullOrWhiteSpace(token))
            return $"{setting} is set to blanks only. Give the token, or leave the setting empty to send none. {NothingConnected}";

        // Before the blank check below: "Bearer abc" has a blank in it, and this is the mistake to name.
        if (token.TrimStart().StartsWith(Scheme + " ", StringComparison.OrdinalIgnoreCase))
            return $"{setting} starts with \"{Scheme} \". Give the token alone: this service adds \"{Scheme} \" itself. {NothingConnected}";

        foreach (var c in token)
        {
            if (char.IsWhiteSpace(c))
                return $"{setting} has a blank or a line break in it (also at its start or end). A token has none. {NothingConnected}";
            // Printable ASCII is what an HTTP header value, and gRPC metadata, can carry.
            if (c is < '!' or > '~')
                return $"{setting} has a character in it that cannot be sent in an HTTP header (a control or a non-ASCII character). {NothingConnected}";
        }

        return null;
    }

    /// <summary>
    /// The <c>Authorization</c> header value for the configured token, or null when none is
    /// configured (no header is sent). Never a header for a token that is not usable.
    /// </summary>
    /// <exception cref="TemporalBearerTokenException">The token is set and not usable.</exception>
    public static string? AuthorizationOrNull(string? token, string setting)
    {
        if (DescribeFault(token, setting) is { } fault)
            throw new TemporalBearerTokenException(fault);
        return IsSet(token) ? $"{Scheme} {token}" : null;
    }

    /// <summary>
    /// What to say at startup about the token's expiry, or null: when no token is configured, when
    /// it is not usable, and when it does not read as a JWT with a numeric <c>exp</c> (an opaque
    /// token says nothing about itself). An aid for upkeep only: no signature is checked, no other
    /// claim is read, and an expired token does not stop the service, because the server decides
    /// and clocks differ. The message carries the instant and no part of the token.
    /// </summary>
    public static TemporalTokenExpiryNotice? DescribeExpiry(string? token, string setting, DateTimeOffset now)
    {
        if (DescribeFault(token, setting) is not null || ReadExpiry(token) is not { } expiresAt)
            return null;

        var at = expiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        if (expiresAt <= now)
            return new(TemporalTokenExpiryState.Expired, expiresAt,
                $"The Temporal bearer token ({setting}) expired at {at} by its own exp claim. Temporal is expected to refuse every call; "
                + "replace the token and restart this service. Starting anyway: the server decides, and clocks differ.");
        if (expiresAt - now < ExpiryWarning)
            return new(TemporalTokenExpiryState.ExpiresSoon, expiresAt,
                $"The Temporal bearer token ({setting}) expires at {at}, less than {ExpiryWarning.Days} days from now. "
                + "Replace it and restart this service before then: after that every Temporal call is refused.");
        return new(TemporalTokenExpiryState.Valid, expiresAt, $"The Temporal bearer token ({setting}) expires at {at}.");
    }

    /// <summary>The <c>exp</c> of a token that reads as a JWT, or null. Nothing else is read.</summary>
    private static DateTimeOffset? ReadExpiry(string? token)
    {
        if (!IsSet(token)) return null;
        var parts = token!.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0)) return null;

        try
        {
            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            if (payload.RootElement.ValueKind != JsonValueKind.Object
                || !payload.RootElement.TryGetProperty("exp", out var exp)
                || exp.ValueKind != JsonValueKind.Number
                || !exp.TryGetDouble(out var seconds)
                || double.IsNaN(seconds))
                return null;

            // Outside what a DateTimeOffset holds is not a date this can say anything about.
            var whole = Math.Floor(seconds);
            if (whole < DateTimeOffset.MinValue.ToUnixTimeSeconds() || whole > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                return null;
            return DateTimeOffset.FromUnixTimeSeconds((long)whole);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private const string NothingConnected = "Nothing was connected.";
}
