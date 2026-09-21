using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;

namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// The one event type each replicated aggregate's own stream can legally start from: Task, Idea,
/// Epic, Run, Decision and Learning each mint exactly one genesis event, and every other event on
/// that stream only ever means anything once the genesis is already applied. <c>Hall9k.Connectors.Replication.EventReplicationInbox</c>
/// uses this to refuse starting a local stream from anything else — a task whose
/// <see cref="TaskAdded"/> predates the sender's outbox, or a run whose parent task never arrived —
/// holding the record rather than letting Marten auto-vivify a headless document nothing will ever
/// repair (the same "starts a document even with no matching Create" behaviour
/// <c>IdeaDetailsProjection</c>'s own doc already names for legitimate out-of-order delivery).
/// <para>
/// Every project-scoped event type in <c>EventScopeRegistry</c> whose stream this node can be
/// asked to START has to be named here, or the inbox holds it forever waiting on a genesis that
/// has already arrived and is the very record being held. Idea d805fd8b, piece 1, added Decision
/// and Learning for exactly that reason: classifying their events project-scoped is what makes
/// them travel, and this is what lets a receiving node apply the first one.
/// </para>
/// </summary>
public static class AggregateGenesisEventTypes
{
    public static bool IsGenesis(Type eventType) =>
        eventType == typeof(TaskAdded)
        || eventType == typeof(IdeaCaptured)
        || eventType == typeof(EpicAdded)
        || eventType == typeof(RunDispatched)
        || eventType == typeof(DecisionRecorded)
        || eventType == typeof(LearningRecorded);
}
