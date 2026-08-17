namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The review agent for the given cycle returned its verdict (Decisions Log #23). The
/// milestone only — the full findings text is an artifact in the run's directory
/// (review-&lt;cycle&gt;-findings.md), never event payload (log #6). MergeReady lets
/// PullRequestOpener proceed; NeedsFixes dispatches a fix run; Unknown (no parseable
/// verdict) parks the run for a human.
/// </summary>
public sealed record ReviewCompleted(
    Guid Id,
    int Cycle,
    ReviewVerdict Verdict,
    DateTimeOffset CompletedAt);
