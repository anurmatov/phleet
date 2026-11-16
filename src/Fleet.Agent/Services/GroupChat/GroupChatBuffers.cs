using System.Collections.Concurrent;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services.GroupChat;

/// <summary>
/// The per-chat message buffers, in memory only. They exist for unread tracking in group "all"
/// mode and check-ins, and for the recent-history block of a provider that keeps no conversation;
/// they begin empty in every new container.
/// </summary>
/// <remarks>
/// They used to be kept in <c>.fleet/chat-history.json</c> under the agent's work directory, read
/// at the first use and rewritten on every message, answer and tool call. A provider session now
/// carries the conversation across a restart, so no agent reads or writes that file;
/// <see cref="DeleteLegacyFile"/> removes one left by an earlier version.
/// </remarks>
internal sealed class GroupChatBuffers
{
    /// <summary>Where earlier versions kept the buffers, relative to the agent's work directory.</summary>
    internal const string LegacyFileRelativePath = ".fleet/chat-history.json";

    private readonly ConcurrentDictionary<long, GroupChatBuffer> _groupBuffers = new();

    internal GroupChatBuffer GetGroupBuffer(long chatId) =>
        _groupBuffers.GetOrAdd(chatId, id => new GroupChatBuffer { ChatId = id });

    /// <summary>
    /// The buffer for a chat that already has one. It does not create a buffer: the proactive
    /// sweep must not make every allowed group look like it has messages.
    /// </summary>
    internal bool TryGetExisting(long chatId, out GroupChatBuffer buffer) =>
        _groupBuffers.TryGetValue(chatId, out buffer!);

    /// <summary>
    /// Deletes the chat history file an earlier version left in <paramref name="workDir"/>, once,
    /// at startup, and logs that it did. Never throws: a file that cannot be deleted is logged and
    /// stays, unread.
    /// </summary>
    internal static void DeleteLegacyFile(string workDir, ILogger logger)
    {
        var path = Path.Combine(workDir, LegacyFileRelativePath);
        try
        {
            if (!File.Exists(path))
                return;

            File.Delete(path);
            logger.LogInformation(
                "Deleted the chat history file {Path}: chat buffers are kept in memory only", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                "The chat history file {Path} could not be deleted ({Error}); it is no longer read or written",
                path, ex.GetType().Name);
        }
    }
}
