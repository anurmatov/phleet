using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Shared;

namespace Fleet.WorkflowOutcomeQualification;

/// <summary>Selected API readback, not fsync, native-process, broker or campaign proof.</summary>
public sealed record ProcessReceiptObservation(Guid BatchId, long Generation, string ItemVersionsHash,
    bool Delivered, bool Acknowledged, string ResponseSha256);

public interface IProcessReceiptReader : IDisposable
{
    string Origin { get; }
    Task<ProcessReceiptObservation> ReadAsync(RuntimeTarget target, CancellationToken ct = default);
}

/// <summary>One bounded GET of the existing coordinator receipt route, never POST/retry/fallback.</summary>
public sealed class ProcessReceiptClient : IProcessReceiptReader
{
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly bool _ownsHttp;
    public string Origin { get; }

    public ProcessReceiptClient(string origin, string token)
        : this(origin, token, new HttpClient(new SocketsHttpHandler {
            AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5)
        }) { Timeout = Timeout.InfiniteTimeSpan }, true) { }

    internal ProcessReceiptClient(string origin, string token, HttpClient http)
        : this(origin, token, http, false) { }

    private ProcessReceiptClient(string origin, string token, HttpClient http, bool ownsHttp)
    {
        if (!Uri.TryCreate(origin,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https")
            || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new QualificationException("receipt origin must be exact");
        if (token.Length is < 1 or > 4096 || token.Any(c => c < '!' || c > '~'))
            throw new QualificationException("receipt token is invalid");
        Origin = uri.GetLeftPart(UriPartial.Authority);
        _token = token;
        _http = http;
        _ownsHttp = ownsHttp;
    }

    public async Task<ProcessReceiptObservation> ReadAsync(RuntimeTarget target, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (new Uri(target.OrchestratorBaseUrl).GetLeftPart(UriPartial.Authority) != Origin
            || target.ScenarioBatchId == Guid.Empty || target.ScenarioItemId == Guid.Empty
            || target.ScenarioVersion <= 0 || target.ScenarioGeneration <= 0
            || string.IsNullOrWhiteSpace(target.ScenarioNonce))
            throw new QualificationException("receipt target does not match the approved origin/scope");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, Origin
            + "/api/workflow-outcomes/batches/" + target.ScenarioBatchId.ToString("D")
            + "/receipt?nonce=" + Uri.EscapeDataString(target.ScenarioNonce));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",_token);
        try
        {
            using var response = await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                throw new QualificationException("receipt GET refused with HTTP " + (int)response.StatusCode);
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                throw new QualificationException("receipt GET did not return JSON");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer,deadline.Token);
                if (read == 0) break;
                if (body.Length + read > 128 * 1024) throw new QualificationException("receipt body exceeds limit");
                body.Write(buffer,0,read);
            }
            var bytes = body.ToArray();
            using var doc = JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth=16 });
            ContainerBindingObservation.RejectDuplicates(doc.RootElement);
            string[] required = ["batchId","generation","nonce","itemVersionsHash","items","deliveredVersions",
                "ackedVersions","receiptHash","deliveredAt","acknowledgedAt"];
            if (doc.RootElement.ValueKind != JsonValueKind.Object || required.Any(k => !doc.RootElement.TryGetProperty(k,out _)))
                throw new QualificationException("receipt is incomplete");
            var receipt = doc.RootElement.Deserialize<WorkflowOutcomeReceipt>(new JsonSerializerOptions(JsonSerializerDefaults.Web) {
                PropertyNameCaseInsensitive=false, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow
            }) ?? throw new QualificationException("receipt is missing");
            var item = new WorkflowOutcomeItemRef(target.ScenarioItemId,target.ScenarioVersion);
            var hash = WorkflowOutcomeHashes.ItemVersionsHash([item]);
            if (receipt.BatchId != target.ScenarioBatchId || receipt.Generation != target.ScenarioGeneration
                || receipt.Nonce != target.ScenarioNonce || receipt.ItemVersionsHash != hash
                || receipt.Items is null || !receipt.Items.SequenceEqual([item])
                || receipt.DeliveredVersions is null || !receipt.DeliveredVersions.SequenceEqual([item])
                || receipt.AckedVersions is null || receipt.AckedVersions.Count > 1
                || receipt.AckedVersions.Any(i => i != item)
                || receipt.DeliveredAt is null || !ContainerBindingObservation.Hex(receipt.ReceiptHash,64)
                || (receipt.AckedVersions.Count == 1) != (receipt.AcknowledgedAt is not null)
                || receipt.AcknowledgedAt < receipt.DeliveredAt)
                throw new QualificationException("receipt membership/state does not match the exact scenario");
            deadline.Token.ThrowIfCancellationRequested();
            return new ProcessReceiptObservation(receipt.BatchId,receipt.Generation,hash,true,
                receipt.AckedVersions.Count == 1,Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException
            or FormatException or ArgumentException)
        {
            // Neither transport text nor malformed bodies may expose the token, nonce or raw response.
            throw new QualificationException("receipt read failed");
        }
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
