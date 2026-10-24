using System.Diagnostics;
using System.Net;
using System.Text;

namespace Fleet.Comms.Tests;

// Test-only ownership of a redirected child: never retain arbitrary application values, paths,
// URLs or exception messages. Keep bounded diagnostic categories and total output counts instead.
internal static class ReadinessProcess
{
    public static async Task WaitAsync(Process child, Uri ready, Uri north,
        Func<CancellationToken, Task<HttpStatusCode>> probe, TimeSpan? wait = null)
    {
        var limit = wait ?? TimeSpan.FromSeconds(15);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromSeconds(15) ||
            !ready.IsLoopback || !north.IsLoopback || ready.Query.Length != 0 || north.Query.Length != 0)
            throw new ArgumentException("Only bounded local fixture readiness is supported.");

        using var deadline = new CancellationTokenSource(limit);
        using var drains = new CancellationTokenSource();
        var stdout = new SafeOutput();
        var stderr = new SafeOutput();
        var outputTask = DrainAsync(child.StandardOutput, stdout, drains.Token);
        var errorTask = DrainAsync(child.StandardError, stderr, drains.Token);
        var exited = child.WaitForExitAsync();
        var failure = "deadline";
        var lastRequest = "none";
        var succeeded = false;
        var cleanup = "complete";
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                if (child.HasExited)
                {
                    failure = "child exited before readiness";
                    break;
                }
                try
                {
                    var request = probe(deadline.Token);
                    if (await Task.WhenAny(request, exited) == exited)
                    {
                        // Cancel and observe the in-flight request before releasing its client.
                        deadline.Cancel();
                        try { await request.WaitAsync(TimeSpan.FromSeconds(2)); }
                        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException) { }
                        failure = "child exited before readiness";
                        break;
                    }
                    var status = await request.WaitAsync(deadline.Token);
                    succeeded = status == HttpStatusCode.OK;
                    failure = $"HTTP {(int)status}";
                    break; // A response other than 200 is a failure, never retried into success.
                }
                catch (HttpRequestException ex)
                {
                    lastRequest = ex.HttpRequestError.ToString(); // Enum only, not Message/URI/token.
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { lastRequest = "request-timeout"; }

                await Task.WhenAny(Task.Delay(250, deadline.Token), exited);
            }
        }
        finally
        {
            // Cleanup is bounded independently of the unchanged readiness ceiling. Descendants
            // holding inherited pipe handles must not turn diagnostics into another indefinite wait.
            try
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
                await exited.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                cleanup = ex.GetType().Name;
            }
            try
            {
                await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                cleanup = "drain timeout";
                drains.Cancel();
                try { await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { cleanup = "drain cancellation timeout"; }
            }
        }

        if (!succeeded || cleanup != "complete")
            throw new Xunit.Sdk.XunitException(
                $"Readiness failed: {failure}; lastRequest={lastRequest}; " +
                $"exit={(child.HasExited ? child.ExitCode.ToString() : "unconfirmed")}; cleanup={cleanup}; " +
                $"ready={ready.GetLeftPart(UriPartial.Path)}; north={north.GetLeftPart(UriPartial.Path)}; " +
                $"stdout={stdout.Snapshot()}; stderr={stderr.Snapshot()}");
    }

    private static async Task DrainAsync(StreamReader reader, SafeOutput output, CancellationToken ct)
    {
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
                output.Append(buffer.AsSpan(0, count));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private sealed class SafeOutput
    {
        private readonly StringBuilder _categories = new();
        private string _overlap = "";
        private long _characters;
        private static readonly string[] Categories =
        [
            "fixture-startup-failure", "AddressInUseException", "SocketException",
            "InvalidOperationException", "AuthStorePath", "UnauthorizedAccessException",
            "IOException", "fail:", "warn:",
        ];

        public void Append(ReadOnlySpan<char> chunk)
        {
            _characters += chunk.Length;
            var text = _overlap + chunk.ToString();
            foreach (var category in Categories)
            {
                if (text.Contains(category, StringComparison.Ordinal) &&
                    _categories.Length + category.Length + 1 <= 2048)
                    _categories.Append(category).Append(' ');
            }
            _overlap = text[^Math.Min(text.Length, 32)..];
        }

        public string Snapshot() => $"characters={_characters}, categories=[{_categories}]";
    }
}
