using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// When <c>h9k task skip-to-pr</c> may be used, as a rule rather than a block of CLI code, so every
/// refusal is a unit test and every refusal says what to do instead (idea 6e6d0200, Card 3).
/// </summary>
public static class ReviewSkipRules
{
    /// <summary>
    /// The door this run is at, or a refusal that names the lever that fits where the run stands.
    /// Refusals are <see cref="DomainConflictException"/>s so the CLI maps them to its conflict exit.
    /// </summary>
    public static ReviewSkipEntry Admit(TaskDetails task, RunAggregate run)
    {
        Guid taskId = task.Id;
        if (task.Type == TaskType.PrReview)
        {
            throw new DomainConflictException(
                $"Task {taskId} is a pr-review task: it opens no pull request of its own, so there is nothing "
                + $"to skip to. h9k task show {taskId} to see where it stands.");
        }

        if (task.Type == TaskType.Spike)
        {
            throw new DomainConflictException(
                $"Task {taskId} is a spike: it answers a question and opens no pull request of its own, so "
                + $"there is nothing to skip to. h9k task show {taskId} to see where it stands.");
        }

        if (!task.InteractiveModeEnabled)
        {
            throw new DomainConflictException(
                $"Task {taskId} is not in interactive mode, and skipping the review pipeline is only for a task "
                + "you are driving by hand. A task the daemon drives runs its full pipeline.");
        }

        if (run.State == RunState.Dispatched || run.State == RunState.Running)
        {
            return ReviewSkipEntry.FromBuild;
        }

        if (run.State == RunState.ReviewParked)
        {
            // A lap that answered a human reviewer parked with a drafted reply nobody has decided on.
            // Skipping would erase the draft and leave the reviewer unanswered without anyone choosing
            // that, so the reply choice is made first, through the lever that records it.
            if (run.ParkedOnReviewDisagreement)
            {
                throw new DomainConflictException(
                    $"Task {taskId} is parked because a lap answered a human reviewer and posted nothing; the "
                    + "drafted reply is yours to decide, and skipping would drop it without anyone choosing that. "
                    + $"Say what the reviewer hears first: h9k review resolve {taskId} with --post-reply-as-written, "
                    + "--post-reply \"<your text>\", or --post-nothing. h9k task show prints the draft whole.");
            }

            // Review has already settled at this park: only opening the pull request is left, so a
            // skip would bypass nothing and h9k review proceed is the honest lever.
            return run.ParkedFromReviewPhase == ReviewPhase.MergeReady
                ? throw new DomainConflictException(
                    $"Task {taskId} is parked with review already settled; only opening the pull request is left, "
                    + $"so there is nothing to skip. h9k review proceed {taskId} opens it.")
                : ReviewSkipEntry.FromPark;
        }

        if (run.State == RunState.Verifying || run.State == RunState.UnderReview)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s gates or a review session are running right now. h9k task skip-to-pr is offered "
                + $"at the next park, not while the pipeline is mid-step; h9k task show {taskId} says when that "
                + "is.");
        }

        throw new DomainConflictException(
            $"Task {taskId}'s run is {run.State.Value}, so there is no build or review park to skip from. "
            + $"h9k task show {taskId} to see where it stands.");
    }
}
