using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// One independent review pass was spawned over the run's diff before its pull request
/// opens (Decisions Log #23): a separate headless session with fresh context — never the
/// session that wrote the code. Cycle counts review rounds from 1, and each cycle dispatches
/// one of these per lens (log #59), so two dispatch events per cycle is the shipped shape.
/// ProcessId + start time are the session's identity for adoption (the PID-reuse guard,
/// log #2). Model is the resolved model this pass was spawned on, recorded per pass as an
/// observed fact (log #33). Lens is which attention budget this pass carries; null on
/// streams written before lenses existed. RunState → UnderReview.
/// </summary>
public sealed record ReviewDispatched(
    Guid Id,
    Guid SessionId,
    int Cycle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model = null,
    ReviewLens? Lens = null);
