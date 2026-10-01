using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// Which of a task's replicated events concern this node's owner, by the viewer's board rule
/// (<see cref="TaskViewerRule"/>: card C's ownership rule, <see cref="TaskOwnerRule"/>, plus a take
/// request pending from the viewer's root, so the feed and the board answer alike for a task).
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
    /// Whether this replicated event is about a task that is not the viewer's, and is not one of the
    /// facts the viewer is a party to (the take flow, and the loss of a task the viewer held). A task
    /// whose owner cannot be resolved counts as a teammate's, as it does on the board.
    /// <para>
    /// The task's owner is read as the board row stands when the feed is read, not as it stood when
    /// the event happened, so an event that itself moved the task away from the viewer finds it
    /// already a teammate's. Those events are exempted by what they say
    /// (<see cref="ViewerIsPartyTo"/>), never by the row.
    /// </para>
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

        return !TaskViewerRule.Decide(root, facts, scope.PendingTakeRequesterRoot).IsViewers
            && !ViewerIsPartyTo(candidate, root, viewer);
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

    private static bool ViewerIsPartyTo(OrchestratorFeedCandidate candidate, string root, OrchestratorFeedViewer viewer) =>
        candidate.Data switch
        {
            TaskTakeRequested requested => requested.RequesterOwnerFingerprint == root,
            TaskTakeRefused refused =>
                (viewer.NodeId is { } nodeId && refused.RequesterNodeId == nodeId)
                || (viewer.OwnerId is { } ownerId && refused.RequesterOwnerId == ownerId),
            // A grant to the viewer's root, or one recorded under the viewer's own root (a fleet
            // sibling) that handed the task to somebody else: the viewer is on one side of it.
            TaskHolderReleased released =>
                released.GrantedToOwnerFingerprint == root
                || (released.GrantedToOwnerFingerprint is { Length: > 0 }
                    && candidate.OriginOwnerRootFingerprint == root),
            // A forced takeover from this node: the viewer is the holder it took the task from.
            TaskHolderTakenOver takenOver =>
                viewer.NodeId is { } nodeId && takenOver.PreviousHolderNodeId == nodeId,
            _ => false,
        };
}
