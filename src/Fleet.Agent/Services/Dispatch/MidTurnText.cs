using Fleet.Agent.Models;

namespace Fleet.Agent.Services.Dispatch;

/// <summary>
/// The fixed text a message is wrapped in when it reaches a turn that is already running: an
/// injected message, a human steering copy, and the reply turn that follows a steered one.
/// </summary>
internal static class MidTurnText
{
    internal static string FormatInjectedMessage(string original) =>
        """
        [NEW MESSAGE — arrived while you were still working on the previous
        instruction. This is not a tool result. Decide whether to finish your
        current step, adjust your plan, or stop and address this first.]

        """ + original;

    /// <summary>Delivery receipt only; does not claim the model read or followed the message.</summary>
    internal const string SteeringDeliveredNotice = "message delivered to my current task, reply pending here.";

    /// <summary>Fixed header of a steering copy (#406). <see cref="FormatInjectedMessage"/> is not applied.</summary>
    internal const string SteeringHeader =
        """
        [HUMAN MESSAGE — a person wrote to you
        while you are doing workflow work. You
        may adjust or stop the current work
        because of it. Do not reply to the
        person here: this turn's final answer
        still goes only to the workflow. After
        this turn ends you get a separate turn
        in that person's chat to reply.]
        """;

    /// <summary>Prefix each steered part gets when its queued entry is drained into the reply turn.</summary>
    internal const string SteeredPartPrefix =
        "[You already saw this message during workflow work. That turn has ended. Reply to the person now.]";

    /// <summary>The steering header, then the already-formatted human prompt. Text only: media waits for the reply turn.</summary>
    internal static string FormatSteeringMessage(string prompt, int attachments) =>
        SteeringHeader + "\n\n" + prompt
        + (attachments > 0 ? $"\n\n({attachments} attachments will be available in the reply turn.)" : "");

    /// <summary>A copy of the entry with each steered part prefixed; the entry itself when none is.</summary>
    internal static QueuedMessage WithSteeredPrefix(QueuedMessage queued)
    {
        var parts = queued.Parts;
        if (!parts.Any(p => p.Steered)) return queued;
        static QueuedMessagePart Prefixed(QueuedMessagePart p) =>
            p.Steered
                ? p with
                {
                    Task = SteeredPartPrefix + "\n\n" + p.Task,
                    ColdStartTask = p.ColdStartTask is null ? null : SteeredPartPrefix + "\n\n" + p.ColdStartTask,
                }
                : p;
        var rebuilt = new QueuedMessage(queued.ChatId, Prefixed(parts[0]));
        foreach (var part in parts.Skip(1))
            if (!rebuilt.TryAppendPart(Prefixed(part))) return queued;
        return rebuilt;
    }
}
