namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The review session ended without a parseable VERDICT line and was re-prompted ONCE,
/// in the same session (claude -p --resume, the log #5 resume pattern): the reviewer
/// already read the diff, so a fresh session would re-spend that work, but the resumed
/// session only needs to conclude. Exactly one re-prompt per cycle — a second
/// verdict-less ending parks the run (never loop on judgment, log #11). SessionId is
/// this leg's own identity (artifact naming; the resumed transcript must not collide
/// with the original leg's files); ResumedSessionId is the review session it re-enters.
/// Origin incident (2026-08-18): the first live review ended with "I'll deliver findings
/// and the verdict when it completes" — a promise, not a verdict — and parked a
/// finished, correct implementation.
/// </summary>
public sealed record ReviewVerdictReprompted(
    Guid Id,
    Guid SessionId,
    Guid ResumedSessionId,
    int Cycle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset RepromptedAt);
