using System.Collections.Concurrent;
using Fleet.Protocol;

namespace Fleet.Agent.Services;

/// <summary>A conversation's channel-scoped identity, before it has a runtime key.</summary>
public sealed record ConversationRef(string ChannelId, string ConversationId, string PrincipalId);

/// <summary>
/// Maps channel-scoped conversations onto the <c>long</c> keys the runtime already uses (D2).
///
/// Changing <c>TaskManager</c> and <c>SessionManager</c> from <c>long</c> to a typed id would
/// touch every dictionary, every test and the session map for no benefit this phase needs. The
/// registry gives the same routing identity without the churn.
/// </summary>
public interface IConversationRegistry
{
    /// <summary>Allocate, or return, the runtime key for a conversation.</summary>
    long Resolve(ConversationRef reference);

    /// <summary>Reverse resolution for publishers. Null when the key is not registered.</summary>
    ConversationRef? Lookup(long runtimeKey);

    /// <summary>True when the key falls in the reserved (non-Telegram) band.</summary>
    bool IsReserved(long runtimeKey);

    /// <summary>Drop a conversation. Used so the per-conversation terminal outbox can be freed.</summary>
    bool Unregister(long runtimeKey);
}

/// <inheritdoc/>
public sealed class ConversationRegistry : IConversationRegistry
{
    /// <summary>
    /// Non-Telegram conversations are allocated from a POSITIVE band.
    ///
    /// This is the correction to the obvious-looking design. A band at <c>long.MinValue</c> would
    /// be negative, and five live sites treat a negative key as "this is a group": the channel
    /// anchor renders <c>group</c> instead of <c>dm</c>, and three separate
    /// <c>SuppressToolMessages &amp;&amp; chatId &lt; 0</c> checks suppress queue notices. A negative
    /// reserved band would therefore have silently changed behaviour on paths nobody edited.
    ///
    /// The Telegram Bot API guarantees chat identifiers fit in 52 significant bits, so every real
    /// key satisfies <c>|chatId| &lt; 2^52</c>. Starting at <c>2^56</c> makes collision impossible
    /// while keeping every reserved key positive, so no existing heuristic has to be removed or
    /// edited — which is what keeps this change additive.
    /// </summary>
    public const long ReservedBandStart = 1L << 56;

    /// <summary>Exclusive upper bound: <c>2^56 + 2^32</c>.</summary>
    public const long ReservedBandEnd = ReservedBandStart + (1L << 32);

    private readonly ConcurrentDictionary<ConversationRef, long> _keysByRef = new();
    private readonly ConcurrentDictionary<long, ConversationRef> _refsByKey = new();
    private readonly object _allocationLock = new();
    private long _nextKey = ReservedBandStart;

    /// <summary>True for any key in <c>[2^56, 2^56 + 2^32)</c>, whether registered or not.</summary>
    public static bool IsReservedKey(long runtimeKey) =>
        runtimeKey >= ReservedBandStart && runtimeKey < ReservedBandEnd;

    /// <inheritdoc/>
    public bool IsReserved(long runtimeKey) => IsReservedKey(runtimeKey);

    /// <inheritdoc/>
    public long Resolve(ConversationRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (_keysByRef.TryGetValue(reference, out var existing))
            return existing;

        // Telegram registers its real chat ids verbatim so existing keys are untouched, and a
        // re-open of the same Telegram conversation cannot consume a reserved slot.
        if (TryParseTelegramKey(reference, out var telegramKey))
        {
            _keysByRef[reference] = telegramKey;
            _refsByKey[telegramKey] = reference;
            return telegramKey;
        }

        lock (_allocationLock)
        {
            if (_keysByRef.TryGetValue(reference, out existing))
                return existing;

            if (_nextKey >= ReservedBandEnd)
            {
                // Exhaustion throws rather than wrapping: wrapping would silently alias two live
                // conversations onto one runtime key, which is a cross-route leak.
                throw new InvalidOperationException(
                    $"Reserved conversation key band exhausted ({ReservedBandEnd - ReservedBandStart} keys in one process lifetime).");
            }

            var key = _nextKey++;
            _keysByRef[reference] = key;
            _refsByKey[key] = reference;
            return key;
        }
    }

    /// <inheritdoc/>
    public ConversationRef? Lookup(long runtimeKey) =>
        _refsByKey.TryGetValue(runtimeKey, out var reference) ? reference : null;

    /// <inheritdoc/>
    public bool Unregister(long runtimeKey)
    {
        if (!_refsByKey.TryRemove(runtimeKey, out var reference))
            return false;
        _keysByRef.TryRemove(reference, out _);
        return true;
    }

    private static bool TryParseTelegramKey(ConversationRef reference, out long key)
    {
        key = 0;
        if (!string.Equals(reference.ChannelId, ChannelIds.Telegram, StringComparison.Ordinal)
            && !string.Equals(reference.ChannelId, ChannelIds.Relay, StringComparison.Ordinal))
            return false;

        return long.TryParse(reference.ConversationId, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out key);
    }
}

/// <summary>Well-known channel ids. These two are owned by the existing runtime paths.</summary>
public static class ChannelIds
{
    /// <summary>The first-class Telegram adapter. No <c>IChannelAdapter</c> is registered for it.</summary>
    public const string Telegram = "telegram";

    /// <summary>Relay/bridge traffic. Never delivered to a client adapter (Constraint 7).</summary>
    public const string Relay = "relay";

    /// <summary>True when the channel is a built-in runtime path rather than a client adapter.</summary>
    public static bool IsRuntimeOwned(string? channelId) =>
        string.Equals(channelId, Telegram, StringComparison.Ordinal)
        || string.Equals(channelId, Relay, StringComparison.Ordinal);
}
