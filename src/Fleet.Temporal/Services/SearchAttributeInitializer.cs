using Fleet.Temporal.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.OperatorService.V1;
using Temporalio.Client;

namespace Fleet.Temporal.Services;

/// <summary>
/// Hosted service that registers required Temporal search attributes on bridge startup.
/// Runs before workers begin polling, ensuring attributes are available when workflows
/// attempt to call <c>Workflow.UpsertTypedSearchAttributes</c>.
///
/// All attribute definitions live here so adding a new one is a single-line change.
/// Each namespace registered in <see cref="TemporalBridgeOptions.Namespaces"/> gets
/// the full set of attributes. If an attribute already exists the call is a no-op.
/// </summary>
public sealed class SearchAttributeInitializer(
    IOptions<TemporalBridgeOptions> options,
    ILogger<SearchAttributeInitializer> logger) : IHostedService
{
    /// <summary>
    /// Search attributes required by Fleet workflows.
    /// Add new attributes here — they will be registered across all configured namespaces.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IndexedValueType> RequiredAttributes =
        new Dictionary<string, IndexedValueType>
        {
            ["IssueNumber"] = IndexedValueType.Int,
            ["PrNumber"]    = IndexedValueType.Int,
            ["PositionId"]  = IndexedValueType.Int,
            ["Repo"]        = IndexedValueType.Keyword,
            ["DocPrs"]      = IndexedValueType.Keyword,
            ["Phase"]       = IndexedValueType.Keyword,
            ["ReviewDate"]  = IndexedValueType.Keyword,
        };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = options.Value;
        if (opts.Namespaces is not { Count: > 0 })
        {
            logger.LogWarning("No namespaces configured — skipping search attribute registration");
            return;
        }

        ITemporalClient client;
        try
        {
            client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(opts.TemporalAddress)
            {
                Namespace = "default"
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not connect to Temporal for search attribute registration — skipping");
            return;
        }

        foreach (var ns in opts.Namespaces)
        {
            await RegisterAttributesForNamespaceAsync(client, ns, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RegisterAttributesForNamespaceAsync(
        ITemporalClient client,
        string ns,
        CancellationToken cancellationToken)
    {
        // List existing custom attributes to determine which ones need registering.
        IReadOnlyDictionary<string, IndexedValueType> existing;
        try
        {
            var listResp = await client.Connection.OperatorService.ListSearchAttributesAsync(
                new ListSearchAttributesRequest { Namespace = ns });
            existing = listResp.CustomAttributes;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list search attributes for namespace {Namespace} — skipping registration", ns);
            return;
        }

        var toRegister = RequiredAttributes
            .Where(kv => !existing.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (toRegister.Count == 0)
        {
            logger.LogDebug("All required search attributes already registered in namespace {Namespace}", ns);
            return;
        }

        logger.LogInformation(
            "Registering {Count} search attribute(s) in namespace {Namespace}: {Attributes}",
            toRegister.Count,
            ns,
            string.Join(", ", toRegister.Keys));

        try
        {
            var req = new AddSearchAttributesRequest { Namespace = ns };
            foreach (var (name, type) in toRegister)
                req.SearchAttributes[name] = type;

            await client.Connection.OperatorService.AddSearchAttributesAsync(req);

            logger.LogInformation(
                "Registered search attributes in namespace {Namespace}: {Attributes}",
                ns,
                string.Join(", ", toRegister.Keys));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to register search attributes in namespace {Namespace} — workflows may fail with BadSearchAttributes",
                ns);
        }
    }
}
