using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Fleet.LocalEndpoint;

/// <summary>
/// A token in the shape of a JWT, made for one test: a header, a payload with invented claims and a
/// "signature" of random bytes. No key exists for it and nothing verifies it; it is here for what a
/// service says about a token and for what it sends, never for what a server would accept.
/// </summary>
internal sealed class SyntheticJwt
{
    public string Header { get; }
    public string Payload { get; }
    public string Signature { get; }

    /// <summary>The three parts joined: the token as a deployment would configure it.</summary>
    public string Token => $"{Header}.{Payload}.{Signature}";

    /// <summary>Everything a log line or a message must not contain: the token and each of its parts.</summary>
    public IReadOnlyList<string> Secrets => [Token, Header, Payload, Signature, Subject];

    /// <summary>A claim value from the payload, to look for in what must not carry it.</summary>
    public string Subject { get; } = "synthetic-subject-" + Guid.NewGuid().ToString("N")[..12];

    /// <param name="expClaim">The <c>exp</c> member as JSON (<c>1800000000</c>, <c>"soon"</c>), or null for none.</param>
    public SyntheticJwt(string? expClaim)
    {
        Header = Encode("""{"alg":"ES256","typ":"JWT","kid":"synthetic-key-1"}""");
        var exp = expClaim is null ? "" : $""","exp":{expClaim}""";
        Payload = Encode($$"""{"sub":"{{Subject}}","permissions":["example:read"]{{exp}}}""");
        Signature = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(64));
    }

    public static SyntheticJwt ExpiringAt(DateTimeOffset instant) => new(instant.ToUnixTimeSeconds().ToString());

    public static string Encode(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
