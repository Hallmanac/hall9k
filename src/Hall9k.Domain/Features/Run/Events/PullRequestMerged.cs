namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The closeout monitor observed the run's pull request merged — the closeout phase is
/// over and RunCompleted follows immediately (Decisions Log #18/#22). MergedAt is
/// GitHub's merge timestamp as reported by gh; null when the provider did not report one
/// (never guessed). ObservedAt is when this node's monitor saw it.
/// </summary>
public sealed record PullRequestMerged(
    Guid Id,
    DateTimeOffset? MergedAt,
    DateTimeOffset ObservedAt);
