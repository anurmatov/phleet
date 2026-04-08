using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fleet.Temporal.Models;

/// <summary>
/// JsonConverter that accepts either a JSON string array, a comma-separated
/// string, or null — normalizing everything to <c>string[]?</c>.
///
/// Exists because UWE's auto-derived workflow input schemas declare every
/// field as "type": "string", so agents fanning out into a child workflow
/// with an array field (e.g. ConsensusReviewInput.ReviewerAgents) may pass
/// it as "linus,developer" instead of ["linus","developer"]. Without this
/// converter, System.Text.Json throws when binding the child workflow input
/// and the fan-out silently never happens.
/// </summary>
public sealed class FlexibleStringArrayConverter : JsonConverter<string[]?>
{
    public override string[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.StartArray:
                return JsonSerializer.Deserialize<string[]>(ref reader, options);

            case JsonTokenType.String:
                var raw = reader.GetString();
                if (string.IsNullOrWhiteSpace(raw)) return null;
                return raw
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            default:
                throw new JsonException(
                    $"FlexibleStringArrayConverter: expected array, string, or null, got {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, string[]? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        JsonSerializer.Serialize(writer, value, options);
    }
}
