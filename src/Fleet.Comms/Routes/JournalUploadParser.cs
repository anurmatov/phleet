using System.Text.Json;
using Fleet.Conversations.Contracts;

namespace Fleet.Comms.Routes;

/// <summary>
/// The one declaration body <c>POST /journal/v1/uploads</c> accepts.
/// </summary>
/// <remarks>
/// Strict in the same shape as <see cref="JournalRecordParser"/>: unknown fields and a wrong JSON
/// type are refusals, not coercions. <b>The only fields are the three the subject must declare</b> —
/// an object key, a bucket, an owner or a state in this body would be a caller telling the server
/// where its bytes live or who they belong to.
/// </remarks>
internal static class JournalUploadParser
{
    private static readonly string[] AllowedFields = ["sha256", "byteSize", "mimeType"];

    public static bool TryParse(
        ReadOnlyMemory<byte> body, out JournalUploadDeclaration? declaration, out string? field)
    {
        declaration = null;
        field = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            field = "body";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                field = "body";
                return false;
            }

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!AllowedFields.Contains(property.Name, StringComparer.Ordinal))
                {
                    field = property.Name;
                    return false;
                }
                fields[property.Name] = property.Value;
            }

            if (fields.Count != AllowedFields.Length)
            {
                field = AllowedFields.First(f => !fields.ContainsKey(f));
                return false;
            }

            if (fields["sha256"].ValueKind != JsonValueKind.String
                || !IsHexSha256(fields["sha256"].GetString()!, out var sha))
            {
                field = "sha256";
                return false;
            }

            if (fields["byteSize"].ValueKind != JsonValueKind.Number
                || !fields["byteSize"].TryGetInt64(out var byteSize) || byteSize < 1)
            {
                field = "byteSize";
                return false;
            }

            if (fields["mimeType"].ValueKind != JsonValueKind.String
                || !JournalRecordParser.IsMimeType(fields["mimeType"].GetString()!))
            {
                field = "mimeType";
                return false;
            }

            declaration = new JournalUploadDeclaration
            {
                Sha256 = sha!,
                ByteSize = byteSize,
                MimeType = fields["mimeType"].GetString()!,
            };

            return true;
        }
    }

    /// <summary>Lowercase hex, 64 characters. Uppercase is refused: digests are stored lowercase.</summary>
    private static bool IsHexSha256(string value, out string? normalized)
    {
        normalized = null;
        if (value.Length != 64) return false;

        foreach (var c in value)
        {
            var hex = c is >= '0' and <= '9'
                || c is >= 'a' and <= 'f';
            if (!hex) return false;
        }

        normalized = value;
        return true;
    }
}
