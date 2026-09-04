using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The refusal every interactive-claim lever owes a pr-review task's sentinel claim. A pr-review
/// task's own Claimed+sentinel state is never a human's own interactive claim — <c>h9k task
/// work</c> and <c>h9k task start</c> both refuse to create one (Decisions Log #99) — it is
/// auto-pr-review's Now-speed deliberate claim (<c>AutoPrReviewEngine.CreateOneAsync</c>), which
/// reads identically to an attended one on <c>TaskAggregate.IsInteractiveClaim</c>'s own
/// <see cref="Guid.Empty"/> discriminator.
/// <para>
/// Shared rather than duplicated across <c>TaskReleaseCommand</c>, <c>TaskHandbackCommand</c> and
/// <c>TaskVerifyCommand</c> because each copy has now drifted twice in the same direction: cycle 7
/// widened Dispatched/Running to <see cref="RunState.IsLive"/> in two of them, and cycle 8 found
/// <see cref="RunState.ReviewParked"/> and <see cref="RunState.BudgetParked"/> — both reachable for
/// a sentinel pr-review run whose task stays Claimed (<c>PrReviewEngine</c> appends the first;
/// <c>RunSupervisor.AdoptableRunStates</c> lists both) — still falling through to a generic
/// "handed off with h9k task deliver" refusal that is false for a task type which is never
/// delivered at all. Composing the message from the run's own observed state here is what keeps
/// the three of them from disagreeing about it again (independent pre-PR review, cycle 8,
/// adversarial lens).
/// </para>
/// </summary>
internal static class PrReviewSentinelClaim
{
    /// <summary>
    /// The refusal for a pr-review task whose current run is in <paramref name="runState"/>,
    /// naming what the run actually is and the lever that actually moves it. <paramref name="verb"/>
    /// is the refusing command's own act ("release", "hand back", "verify"), so the sentence reads
    /// as that command's refusal rather than a generic one.
    /// </summary>
    public static DomainConflictException Refuse(Guid taskId, RunState runState, string verb)
    {
        (string situation, string nextStep) = runState switch
        {
            _ when runState.IsLive =>
                ("it is already running headlessly under the daemon's own supervision",
                    $"Let the run finish, or h9k task abandon {taskId}"),
            _ when runState == RunState.ReviewParked =>
                ("its headless review has parked for you with its findings report",
                    $"Walk the report and close it with h9k review resolve {taskId} --merge-ready"),
            _ when runState == RunState.BudgetParked =>
                ("its headless review has parked on an exhausted token budget",
                    $"h9k task abandon {taskId} if it should not continue"),
            // Never guessed at as one of the states above: a run record carrying no state at all
            // is an unread fact, said out loud, rather than a plausible-looking one filled in.
            _ when runState == RunState.Unknown =>
                ("its headless run's own state is not recorded",
                    $"h9k task abandon {taskId} if it should not continue"),
            _ =>
                ($"its headless run is {runState.Value}",
                    $"h9k task abandon {taskId} if it should not continue"),
        };

        return new DomainConflictException(
            $"Task {taskId} is a pr-review task dispatched by auto-pr-review's now speed — {situation}, "
            + $"not an interactive claim to {verb}. {nextStep} — h9k task show {taskId} to see where it stands.");
    }
}
