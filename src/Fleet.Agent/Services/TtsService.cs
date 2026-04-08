using Fleet.Agent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;

namespace Fleet.Agent.Services;

/// <summary>
/// Sends text to the fleet-kokoro-tts service and returns OGG audio bytes.
/// If the service URL is not configured or the request fails, returns null.
/// </summary>
public sealed class TtsService(
    IHttpClientFactory httpClientFactory,
    IOptions<TtsOptions> options,
    ILogger<TtsService> logger)
{
    private const int MaxTextLength = 2000;
    private readonly TtsOptions _opts = options.Value;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_opts.ServiceUrl);

    /// <summary>
    /// Synthesizes speech from the given text. Returns OGG audio bytes, or null on failure.
    /// </summary>
    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return null;

        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (text.Length > MaxTextLength)
            text = text[..MaxTextLength];

        try
        {
            var client = httpClientFactory.CreateClient("tts");
            var url = $"{_opts.ServiceUrl.TrimEnd('/')}/v1/audio/speech";
            var response = await client.PostAsJsonAsync(url, new
            {
                model = "kokoro",
                input = text,
                voice = _opts.Voice,
                response_format = "opus"
            }, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TTS synthesis failed — skipping voice response");
            return null;
        }
    }
}
