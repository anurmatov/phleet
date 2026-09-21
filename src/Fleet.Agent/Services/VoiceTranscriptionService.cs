using Fleet.Agent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Sends audio bytes to the fleet-whisper transcription service and returns the transcribed text.
/// If the service URL is not configured or the request fails, returns null.
/// </summary>
public sealed class VoiceTranscriptionService(
    IHttpClientFactory httpClientFactory,
    IOptions<WhisperOptions> options,
    ILogger<VoiceTranscriptionService> logger)
{
    private readonly WhisperOptions _opts = options.Value;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_opts.ServiceUrl);

    /// <summary>
    /// Transcribes the given audio or video bytes (OGG, MP4, or any ffmpeg-supported format).
    /// Returns the transcribed text, or null on failure.
    /// </summary>
    /// <param name="contentType">
    /// Content type of the uploaded part. The service decodes by content rather than by
    /// suffix, so this does not change what it does — it just stops the payload being
    /// labelled as something it is not.
    /// </param>
    public async Task<string?> TranscribeAsync(
        byte[] audioBytes,
        string fileName = "voice.ogg",
        string contentType = "audio/ogg",
        CancellationToken ct = default)
    {
        if (!IsEnabled)
            return null;

        try
        {
            var client = httpClientFactory.CreateClient("whisper");
            using var content = new MultipartFormDataContent();
            using var audioContent = new ByteArrayContent(audioBytes);
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            content.Add(audioContent, "audio", fileName);

            var response = await client.PostAsync($"{_opts.ServiceUrl.TrimEnd('/')}/transcribe", content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("text").GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transcription failed for {FileName} — no transcript", fileName);
            return null;
        }
    }
}
