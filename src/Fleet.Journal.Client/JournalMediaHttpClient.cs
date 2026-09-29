using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Fleet.Journal.Client;

/// <summary>What one media call produced.</summary>
/// <param name="Status">The HTTP status, or 0 when no response arrived.</param>
/// <param name="Error">The body's <c>error</c> code, when there is one.</param>
/// <param name="UploadId">The declared upload id, for a successful declaration.</param>
public sealed record JournalMediaResponse(int Status, string? Error, string? UploadId)
{
    public bool IsTransportFailure => Status == 0;
}

/// <summary>
/// The two upload calls, on the same client and the same token as the message post.
/// </summary>
/// <remarks>
/// ⚠️ The token travels only in the <c>Authorization</c> header, and neither the token nor an
/// upload id appears in an exception this class produces. An upload id is not a secret, but it is
/// an internal identifier and a log line is where it would start leaking.
/// </remarks>
public sealed class JournalMediaHttpClient(HttpClient http, string token)
{
    public const string UploadsPath = "/journal/v1/uploads";

    /// <summary>A declaration is a few hundred bytes; a PUT is up to the object cap.</summary>
    public static readonly TimeSpan DeclareTimeout = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan PutTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http = http;
    private readonly string _token = token.Trim();

    public async Task<JournalMediaResponse> DeclareAsync(
        string sha256, long byteSize, string mimeType, CancellationToken ct)
    {
        var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("sha256", sha256);
            writer.WriteNumber("byteSize", byteSize);
            writer.WriteString("mimeType", mimeType);
            writer.WriteEndObject();
        }

        body.Position = 0;
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await SendAsync(HttpMethod.Post, UploadsPath, content, null, DeclareTimeout, ct);
    }

    public async Task<JournalMediaResponse> PutAsync(
        string uploadId, byte[] payload, string mimeType, CancellationToken ct)
    {
        using var content = new ReadOnlyMemoryContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

        return await SendAsync(
            HttpMethod.Put, $"{UploadsPath}/{uploadId}", content, null, PutTimeout, ct);
    }

    private async Task<JournalMediaResponse> SendAsync(
        HttpMethod method, string path, HttpContent content, string? accept,
        TimeSpan timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new JournalMediaResponse(0, "timeout", null);
        }
        catch (HttpRequestException)
        {
            return new JournalMediaResponse(0, "connection", null);
        }

        using (response)
        {
            string? error = null, uploadId = null;

            try
            {
                var text = await response.Content.ReadAsStringAsync(budget.Token);
                if (text.Length > 0 && text.Length < 4096)
                {
                    using var document = JsonDocument.Parse(text);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        error = Str(document.RootElement, "error");
                        uploadId = Str(document.RootElement, "uploadId");
                    }
                }
            }
            catch (Exception)
            {
                // A body that is not the listener's JSON: the status alone decides.
            }

            return new JournalMediaResponse((int)response.StatusCode, error, uploadId);
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
