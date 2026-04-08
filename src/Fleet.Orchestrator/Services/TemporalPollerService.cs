using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Models;
using Microsoft.Extensions.Options;
using Temporalio.Common;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Background service that polls Temporal for running workflows across all configured namespaces
/// and stores the results in WorkflowStore, broadcasting updates via AgentRegistry WebSocket.
/// </summary>
public sealed class TemporalPollerService(
    IOptions<TemporalOptions> opts,
    WorkflowStore store,
    AgentRegistry registry,
    TemporalClientRegistry clientRegistry,
    ILogger<TemporalPollerService> logger) : BackgroundService
{
    private readonly TemporalOptions _opts = opts.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!clientRegistry.IsConfigured)
        {
            logger.LogInformation("Temporal address not configured — workflow poller disabled");
            return;
        }

        logger.LogInformation("TemporalPollerService starting, address={Address}, namespaces={Ns}",
            _opts.Address, string.Join(',', _opts.Namespaces));

        var clients = await clientRegistry.GetClientsAsync(ct);

        if (clients.Count == 0)
        {
            logger.LogWarning("No Temporal namespaces reachable — workflow poller stopping");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var workflows = new List<WorkflowSummary>();

                foreach (var (ns, client) in clients)
                {
                    try
                    {
                        await foreach (var wf in client.ListWorkflowsAsync("ExecutionStatus=\"Running\"").WithCancellation(ct))
                        {
                            int? issueNum = null;
                            int? prNum = null;
                            string? repo = null;
                            string? docPrs = null;
                            string? phase = null;
                            try
                            {
                                var attrs = wf.TypedSearchAttributes;
                                if (attrs.TryGetValue(SearchAttributeKey.CreateLong("IssueNumber"), out var iv))
                                    issueNum = (int)iv;
                                if (attrs.TryGetValue(SearchAttributeKey.CreateLong("PrNumber"), out var pv))
                                    prNum = (int)pv;
                                if (attrs.TryGetValue(SearchAttributeKey.CreateKeyword("Repo"), out var rv))
                                    repo = rv;
                                if (attrs.TryGetValue(SearchAttributeKey.CreateKeyword("DocPrs"), out var dv))
                                    docPrs = dv;
                                if (attrs.TryGetValue(SearchAttributeKey.CreateKeyword("Phase"), out var phv))
                                    phase = phv;
                            }
                            catch
                            {
                                // Search attributes unavailable — non-fatal
                            }

                            workflows.Add(new WorkflowSummary(
                                WorkflowId:   wf.Id,
                                RunId:        wf.RunId ?? "",
                                WorkflowType: wf.WorkflowType ?? "",
                                Namespace:    ns,
                                TaskQueue:    wf.TaskQueue,
                                Status:       "Running",
                                StartTime:    wf.StartTime,
                                IssueNumber:  issueNum,
                                PrNumber:     prNum,
                                Repo:         repo,
                                DocPrs:       docPrs,
                                Phase:        phase));
                        }
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Error querying Temporal namespace '{Namespace}'", ns);
                    }
                }

                var changed = store.Update(workflows);
                if (changed)
                {
                    logger.LogDebug("Workflow snapshot updated: {Count} running workflows", workflows.Count);
                    registry.BroadcastWorkflows(workflows);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Unexpected error in TemporalPollerService");
            }

            await Task.Delay(TimeSpan.FromSeconds(_opts.PollIntervalSeconds), ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
