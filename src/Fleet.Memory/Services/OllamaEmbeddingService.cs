using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Memory.Configuration;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Services;

public sealed class OllamaEmbeddingService(HttpClient httpClient, IOptions<EmbeddingOptions> options, ILogger<OllamaEmbeddingService> logger) : IEmbeddingService
{
    private readonly string _model = options.Value.Ollama.Model;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request = new EmbedRequest { Model = _model, Input = text };

        var response = await httpClient.PostAsJsonAsync("/api/embed", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct)
            ?? throw new InvalidOperationException("Ollama returned null response");

        if (result.Embeddings is not { Count: > 0 })
            throw new InvalidOperationException("Ollama returned empty embeddings");

        logger.LogDebug("[Ollama] Generated embedding with {Dimensions} dimensions", result.Embeddings[0].Length);
        return result.Embeddings[0];
    }

    private sealed class EmbedRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; set; }

        [JsonPropertyName("input")]
        public required string Input { get; set; }
    }

    private sealed class EmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]>? Embeddings { get; set; }
    }
}
