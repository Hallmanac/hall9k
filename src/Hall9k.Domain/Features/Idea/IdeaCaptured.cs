namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// The whole of capture: a thought, whose it is, and when (Decisions Log #35). ProjectId is
/// nullable because an idea may precede its project — or become one — and demanding a project
/// at capture would defeat the one thing capture is for.
/// <para>
/// The discovery workspace is deliberately absent: its path is derived from the id
/// (<see cref="Hall9k.Domain.Infrastructure.Storage.IdeaPaths"/>), exactly as a run's
/// directory is, so the stream never records a fact it can recompute — and never records what
/// accumulates inside it.
/// </para>
/// </summary>
public sealed record IdeaCaptured(
    Guid Id,
    Guid OwnerId,
    string Text,
    Guid? ProjectId,
    DateTimeOffset CapturedAt);
