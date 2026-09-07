namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A changes-requested fix lap met a finding it did not agree with and stopped rather than
/// answering the reviewer itself (task: a changes-requested pull-request review from a human
/// becomes a fix lap). Nothing was posted and no thread was resolved: a disagreement with a
/// person is the implementer's to send, so the session drafted a reply and handed it over
/// (Brian's ruling, 2026-09-06 12:15).
/// <para>
/// Appended alongside the <see cref="ReviewParked"/> that actually parks the run — that event
/// already surfaces as NeedsHuman, keeps the lease refreshed, and is what
/// <c>h9k review resolve</c> answers. This one carries the structured positions that resolve
/// needs in order to offer the three choices: post the drafted reply as written, post an edited
/// one, or post nothing.
/// </para>
/// </summary>
public sealed record ReviewDisagreementParked(
    Guid Id,
    IReadOnlyList<ReviewDisagreement> Disagreements,
    DateTimeOffset ParkedAt);
