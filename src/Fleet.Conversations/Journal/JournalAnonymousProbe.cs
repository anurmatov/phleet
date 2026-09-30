using System.Net;

namespace Fleet.Conversations.Journal;

public static class JournalAnonymousProbe
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<bool> CheckAsync(
        string endpoint, string bucket, HttpClient? http = null, CancellationToken ct = default)
    {
        try
        {
            using var response = await (http ?? Client).GetAsync(
                $"{endpoint.TrimEnd('/')}/{Uri.EscapeDataString(bucket)}?list-type=2",
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.Forbidden)
                throw new JournalAnonymousAccessException();
            return true;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}

public sealed class JournalAnonymousAccessException() : Exception("bucket_public");
