namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A resolve-review-threads follow-up gave every unresolved thread it triaged exactly one
/// disposition before touching any code (task: every review thread on a pull request gets a
/// triage disposition before any fix work). Recorded once per follow-up run, off the same closing
/// summary <see cref="ReviewFeedbackReceived"/>'s dispatch-time count is read ahead of — this
/// event is the after-the-fact answer to what the platform only asked as a number.
/// <para>
/// It moves nothing on <c>RunAggregate</c> — a follow-up that pushes no commit because every
/// thread declined or routed is already the ordinary "no diff to gate" shape the pipeline handles
/// ever since a thread could be answered without a code change. It exists so <c>h9k task show</c>
/// can render the latest triage's disposition counts and so the decline rate — laps bought by a
/// thread that turned out not to hold up — is a number rather than an impression. It is NOT
/// otherwise informational-only, though: <c>CloseoutEngine</c> reads the human-authored subset of
/// <c>RunDetails.LastReviewThreadOutcomes</c> this event replaces to decide whether a thread still
/// unresolved on a later sweep is one this run already answered and is deliberately leaving open,
/// which suppresses that sweep's own follow-up dispatch (Decisions Log #159's dispatch-suppression
/// clause, independent pre-PR review, cycle 3, adversarial lens — added after this doc comment was
/// first written, which is why it still read "gates nothing" unqualified).
/// </para>
/// </summary>
public sealed record ReviewThreadsTriaged(
    Guid Id,
    IReadOnlyList<ReviewThreadOutcome> Threads,
    DateTimeOffset TriagedAt);
