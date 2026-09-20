namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// One thing that happened, as an orchestrator reads it (idea 89471598, piece 2). Built from a
/// raw event at read time and never stored: <see cref="Sequence"/> is that event's own global
/// sequence on this node, which is what a drain advances the cursor to and what orders the feed.
/// </summary>
/// <param name="TaskId">
/// The task this item belongs under, or null when it belongs to no task — a project-level
/// setting change, an idea, a message about nothing in particular.
/// <see cref="OrchestratorFeedRenderer"/> groups on it.
/// </param>
/// <param name="Description">
/// The one-line plain description, already composed by
/// <see cref="OrchestratorFeedDescription"/>. Never a bare id.
/// </param>
/// <param name="IsUrgent">
/// Whether the feed courier's own spawn gate (idea 89471598, piece 3) dispatches at once for
/// this item regardless of the batching wait — <see cref="OrchestratorFeedUrgency"/>'s own table.
/// </param>
public sealed record OrchestratorFeedItem(
    long Sequence,
    DateTimeOffset At,
    Guid? TaskId,
    string Description,
    bool IsUrgent = false);
