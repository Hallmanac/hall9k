namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The run's handoff to whatever depends on it (Decisions Log #36): what it did, what a
/// dependent needs to know, what it deliberately left undone. Captured from the agent's own
/// session-end result, never authored by a separate session — but appended here, by the
/// closeout monitor, in the same transaction as PullRequestMerged and RunCompleted. Two
/// moments, one fact, honestly ordered: a run whose pull request never merges never hands
/// anything down, because the event that carries the handoff lands only at true closeout.
/// </summary>
/// <param name="Outcome">
/// Whether there is a handoff at all, and when there is not, why. A missing handoff is a
/// recorded answer, never a silently empty string.
/// </param>
/// <param name="Summary">
/// The handoff text, bounded — the stream carries milestones, and the run directory's
/// handoff.md is the inspectable copy (log #6). Null whenever <paramref name="Outcome"/>
/// says there is none.
/// </param>
public sealed record RunHandoffRecorded(
    Guid Id,
    HandoffOutcome Outcome,
    string? Summary,
    DateTimeOffset RecordedAt);
