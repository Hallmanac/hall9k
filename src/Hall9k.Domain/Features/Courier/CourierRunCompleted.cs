namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// What a courier run came to (idea 89471598, piece 3). <see cref="Delivered"/> is read off
/// whether the project's own feed cursor actually advanced past where it stood before this
/// courier was spawned — never off anything the session merely said about itself — the same
/// "read the record, not the transcript" discipline <c>WorkItemPublicationCompleted.Linked</c>
/// already applies to card publication. A courier that ended without advancing the cursor
/// recorded <see cref="Delivered"/> false regardless of what its own final message claimed, and
/// the daemon's own spawn gate leaves the cursor exactly where it was so the next courier tries
/// the same items again.
/// </summary>
public sealed record CourierRunCompleted(
    Guid Id,
    bool Delivered,
    string Outcome,
    DateTimeOffset CompletedAt);
