using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// Which of a task's replicated events concern this node's owner, by card C's ownership rule
/// (<see cref="TaskOwnerRule"/>, the same answer the task commands and the viewer's board give).
/// Two decisions live here so <see cref="OrchestratorFeedSelection"/> reads as one flow: what a
/// teammate's activity drops, and when another root ending the viewer's own task is worth paging
/// over.
/// <para>
/// Both apply only to a replicated event naming a task whose scope carries ownership facts. A
/// local event is this node's own work, and a scope with no task or no facts (a message, a root
/// rotation, an idea) is not a task event at all, so those read exactly as they always have.
/// </para>
/// </summary>
public static class OrchestratorFeedOwnership
{
    /// <summary>
    /// Whether this replicated event is about a task the viewer's root may not act on, and is not one
    /// of the three take-flow facts the viewer is a party to. A task whose owner cannot be resolved
    /// counts as a teammate's, as it does on the board.
    /// </summary>
    public static bool IsTeammatesActivity(
        OrchestratorFeedCandidate candidate, OrchestratorFeedScope scope, OrchestratorFeedViewer? viewer)
    {
        if (!candidate.IsReplicated
            || scope.TaskId is null
            || scope.OwnerFacts is not { } facts
            || viewer?.OwnerRootFingerprint is not { Length: > 0 } root)
        {
            return false;
        }

        return !TaskOwnerRule.Decide(root, facts).MayAct && !ViewerIsPartyTo(candidate.Data, root, viewer);
    }

    /// <summary>
    /// Whether this is a replicated <see cref="TaskAbandoned"/> or <see cref="TaskResolved"/> whose
    /// origin root is a known root other than the viewer's. A fleet sibling shares the viewer's root,
    /// so its end is the viewer's own and never matches; an origin that is missing is not a different
    /// root, because nothing observed says whose act it was. Says nothing about whose task it is:
    /// <see cref="EndsViewersTask"/> adds that once the scope is known.
    /// </summary>
    public static bool IsEndFromAnotherRoot(OrchestratorFeedCandidate candidate, OrchestratorFeedViewer? viewer) =>
        candidate.IsReplicated
        && candidate.Data is TaskAbandoned or TaskResolved
        && viewer?.OwnerRootFingerprint is { Length: > 0 } root
        && candidate.OriginOwnerRootFingerprint is { Length: > 0 } origin
        && origin != root;

    /// <summary>
    /// Whether an end from another root landed on a task the viewer's root may act on, which is what
    /// makes it an urgent item rather than a transition line.
    /// </summary>
    public static bool EndsViewersTask(OrchestratorFeedScope scope, OrchestratorFeedViewer? viewer) =>
        scope.TaskId is not null
        && scope.OwnerFacts is { } facts
        && viewer?.OwnerRootFingerprint is { Length: > 0 } root
        && TaskOwnerRule.Decide(root, facts).MayAct;

    private static bool ViewerIsPartyTo(object eventData, string root, OrchestratorFeedViewer viewer) => eventData switch
    {
        TaskTakeRequested requested => requested.RequesterOwnerFingerprint == root,
        TaskTakeRefused refused =>
            (viewer.NodeId is { } nodeId && refused.RequesterNodeId == nodeId)
            || (viewer.OwnerId is { } ownerId && refused.RequesterOwnerId == ownerId),
        TaskHolderReleased released => released.GrantedToOwnerFingerprint == root,
        _ => false,
    };
}
