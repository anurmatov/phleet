using System.Text.RegularExpressions;
using Fleet.Agent.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>
/// Publishes relay and bridge answers when a turn completes (#277 D-2).
///
/// This code used to live on the Telegram transport, attached in its constructor. That made the
/// thing workflow delegations depend on — the answer ever getting back to the caller — a private
/// method of the one class a Telegram-free deployment wants to omit. Making the transport's
/// registration conditional would have silently deleted every workflow answer, or, with the #275
/// startup guard in place, turned the process into a crash loop.
///
/// The move is verbatim: the bridge correlation branch and the relay response branch are the same
/// code, and <see cref="FormatTaskResponse"/> came with them because nothing else calls it.
///
/// Subscription happens in the CONSTRUCTOR, not in <see cref="StartAsync"/>, for the same reason
/// the transport did it that way: the host materialises every hosted service before calling any
/// <c>StartAsync</c>, so there is no instant at which the relay consumer is running and this
/// handler is not. <see cref="RuntimeWiringService"/> now verifies that rather than assuming it.
///
/// Registered as <c>AddSingleton&lt;RelayCompletionPublisher&gt;()</c> PLUS
/// <c>AddHostedService(sp =&gt; sp.GetRequiredService&lt;RelayCompletionPublisher&gt;())</c>. That
/// pair is load-bearing, not style — see the comment at the registration site (#277 MUST NOT 16).
/// </summary>
public sealed class RelayCompletionPublisher : IHostedService, IDisposable
{
    private static readonly Regex TaskFailedMarkerRegex =
        new(@"^\[TASK_FAILED:\s*([^\]]+)\]\s*", RegexOptions.Compiled);

    private readonly TaskManager _taskManager;
    private readonly GroupRelayService _relay;
    private readonly RelayCompletionCounter _counter;
    private readonly ILogger<RelayCompletionPublisher> _logger;

    public RelayCompletionPublisher(
        TaskManager taskManager,
        GroupRelayService relay,
        ILogger<RelayCompletionPublisher> logger,
        RelayCompletionCounter? counter = null)
    {
        _taskManager = taskManager;
        _relay = relay;
        _logger = logger;
        _counter = counter ?? new RelayCompletionCounter();

        _taskManager.OnTaskCompleted += OnTaskCompleted;
        IsAttached = true;
    }

    /// <summary>
    /// True once this instance has subscribed to <see cref="TaskManager.OnTaskCompleted"/>.
    ///
    /// Read by <see cref="RuntimeWiringService"/>'s startup guard. The guard identifies this
    /// publisher BY TYPE rather than asking whether the event has any subscriber at all: after the
    /// split, <see cref="CompletionContextBuffer"/> also subscribes, so a boolean "someone is
    /// listening" would be satisfied while every workflow answer vanished (#277 MUST NOT 21).
    /// </summary>
    public bool IsAttached { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Detach();
        return Task.CompletedTask;
    }

    public void Dispose() => Detach();

    private void Detach()
    {
        if (!IsAttached) return;
        _taskManager.OnTaskCompleted -= OnTaskCompleted;
        IsAttached = false;
    }

    private void OnTaskCompleted(
        long chatId, string result, string? relaySender, TaskSource source, bool isPartial,
        string? correlationId, string? taskId, CompletionKind kind)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (relaySender == "bridge" && correlationId is not null)
                {
                    var bridgeResult = kind switch
                    {
                        CompletionKind.Idle => "[status: idle]",
                        CompletionKind.Failed => $"[status: failed]\n{result}",
                        CompletionKind.Incomplete => $"[status: incomplete]\n{result}",
                        _ => result,
                    };
                    await _relay.PublishToAgentAsync("bridge", chatId, bridgeResult,
                        type: RelayMessageType.BridgeResponse, correlationId: correlationId, taskId: taskId);
                    _counter.Published(nameof(RelayMessageType.BridgeResponse));
                }
                else if (relaySender is not null)
                {
                    var type = isPartial ? RelayMessageType.PartialResponse : RelayMessageType.Response;
                    var text = taskId is not null ? FormatTaskResponse(result, isPartial, kind) : result;
                    await _relay.PublishToAgentAsync(relaySender, chatId, text, type: type, taskId: taskId);
                    _counter.Published(type.ToString());
                }
            }
            catch (Exception ex)
            {
                // Never payload text: this reaches logs, and a relay answer can carry anything the
                // agent wrote. Publication failure must not fault the turn either — it already
                // completed.
                _logger.LogError(ex,
                    "Failed to publish relay completion (sender={Sender}, hasCorrelationId={HasCorrelation}, hasTaskId={HasTaskId})",
                    relaySender, correlationId is not null, taskId is not null);
            }
        });
    }

    private static string FormatTaskResponse(string result, bool isPartial, CompletionKind kind)
    {
        if (kind == CompletionKind.Idle)
            return "[status: idle]";

        if (kind == CompletionKind.Failed)
            return $"[status: failed]\n{result}";

        // Detect voluntary failure marker: [TASK_FAILED: reason]
        // Agents can emit this to signal that they refused or cannot complete a delegated task.
        var taskFailedMatch = TaskFailedMarkerRegex.Match(result);
        if (taskFailedMatch.Success)
        {
            var reason = taskFailedMatch.Groups[1].Value.Trim();
            var body = result[taskFailedMatch.Length..].TrimStart('\n', '\r');
            var text = string.IsNullOrEmpty(body) ? $"Task failed: {reason}" : $"Task failed: {reason}\n{body}";
            return $"[status: failed]\n{text}";
        }

        // Determine status from result content and isPartial flag
        var status = isPartial
            ? (result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
               || result.StartsWith("Task failed:", StringComparison.OrdinalIgnoreCase)
               ? "failed"
               : "incomplete")
            : "completed";

        return $"[status: {status}]\n{result}";
    }
}
