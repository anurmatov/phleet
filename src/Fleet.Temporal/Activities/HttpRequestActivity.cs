using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Temporalio.Activities;

namespace Fleet.Temporal.Activities;

/// <summary>
/// Generic HTTP request activity for the Universal Workflow Engine.
/// Replaces project-specific mcp_call activities by allowing workflows to call any HTTP endpoint directly.
/// Registered on all configured namespace workers.
/// </summary>
public sealed class HttpRequestActivity
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpRequestActivity> _logger;

    public HttpRequestActivity(IHttpClientFactory httpClientFactory, ILogger<HttpRequestActivity> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Response body capped at 512 KB to stay within Temporal's 2 MB event-history limit.</summary>
    private const int MaxResponseBytes = 512 * 1024;

    /// <summary>Max chars included in error messages to avoid leaking large bodies into workflow history.</summary>
    private const int MaxErrorBodyPreview = 500;

    /// <summary>
    /// Executes an HTTP request and returns the response body as a string (capped at 512 KB).
    /// Throws <see cref="HttpRequestException"/> if the response status code is not in <see cref="HttpRequestInput.ExpectedStatusCodes"/>.
    /// </summary>
    [Activity]
    public async Task<string> HttpRequestAsync(HttpRequestInput request)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        _logger.LogInformation("HttpRequest: {Method} {Url}", request.Method, request.Url);

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(request.TimeoutSeconds);

        using var httpRequest = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);

        // Apply headers
        if (request.Headers != null)
        {
            foreach (var (name, value) in request.Headers)
                httpRequest.Headers.TryAddWithoutValidation(name, value);
        }

        // Apply body
        if (request.Body != null)
        {
            var bodyStr = request.Body is string s ? s : JsonSerializer.Serialize(request.Body);
            httpRequest.Content = new StringContent(bodyStr, Encoding.UTF8, "application/json");

            // Override Content-Type if explicitly set in headers
            if (request.Headers != null
                && request.Headers.TryGetValue("Content-Type", out var ct2)
                && !string.IsNullOrEmpty(ct2))
            {
                httpRequest.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ct2);
            }
        }

        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        // Cap response body to MaxResponseBytes to stay within Temporal's event-history size limit
        var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxResponseBytes + 1];
        var totalRead = 0;
        int bytesRead;
        while (totalRead < buffer.Length &&
               (bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead), ct)) > 0)
        {
            totalRead += bytesRead;
        }

        var responseBody = Encoding.UTF8.GetString(buffer, 0, Math.Min(totalRead, MaxResponseBytes));
        if (totalRead > MaxResponseBytes)
            responseBody += $"\n[response truncated at {MaxResponseBytes / 1024} KB]";

        var expectedCodes = request.ExpectedStatusCodes?.Length > 0
            ? request.ExpectedStatusCodes
            : [200];

        if (!expectedCodes.Contains((int)response.StatusCode))
        {
            var preview = responseBody.Length > MaxErrorBodyPreview
                ? responseBody[..MaxErrorBodyPreview] + "…"
                : responseBody;
            throw new HttpRequestException(
                $"HTTP {request.Method} {request.Url} returned {(int)response.StatusCode} {response.ReasonPhrase}. Body: {preview}");
        }

        _logger.LogInformation("HttpRequest: {Method} {Url} → {StatusCode}", request.Method, request.Url, (int)response.StatusCode);
        return responseBody;
    }
}

/// <summary>Input for <see cref="HttpRequestActivity.HttpRequestAsync"/>.</summary>
public sealed record HttpRequestInput(
    string Url,
    string Method,
    Dictionary<string, string>? Headers,
    string? Body,
    int TimeoutSeconds,
    int[] ExpectedStatusCodes);
