namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// One of an idea's two terminal acts, always an explicit human decision (Brian, 2026-08-21):
/// discovery happened and something came of it — tasks were cut, or an outcome was acted on
/// some other way. Cutting a task never appends this on its own; discovery may keep producing,
/// so an idea concludes only when a human says it is done, not when the first (or the fifth)
/// task comes out of it.
/// <para>
/// Reconciles the shipped 1:1 <c>IdeaPromoted</c> (backlog 22), which always meant the same
/// thing — something came of the idea — without a reason attached. <c>h9k idea promote</c>
/// survives as sugar over cutting one task and concluding in the same breath; every other path
/// to concluding goes through this event directly.
/// </para>
/// </summary>
public sealed record IdeaConcluded(
    Guid Id,
    string Reason,
    DateTimeOffset ConcludedAt,
    Guid ConcludedByOwnerId);
