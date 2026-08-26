namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One human verdict on a review park (<c>h9k review resolve</c>), kept as history rather than
/// overwritten the way <see cref="Projections.RunDetails.LastReviewVerdict"/> is. A later review
/// pass — later in the same cycle-count, or a retried run on the same task — is handed these so
/// it can be told a question was already settled instead of re-raising it fresh (the origin
/// incident: the config.json survival ruling was re-litigated three times across one task's
/// twelve review cycles, and a finding dismissed with git-ancestry evidence was re-raised
/// verbatim by the next fresh-context reviewer).
/// </summary>
/// <param name="Cycle">The review cycle the park happened at, so a reader can place the ruling.</param>
/// <param name="Verdict">MergeReady or NeedsFixes — the human's own call.</param>
/// <param name="Reason">
/// The human's own text: required on NeedsFixes, optional on MergeReady (<c>--reason</c>).
/// Null means none was recorded, not that nothing was decided.
/// </param>
/// <param name="ResolvedAt">When the human recorded it.</param>
public sealed record ReviewParkResolution(
    int Cycle,
    ReviewVerdict Verdict,
    string? Reason,
    DateTimeOffset ResolvedAt);
