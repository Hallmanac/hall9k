using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A dependent claimed with more blockers than DaemonOptions.BlockerSynthesisThreshold gets a
/// synthesis session first (Decisions Log #36): a platform-dispatched agent that condenses its
/// blockers' handoffs into one context document before the build session starts. It follows
/// the review-session patterns — recorded model (log #33), tokens recorded on the run, and
/// artifacts in the dependent run's own directory.
/// </summary>
public sealed record ContextSynthesisDispatched(
    Guid Id,
    Guid SessionId,
    int BlockerCount,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model = null);
