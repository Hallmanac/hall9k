using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Tasks.Feedback;

/// <summary>
/// The rules about the feedback step that more than one decider asks, in one place: whether a task takes it, whether the
/// release point holds it, and what a held task refuses. Pure over the aggregate and the project's one setting.
/// </summary>
public static class FeedbackStepRules
{
    /// <summary>
    /// Whether the task takes the feedback step: its own override either way, otherwise the project's setting, which is off
    /// until a person turns it on.
    /// </summary>
    public static bool Takes(TaskAggregate task, bool projectTakesFeedbackStep) =>
        task.FeedbackStepOverride ?? projectTakesFeedbackStep;

    /// <summary>
    /// Whether the release point holds this task: it takes the step, the step has not begun (it never runs twice), and it has
    /// blockers, which is where notes come from. A pull-request review has none to read and never takes the step. A task with no
    /// blockers is queued as it always was, because there is nothing for a check to read.
    /// </summary>
    public static bool Holds(TaskAggregate task, bool projectTakesFeedbackStep) =>
        !task.FeedbackStepBegun
        && task.Type != TaskType.PrReview
        && task.BlockedBy.Count > 0
        && Takes(task, projectTakesFeedbackStep);

    /// <summary>
    /// Refuses a claim of a task held for its feedback step, naming why and what ends the hold. A held task is Blocked with
    /// nothing left to wait for, so no flag on the claim, including acknowledging unmet dependencies, can answer it: there is
    /// no dependency to acknowledge, and the step decides what the task's blockers' notes mean for it before anyone works it.
    /// </summary>
    public static void RefuseClaimWhileHeld(TaskAggregate task, string verb)
    {
        if (task.State == TaskState.Blocked && task.FeedbackStepHeld)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is held for its feedback step, so it cannot be {verb} yet: its blockers have all finished, and the "
                + "daemon is checking the notes they left against the task before it is released. The step ends in at most a "
                + $"refine-and-requeue or a park for a person; h9k task show {task.Id} says where it stands. To take the task out of "
                + $"the step, dequeue it: h9k task dequeue {task.Id}.");
        }
    }

    /// <summary>
    /// Refuses a claim made in the same breath as the queueing when that queueing would have held the task for its feedback
    /// step. <c>h9k task start</c> and <c>h9k task drive</c> on a Published task assign and claim in one append, so without this
    /// the task would skip the step that every other way of releasing it goes through. A queueing that still waits on a blocker
    /// is not at its release point yet, so the claim that acknowledges it is left alone.
    /// </summary>
    public static void RefuseClaimWhenQueueingWouldHold(TaskAssigned assigned, string verb, string command)
    {
        if (assigned.HoldForFeedbackStep && assigned.UnmetDependencies.Count == 0)
        {
            throw new DomainConflictException(
                $"Task {assigned.Id} takes the feedback step and its blockers have all finished, so it cannot be {verb} straight from "
                + "Published: it would be held at its release point while the daemon checks the notes they left against it. "
                + $"Queue it to let the step run (h9k task queue {assigned.Id}), or turn the step off for this task first "
                + $"(h9k task revise {assigned.Id} --feedback-step off) and run {command} again.");
        }
    }

    /// <summary>
    /// Refuses a change of the task's own override once it can no longer matter: held for the step, already released to Queued,
    /// or claimed (any state from Queued on), or its step has already begun. Allowed while Draft, Published, or Blocked and not held.
    /// </summary>
    public static void RefuseOverrideNow(TaskAggregate task)
    {
        string? why = task switch
        {
            { FeedbackStepHeld: true } =>
                "it is held for its feedback step right now, and a change would not reach a step already running",
            { FeedbackStepBegun: true } =>
                "its feedback step has already run, and nothing starts a second one",
            { State: var state } when state == TaskState.Draft || state == TaskState.Published || state == TaskState.Blocked => null,
            { State: var state } when state == TaskState.Queued =>
                "it has already been released to Queued, past the point where the step would have held it",
            _ => $"it is {task.State.Value}, past the point where the step would have held it, because it has been claimed",
        };

        if (why is not null)
        {
            throw new DomainConflictException(
                $"Task {task.Id}'s feedback step setting cannot change: {why}. It is settable while the task is Draft, Published, or "
                + "Blocked and not yet held for its feedback step.");
        }
    }
}
