namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// One lens of a review cycle returned its verdict (Decisions Log #59). A cycle runs every
/// still-active track as an independent pass, so this is the per-pass milestone and
/// <see cref="ReviewCompleted"/> is the cycle's merged one; both are appended in the same
/// transaction when the last pass of a cycle lands.
/// <para>
/// The lens rides on the event because the review history is what teaches which lens earns
/// its keep: "the adversarial pass found this, the conformance pass did not" is a query over
/// the stream rather than an impression. The findings themselves stay artifacts in the run's
/// directory (review-&lt;cycle&gt;-&lt;lens&gt;-findings.md), never event payload (log #6).
/// </para>
/// <para>
/// <see cref="Findings"/> carries each finding's grade, scope tag, pointer, and the
/// disposition the loop chose for it (Decisions Log #63) — the classification, never the text.
/// That is what lets the history answer which severities actually forced cycles and which
/// track produced them. Null on streams written before findings were classified; an empty
/// list on a clean pass, which is a different fact and is recorded as one.
/// </para>
/// <para>Mode is which shape the cycle this pass belongs to took; null reads as <see cref="ReviewMode.Discovery"/>.</para>
/// </summary>
public sealed record ReviewPassCompleted(
    Guid Id,
    int Cycle,
    ReviewLens Lens,
    ReviewVerdict Verdict,
    DateTimeOffset CompletedAt,
    IReadOnlyList<ReviewFindingRecord>? Findings = null,
    ReviewMode? Mode = null);
