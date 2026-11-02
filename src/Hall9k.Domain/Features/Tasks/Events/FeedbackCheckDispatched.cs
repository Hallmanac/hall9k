using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>The feedback check's headless session was spawned. Recorded before anything starts, so a crash leaves a dispatch with no outcome that the next start records as failed.</summary>
/// <param name="Id">The task's id.</param>
/// <param name="SessionId">The check session.</param>
/// <param name="NodeId">The node that runs it.</param>
/// <param name="DispatchedAt">When it was dispatched.</param>
/// <param name="Model">The model the check runs on, from the model policy's feedback check role.</param>
/// <param name="Effort">The effort the check runs at.</param>
public sealed record FeedbackCheckDispatched(
    Guid Id,
    Guid SessionId,
    Guid NodeId,
    DateTimeOffset DispatchedAt,
    AgentModel Model,
    AgentEffort Effort);
