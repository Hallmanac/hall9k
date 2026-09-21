using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;

namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// The one event type each replicated aggregate's own stream can legally start from: Task, Idea,
/// Epic and Run each mint exactly one genesis event, and every other event on that stream only
/// ever means anything once the genesis is already applied. <c>Hall9k.Connectors.Replication.EventReplicationInbox</c>
/// uses this to refuse starting a local stream from anything else — a task whose
/// <see cref="TaskAdded"/> predates the sender's outbox, or a run whose parent task never arrived —
/// holding the record rather than letting Marten auto-vivify a headless document nothing will ever
/// repair (the same "starts a document even with no matching Create" behaviour
/// <c>IdeaDetailsProjection</c>'s own doc already names for legitimate out-of-order delivery).
/// </summary>
public static class AggregateGenesisEventTypes
{
    public static bool IsGenesis(Type eventType) =>
        eventType == typeof(TaskAdded)
        || eventType == typeof(IdeaCaptured)
        || eventType == typeof(EpicAdded)
        || eventType == typeof(RunDispatched);
}
