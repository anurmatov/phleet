namespace Fleet.Conversations.Journal;

/// <summary>
/// The one place the two questions about media are answered together, so a route cannot ask one
/// and forget the other.
/// </summary>
/// <remarks>
/// <c>IsAvailableAsync</c> is the per-request gate; <see cref="MediaState"/> is the label the status
/// route prints. A route that checked availability and reported a state it read at a different
/// moment could tell an operator "enabled" while answering <c>503</c>.
/// </remarks>
public interface JournalMediaGate : IJournalStoreHealth;
