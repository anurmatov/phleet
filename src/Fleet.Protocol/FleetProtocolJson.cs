using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fleet.Protocol;

/// <summary>
/// The only supported serializer configuration for the conversation protocol (D1).
/// Hand-rolled <see cref="JsonSerializerOptions"/> elsewhere is a review rejection: camelCase,
/// null-omission and the lowercase enum policy together define the wire shape that
/// <c>ProtocolGoldenJsonTests</c> pins.
/// </summary>
public static class FleetProtocolJson
{
    /// <summary>
    /// camelCase properties, nulls omitted, enums as lowercase strings.
    ///
    /// Enum members are written lowercase with word boundaries as underscores, so
    /// <c>SubmissionDisposition.QueueFull</c> is <c>"queue_full"</c> and
    /// <c>OutcomeUnknownReason.TurnReaped</c> is <c>"turn_reaped"</c> — matching the design's
    /// wire vocabulary exactly.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Receivers must tolerate unknown payload fields rather than throwing (D4).
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        // populateMissingResolver: true installs the default reflection resolver. Freezing the
        // instance is what makes "the only supported configuration" enforceable rather than
        // advisory — a caller cannot mutate the shared options out from under everyone else.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Serialize with the supported options.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserialize with the supported options.</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
