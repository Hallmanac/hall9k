namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// What a courier run came to (idea 89471598, piece 3). <see cref="Delivered"/> is read off the
/// session's own terminal result — <c>CourierDeliveryOutcome.Parse</c> looking for
/// <c>CourierPromptBuilder.DeliveredMarker</c> in its final message — never off whether the
/// project's own feed cursor happens to advance, since that is the daemon's own doing
/// <em>after</em> this event is recorded, not a fact the session could observe about itself. The
/// cursor advance is downstream of <see cref="Delivered"/> here, not the other way around:
/// <c>CourierEngine.RecordOutcomeAsync</c> drains the feed only when this event's own
/// <see cref="Delivered"/> is true, and leaves the cursor exactly where it stood otherwise, so the
/// next courier tries the same items again.
/// </summary>
public sealed record CourierRunCompleted(
    Guid Id,
    bool Delivered,
    string Outcome,
    DateTimeOffset CompletedAt);
