using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The pr-review run's second lens: the adversarial lens is this run's ordinary primary
/// session (RunDispatched/RunProcessStarted, unchanged), so only the conformance lens —
/// dispatched afterward, once the adversarial session's findings are on disk — needs its
/// own record. Deliberately not ReviewDispatched: that event feeds ReviewEngine's own
/// cycle/track state machine (fix loop, dispute, severity gate), none of which applies to
/// reviewing someone else's pull request read-only. Moves the run to UnderReview so a
/// restarted daemon's adoption sweep resumes it through PrReviewEngine rather than
/// mistaking the (already-exited) primary session's process for this one's.
/// </summary>
public sealed record PrReviewConformanceDispatched(
    Guid Id,
    Guid SessionId,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel Model);
