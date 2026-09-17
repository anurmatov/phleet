using Fleet.Protocol;

namespace Fleet.Comms.Contracts;

// The exact wire shapes of docs/first-party-api.md §5.1. Every request and response body carries
// `protocol`, and every one of these is serialized through FleetProtocolJson.Options — camelCase,
// nulls omitted, enums as snake_case strings — so the wire form is the protocol's, not this
// project's. NorthSerializationTests pins each body byte-for-byte.

/// <summary>`POST /v1/auth/devices` request.</summary>
public sealed record RegisterDeviceRequest
{
    public string? Protocol { get; init; }
    public string? EnrollmentCode { get; init; }
}

/// <summary>
/// `POST /v1/auth/devices` response. The only time a device secret is ever transmitted; the server
/// keeps a salted Argon2id hash and cannot reproduce this value.
/// </summary>
public sealed record RegisterDeviceResponse
{
    public string Protocol { get; init; } = ProtocolVersion.Current;
    public required string DeviceId { get; init; }
    public required string DeviceSecret { get; init; }
}

/// <summary>`POST /v1/auth/token` request. There is no refresh token; this is the refresh path.</summary>
public sealed record TokenRequest
{
    public string? Protocol { get; init; }
    public string? DeviceId { get; init; }
    public string? DeviceSecret { get; init; }
}

/// <summary>`POST /v1/auth/token` response.</summary>
public sealed record TokenResponse
{
    public string Protocol { get; init; } = ProtocolVersion.Current;
    public required string AccessToken { get; init; }
    public required int ExpiresInSeconds { get; init; }
}

/// <summary>`POST /v1/auth/devices/{deviceId}:revoke` response.</summary>
public sealed record RevokeDeviceResponse
{
    public string Protocol { get; init; } = ProtocolVersion.Current;
    public required bool Revoked { get; init; }
}

/// <summary>The server limits a client needs before it can size anything (§5.1).</summary>
public sealed record SessionLimits
{
    public required int InboundTextBytes { get; init; }
    public required int CatchUpLimitDefault { get; init; }
    public required int CatchUpLimitMax { get; init; }
    public required int IdentifierMaxLength { get; init; }
    public required int OutboundBufferEvents { get; init; }
}

/// <summary>
/// `GET /v1/session` response. `principalId` is derived from the authenticated device record and is
/// never accepted from input (§1, §4).
/// </summary>
public sealed record SessionResponse
{
    public string Protocol { get; init; } = ProtocolVersion.Current;
    public required string PrincipalId { get; init; }
    public required string AgentLabel { get; init; }
    public required SessionLimits Limits { get; init; }
}

/// <summary>
/// Every non-2xx body on this boundary.
///
/// <para><see cref="Message"/> is always the fixed constant from <see cref="ProtocolErrors"/>, so
/// two failures with the same code are byte-identical — which is what makes the five auth failures
/// indistinguishable to a caller (§3.6) — and so no runtime or exception text ever reaches a
/// client (§13).</para>
/// </summary>
public sealed record ErrorResponse
{
    public string Protocol { get; init; } = ProtocolVersion.Current;
    public required ProtocolErrorCode Code { get; init; }
    public required string Message { get; init; }

    public static ErrorResponse For(ProtocolErrorCode code) =>
        new() { Code = code, Message = ProtocolErrors.MessageFor(code) };
}
