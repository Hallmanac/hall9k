namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// One lens of a review cycle returned its verdict (Decisions Log #59). A cycle runs every
/// lens in <see cref="ReviewLens.CycleLenses"/> as independent passes, so this is the
/// per-pass milestone and <see cref="ReviewCompleted"/> is the cycle's merged one; both are
/// appended in the same transaction when the last pass of a cycle lands.
/// <para>
/// The lens rides on the event because the review history is what teaches which lens earns
/// its keep: "the adversarial pass found this, the conformance pass did not" is a query over
/// the stream rather than an impression. The findings themselves stay artifacts in the run's
/// directory (review-&lt;cycle&gt;-&lt;lens&gt;-findings.md), never event payload (log #6).
/// </para>
/// </summary>
public sealed record ReviewPassCompleted(
    Guid Id,
    int Cycle,
    ReviewLens Lens,
    ReviewVerdict Verdict,
    DateTimeOffset CompletedAt);
