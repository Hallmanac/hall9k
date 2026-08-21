namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A review cycle concluded (Decisions Log #23): the merged verdict over every lens the
/// cycle ran (log #59), appended in the same transaction as the last
/// <see cref="ReviewPassCompleted"/> of that cycle. The milestone only — the findings text
/// is an artifact in the run's directory (review-&lt;cycle&gt;-findings.md, the merged
/// document the fix session reads), never event payload (log #6).
/// <para>
/// MergeReady — which requires every lens to be clean — lets PullRequestOpener proceed;
/// NeedsFixes dispatches one fix run over the merged findings of both lenses; Unknown (any
/// lens returned no parseable verdict) re-prompts once, then parks the run for a human.
/// </para>
/// </summary>
public sealed record ReviewCompleted(
    Guid Id,
    int Cycle,
    ReviewVerdict Verdict,
    DateTimeOffset CompletedAt);
