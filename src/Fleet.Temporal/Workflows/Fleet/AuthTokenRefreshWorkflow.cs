using Fleet.Temporal.Activities;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Logging;
using Temporalio.Workflows;

namespace Fleet.Temporal.Workflows.Fleet;

/// <summary>
/// Scheduled workflow that centrally manages OAuth token refresh for all configured providers.
///
/// Runs every 30 minutes. Iterates over each provider (default: ["claude"]), checks token
/// expiry and refreshes directly via the OAuth endpoint — no agent delegation needed.
/// On successful refresh, broadcasts the new tokens to all running agents and to the orchestrator.
///
/// If one provider fails, the others still run. Per-provider failures are caught and logged.
/// </summary>
[Workflow]
public class AuthTokenRefreshWorkflow
{
    private static readonly ActivityOptions RefreshOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new() { MaximumAttempts = 3 },
    };

    private static readonly ActivityOptions BroadcastOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        HeartbeatTimeout = TimeSpan.FromSeconds(60),
        RetryPolicy = new() { MaximumAttempts = 3 },
    };

    [WorkflowRun]
    public async Task<string> RunAsync(AuthTokenRefreshWorkflowInput? input = null)
    {
        var workflowId = Workflow.Info.WorkflowId;

        Workflow.Logger.LogInformation(
            "AuthTokenRefreshWorkflow starting (workflowId={WorkflowId})", workflowId);

        var providers = input?.Providers is { Length: > 0 }
            ? input.Providers
            : ["claude"];
        var forceRefresh = input?.ForceRefresh ?? false;

        var results = new List<string>();

        foreach (var provider in providers)
        {
            var result = await RefreshProviderAsync(provider, forceRefresh);
            results.Add($"{provider}:{result}");
        }

        return string.Join(", ", results);
    }

    private async Task<string> RefreshProviderAsync(string provider, bool forceRefresh)
    {
        AgentTokenResult tokenResult;
        try
        {
            tokenResult = await Workflow.ExecuteActivityAsync(
                (RefreshAuthTokenActivity a) => a.CheckAndRefreshAsync(provider, forceRefresh),
                RefreshOptions);
        }
        catch (Exception ex)
        {
            Workflow.Logger.LogError(ex, "{Provider} token check/refresh failed", provider);
            return $"failed: {ex.Message}";
        }

        if (!tokenResult.Refreshed)
        {
            Workflow.Logger.LogInformation("{Provider} token does not need refresh", provider);
            return "no-op: token still valid";
        }

        Workflow.Logger.LogInformation("{Provider} token was refreshed, broadcasting", provider);

        if (tokenResult.AccessToken is null || tokenResult.RefreshToken is null || tokenResult.ExpiresAt is null)
        {
            Workflow.Logger.LogError(
                "{Provider} token marked as refreshed but token fields are missing", provider);
            return "failed: token fields missing in refresh response";
        }

        try
        {
            await Workflow.ExecuteActivityAsync(
                (BroadcastTokenUpdateActivity a) => a.BroadcastAsync(
                    provider,
                    tokenResult.AccessToken,
                    tokenResult.RefreshToken,
                    tokenResult.ExpiresAt.Value,
                    tokenResult.Scopes,
                    tokenResult.SubscriptionType,
                    tokenResult.RateLimitTier,
                    tokenResult.IdToken,
                    tokenResult.AccountId),
                BroadcastOptions);
        }
        catch (Exception ex)
        {
            Workflow.Logger.LogError(ex, "{Provider} token broadcast failed", provider);
            return $"refreshed but broadcast failed: {ex.Message}";
        }

        return "refreshed and broadcast";
    }
}
