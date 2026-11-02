using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Feedback;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Handlers;

/// <summary>
/// The decisions of a task's feedback step, pure and over the aggregate alone, so what the step may do at each point is
/// provable with no database and no process. The daemon decides here and performs the effects (a session, a plan, a save);
/// nothing in <see cref="TaskAggregate"/> decides anything. The step is: held at the release point, begun by one node, checked
/// over the blockers' notes, and then either released, or refined, challenged once, published, and queued again, or parked.
/// </summary>
public static class FeedbackStepDecider
{
    /// <summary>
    /// Whether this node may begin the step: the task is held, and it is placed on this node or, with no placement, queued for
    /// this node's owner. No other node begins one, a teammate's included. The same two predicates the dispatcher's claim gate
    /// asks, so "whose task is this and where does it run" is answered one way.
    /// </summary>
    public static bool MayBeginOnThisNode(
        TaskAggregate task, Guid thisNodeId, Guid thisOwnerId, string? thisOwnerRootFingerprint) =>
        task is { State: var state, FeedbackStepHeld: true, FeedbackStepBegun: false }
        && state == TaskState.Blocked
        && TaskDecider.IsGrantedToThisOwner(task.AssignedOwnerId, task.AssignedOwnerFingerprint, thisOwnerId, thisOwnerRootFingerprint)
        && TaskDecider.IsPlacedOnThisNode(task.PlacedOnNodeId, thisNodeId);

    /// <summary>
    /// The step begins, recording what the requeue restores. Refused unless the task is held and its step has not begun; the
    /// caller appends it under the task stream's version, which is what lets only one of two nodes that read the same hold begin it.
    /// </summary>
    public static FeedbackStepStarted Start(TaskAggregate task, Guid nodeId, DateTimeOffset now)
    {
        if (task.State != TaskState.Blocked || !task.FeedbackStepHeld || task.FeedbackStepBegun)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is not held for a feedback step that has yet to begin, so none can begin.");
        }

        return new FeedbackStepStarted(
            task.Id, nodeId, now, task.AssignedOwnerId, task.AssignedOwnerFingerprint, task.PlacedOnNodeId, [.. task.BlockedBy]);
    }

    /// <summary>The check's session was spawned. Refused while the step is not running, a check is already running, or the check was already recorded.</summary>
    public static FeedbackCheckDispatched DispatchCheck(
        TaskAggregate task, Guid sessionId, Guid nodeId, AgentModel model, AgentEffort effort, DateTimeOffset now)
    {
        RequireRunning(task);
        return task.RunningFeedbackCheckSessionId is not null || task.LatestFeedbackCheck is not null || task.LatestFeedbackCheckDispatch is not null
            ? throw new DomainConflictException($"Task {task.Id}'s feedback check has already been dispatched; a step checks once.")
            : new FeedbackCheckDispatched(task.Id, sessionId, nodeId, now, model, effort);
    }

    /// <summary>How the check ended. A failure carries the closed reason; the step releases the task either way a check does not say it is affected.</summary>
    public static FeedbackCheckRecorded RecordCheck(
        TaskAggregate task, Guid? sessionId, Guid nodeId, FeedbackCheckAnswer answer, RefinementFailureReason failure, DateTimeOffset now)
    {
        RequireRunning(task);
        return new FeedbackCheckRecorded(task.Id, sessionId, nodeId, answer, answer.IsAnswer ? RefinementFailureReason.Unknown : failure, now);
    }

    /// <summary>
    /// The check said the task is affected: the task leaves the queue and returns to Draft, so a plan can refine it. The two
    /// moves are the same deciders a person's <c>h9k task dequeue</c> and <c>h9k task draft</c> use, applied to the aggregate in
    /// turn, and the dequeue keeps the assignee. The caller appends both with the begun event, in one save.
    /// </summary>
    public static (TaskUnassigned Dequeued, TaskReturnedToDraft Drafted, FeedbackStepRefinementBegun Begun) TakeBackToDraft(
        TaskAggregate task, Guid planId, Guid ownerId, DateTimeOffset now)
    {
        RequireRunning(task);
        if (!task.FeedbackStepHeld || task.State != TaskState.Blocked)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is no longer held for its feedback step, so it cannot be taken back to Draft for it.");
        }

        TaskUnassigned dequeued = TaskDecider.Dequeue(
            task, "feedback step: the blockers' notes affect this task", leaseHeld: false, now, ownerId);
        task.Apply(dequeued);
        TaskReturnedToDraft drafted = TaskDecider.ReturnToDraft(
            task, "feedback step: refining the task against its blockers' notes", now, ownerId);
        return (dequeued, drafted, new FeedbackStepRefinementBegun(task.Id, planId, now));
    }

    /// <summary>
    /// The queue-again move once the plan has published the refined task: the same owner, the same owner fingerprint, and the
    /// same node placement the task had before the step, which the dequeue cleared. Refused when anything about the task is not
    /// what the step left: it must be Published, still held by the owner it was queued for, and the step's own.
    /// </summary>
    public static TaskAssigned Requeue(
        TaskAggregate task, IReadOnlyList<TaskDependency> dependencies, DateTimeOffset now)
    {
        RequireRunning(task);
        FeedbackStepStarted started = task.FeedbackStep
            ?? throw new DomainConflictException($"Task {task.Id}'s feedback step recorded no start, so there is nothing to restore.");
        if (started.QueuedForOwnerId is not { } ownerId)
        {
            throw new DomainConflictException($"Task {task.Id} was queued for nobody when its feedback step began, so it cannot be queued again.");
        }

        if (task.AssigneeOwnerId != ownerId)
        {
            throw new DomainConflictException(
                $"Task {task.Id}'s assignee changed while its feedback step ran, so the step does not queue it again for the owner it began with.");
        }

        // A queued task takes the step at most once: the begun event is on the stream, so Holds is false and this lands Queued
        // because its blockers have cleared, never held a second time.
        return TaskDecider.Assign(
            task, ownerId, dependencies, now, ownerId, started.QueuedForOwnerFingerprint,
            Optional<Guid?>.Of(started.PlacedOnNodeId), projectTakesFeedbackStep: false);
    }

    /// <summary>
    /// The step is over, and how. Refused when it already is. A released outcome is also the move to Queued, so it is refused for a
    /// task that is not held; every other outcome ends a step that may or may not still hold the task.
    /// </summary>
    public static FeedbackStepOutcomeRecorded Conclude(
        TaskAggregate task, FeedbackStepOutcome outcome, string reason, DateTimeOffset now)
    {
        RequireRunning(task);
        if (!outcome.IsKnown)
        {
            throw new DomainValidationException("A feedback step ends in a known outcome.");
        }

        if (outcome.ReleasesTask && (!task.FeedbackStepHeld || task.State != TaskState.Blocked))
        {
            throw new DomainConflictException(
                $"Task {task.Id} is not held for its feedback step, so there is nothing to release.");
        }

        return new FeedbackStepOutcomeRecorded(task.Id, outcome, reason, now);
    }

    private static void RequireRunning(TaskAggregate task)
    {
        if (!task.FeedbackStepRunning)
        {
            throw new DomainConflictException(
                $"Task {task.Id}'s feedback step is not running" + (task.FeedbackStepResult is { } result
                    ? $" (it ended: {result.Outcome.Description})."
                    : " (it has not begun)."));
        }
    }
}
