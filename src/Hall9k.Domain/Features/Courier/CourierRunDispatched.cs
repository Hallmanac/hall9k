using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// A courier spawned to deliver a project's orchestrator feed to its live orchestrator session
/// (idea 89471598, piece 3) — the run with no task the courier role needs: there is no
/// <see cref="Features.Tasks.TaskAggregate"/> and no <see cref="Features.Run.RunAggregate"/> to
/// carry this on, so it opens its own stream keyed by <see cref="Id"/>, a fresh id minted the
/// same way <c>WorkItemPublicationDispatched</c>'s own <c>SessionId</c> is — and, unlike that
/// event, the stream key and the session's own artifact key are the identical id, since there is
/// no owning task for a second id to distinguish this from.
/// <para>
/// Node-scoped, like every event in <c>Features.Orchestrator</c> this courier's own spawn
/// decision reads (presence, the feed cursor): a courier only ever delivers to an orchestrator
/// window on the machine it runs on, so a record of it replicating to another node would describe
/// a delivery nobody there could ever have observed.
/// </para>
/// </summary>
public sealed record CourierRunDispatched(
    Guid Id,
    Guid ProjectId,
    Guid NodeId,
    AgentModel Model,
    DateTimeOffset DispatchedAt);
