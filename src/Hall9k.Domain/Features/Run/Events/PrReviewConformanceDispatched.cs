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
/// <para>
/// Since review personas (idea b9b09779, piece 1) a pr-review run can owe more than one
/// follow-on session — one per persona beyond the first, which is always the primary — so this
/// event carries which one it dispatched. It keeps its name: the engineer's conformance lens is
/// what every stream written before personas existed recorded here, and renaming the event would
/// have made those streams unreadable to buy nothing.
/// </para>
/// </summary>
/// <param name="Slug">
/// Which of the plan's sessions this dispatch is (<c>ReviewPersonaSession.Slug</c>). Trailing and
/// defaulted to the empty string so an older stream still deserializes; blank reads as the
/// engineer's conformance lens, which is the only follow-on session that existed when those
/// streams were written — what shipped, not a guess about what they meant.
/// </param>
public sealed record PrReviewConformanceDispatched(
    Guid Id,
    Guid SessionId,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel Model,
    string SessionName = "",
    string Slug = "");
