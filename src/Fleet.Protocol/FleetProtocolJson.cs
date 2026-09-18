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

    /// <summary>
    /// Copy the supported configuration onto a mutable options instance owned by someone else —
    /// ASP.NET's <c>JsonOptions.SerializerOptions</c>, which is get-only and cannot be replaced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A host that does not do this deserializes request bodies with the framework's web defaults,
    /// which carry <b>no enum converter at all</b>. A body written in this protocol's own wire
    /// vocabulary — <c>"disposition":"ran"</c> — then fails to bind, and the caller gets a framework
    /// 400 with an empty body before the endpoint, or its authorisation, is ever reached. That is
    /// not hypothetical: it is what the south listener did until the first HTTP request was sent at
    /// it.
    /// </para>
    /// <para>
    /// Copied from <see cref="Options"/> member by member rather than restated, so there remains one
    /// definition of the wire shape. A second literal configuration is a second thing to keep in
    /// step, and the drift shows up as a value that serializes correctly and means something else.
    /// </para>
    /// </remarks>
    public static void ApplyTo(JsonSerializerOptions target)
    {
        target.PropertyNamingPolicy = Options.PropertyNamingPolicy;
        target.DefaultIgnoreCondition = Options.DefaultIgnoreCondition;
        target.UnmappedMemberHandling = Options.UnmappedMemberHandling;
        target.PropertyNameCaseInsensitive = Options.PropertyNameCaseInsensitive;

        foreach (var converter in Options.Converters)
            target.Converters.Add(converter);
    }

    /// <summary>Serialize with the supported options.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserialize with the supported options.</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
