using Hall9k.Domain.Shared.ValueObjects;

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
/// finished, correct implementation. Model is the model the RESUMED session already runs
/// on, carried forward and recorded rather than re-resolved: a resumed session keeps the
/// model it started with, so re-resolving would record a model it never used (log #33).
/// Lens says which pass of the cycle is being re-prompted (log #59); the one re-prompt is
/// the CYCLE's, not each lens's, so a second verdict-less pass in the same cycle parks
/// rather than doubling the parking math.
/// </summary>
public sealed record ReviewVerdictReprompted(
    Guid Id,
    Guid SessionId,
    Guid ResumedSessionId,
    int Cycle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset RepromptedAt,
    AgentModel? Model = null,
    ReviewLens? Lens = null,
    string SessionName = "");
