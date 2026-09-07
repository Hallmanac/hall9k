using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Daemon.Review;

/// <summary>
/// What a stacked child's checkpoint does about what it observed — an unpersisted in-process
/// outcome, so an enum is right here (TASK-MODEL.md §8).
/// </summary>
public enum StackedCheckpointAction
{
    /// <summary>Nothing is owed: carry on with whatever was about to run.</summary>
    Proceed,

    /// <summary>Replay this branch's own commits from the recorded fork point onto the parent's observed head, then run the gates.</summary>
    Replay,

    /// <summary>Stop and park for a human — the situation named in <see cref="StackedCheckpointVerdict.Reason"/>.</summary>
    Park,
}

/// <summary>
/// One checkpoint's decision, decided over nothing but the observation and the budget — no git
/// call, no append — so it can be asserted directly and read in one place.
/// </summary>
/// <param name="UpstreamCommit">The replay's <c>&lt;upstream&gt;</c>, blank unless <see cref="Action"/> is <see cref="StackedCheckpointAction.Replay"/>.</param>
/// <param name="OntoCommit">The commit the replay lands on, blank alongside <paramref name="UpstreamCommit"/>.</param>
/// <param name="Reason">
/// The park's own message where this parks, and the log line's account otherwise. Composed here
/// rather than at the call site so the wording and the decision that earns it cannot drift apart.
/// </param>
public sealed record StackedCheckpointVerdict(
    StackedCheckpointAction Action,
    string UpstreamCommit,
    string OntoCommit,
    string Reason);

/// <summary>
/// The rule a stacked child's rebase checkpoint follows, as a pure function of what
/// <see cref="StackedParentWatch"/> observed and how much of the child's rebase budget is already
/// spent (task: a stacked child absorbs its parent's post-delivery churn safely).
/// <para>
/// Split out of <see cref="ReviewEngine"/> deliberately. Both of the child's checkpoints — before
/// its first review cycle, and before the mandatory final full pass — ask the identical question of
/// the identical rule, so it lives once; and the interesting half of this feature is which
/// observations park rather than proceed, which is exactly the half that needs no repository, no
/// store and no agent session to assert.
/// </para>
/// <para>
/// Where it parts company with closeout's own reading of the same observation
/// (<c>CloseoutEngine.TryReplayStackedChildAsync</c>) is the unresolvable-parent case, and the
/// difference is structural rather than a matter of taste: closeout is a sweep looking at an
/// already-open pull request, so "ask again next sweep" is always available to it, while a
/// checkpoint is the last look before the thing it precedes runs. A checkpoint that shrugged would
/// hand its reviewers — or the remote — a branch built on a base nobody could account for.
/// </para>
/// </summary>
public static class StackedCheckpointPolicy
{
    /// <summary>
    /// What every park that ends in "this branch belongs on a different base" has to say, said
    /// once here rather than three times below. A human rebasing the branch by hand does not
    /// change what the next checkpoint observes: the base a run watches is the one frozen on its
    /// own dispatch (<c>RunDispatched.BaseBranch</c>), and a claimed task's stacked edge cannot be
    /// revised at all (<c>TaskDecider.Revise</c> is Draft-only, and the domain's own refusal there
    /// is the sentence quoted at the end of this one), so the same observation returns and parks
    /// again. Said because the alternative is a park that reads as though a resolve carried the run
    /// onward — the identical defect the budget park below carried (independent pre-PR review,
    /// cycle 1, adversarial lens), found on these three by that finding's own class sweep.
    /// </summary>
    private const string TheBaseThisRunWatchesIsFrozen =
        " What no resolve can do is move this run onto another base: the one it watches was frozen when it was "
        + "dispatched, and a claimed task's stacked edge cannot be revised — so a rebase by hand leaves this "
        + "same checkpoint parking on this same account. Work that belongs on another base continues as a fresh "
        + "task rather than in this run (a task that has already run gets a new task, not a rewritten contract).";

    public static StackedCheckpointVerdict Decide(
        StackedParentObservation observation,
        StackedCheckpoint checkpoint,
        int rebasesSpent,
        int rebaseBudget) => observation.Verdict switch
    {
        // Still built on the parent's current head: the checkpoint's whole purpose is already
        // satisfied, and nothing is spent for finding that out.
        StackedParentVerdict.Aligned => Proceed(observation.Detail),

        // A read that failed is not a fact (AGENTS.md's never-guess rule), and it is not this
        // run's fault either — the identical stance the unstacked half of this gate takes on a
        // failed fetch. Proceeding is safe in the same way it is there: once this branch is
        // pushed, closeout's own sweep observes the parent and dispatches the replay, so residual
        // staleness has a second reader. Parking here instead would park a run for a transient.
        StackedParentVerdict.Unobservable => Proceed(observation.Detail),

        // No parent head exists to be brought onto, and no later sweep finds one this look missed.
        // Parked rather than proceeded precisely because it is stable: the child is building on a
        // branch nobody can point at, and the honest thing is to say so before its reviewers or the
        // remote read the result. Stable is not permanent, though — a parent still in flight that
        // simply has not pushed its branch yet produces this too (a child claimed ahead with
        // --acknowledge-unmet-dependencies), and for that shape the cheapest path is to wait rather
        // than unstack, so the park names it first (independent pre-PR review, cycle 1, adversarial
        // lens: this message asserted permanence and steered every reader toward unstacking).
        StackedParentVerdict.ParentUnresolvable => Park(
            $"This branch is stacked on {observation.ParentBranch} and {observation.Detail}. The rebase owed "
            + $"before {checkpoint.Describe()} therefore has no base to run onto. If the parent is still in "
            + $"flight and simply has not pushed {observation.ParentBranch} yet — this task was claimed ahead of "
            + "it with --acknowledge-unmet-dependencies — the cheapest path is to wait for that push and then "
            + "resolve with h9k review resolve. If that branch is never coming, abandon this task instead."
            + TheBaseThisRunWatchesIsFrozen),

        // The parent is not coming back on its own, so following it is not a plan. Which door it
        // took is in the detail, and that decides whether it can be put back on its feet at all.
        StackedParentVerdict.ParentDead => Park(
            $"This branch is stacked on {observation.ParentBranch} and {observation.Detail}. Rather than take "
            + $"the rebase owed before {checkpoint.Describe()} onto a base with no future, this run parks. If "
            + "that parent should live after all, put it back on its feet first — h9k task retry for one that "
            + "ended Failed, h9k pr resolve for one whose pull request closed unmerged, nothing for an abandoned "
            + "one — and then resolve this run with h9k review resolve: a parent that reaches Delivered again is "
            + "a base this checkpoint can follow. If it is truly finished, abandon this task with it."
            + TheBaseThisRunWatchesIsFrozen),

        // The same refusal closeout makes, for the same reason: ordering a stack three levels deep
        // is out of this slice, and the base one level up is a human's call rather than a
        // mechanical one.
        StackedParentVerdict.ParentMergedElsewhere => Park(
            $"This branch is stacked on {observation.ParentBranch} and {observation.Detail}. The rebase owed "
            + $"before {checkpoint.Describe()} cannot pick that base mechanically without dropping work, so "
            + "ordering this stack is yours: land it by hand from here — onto the branch its parent merged into, "
            + "or onto the project's base once that branch's own pull request has merged."
            + TheBaseThisRunWatchesIsFrozen),

        // ParentMerged and ParentMoved are one operation with a different --onto, exactly as they
        // are for closeout's replay: the parent's commits are dropped at the recorded fork point
        // and this branch's own are replayed onto the observed head. A merged parent's onto is the
        // project's base tip, and this run's pull request — if it has one yet — is retargeted by
        // closeout's own sweep later, not from here.
        StackedParentVerdict.ParentMerged or StackedParentVerdict.ParentMoved =>
            rebasesSpent >= rebaseBudget
                // Which levers this names is load-bearing, and it is deliberately NOT closeout's
                // own "grant another attempt with h9k pr resolve" (independent pre-PR review,
                // cycle 1, adversarial lens: that wording was copied here, where it grants
                // nothing). This budget is a count on the TASK's stream
                // (TaskAggregate.StackReplaysDispatched), and h9k review resolve appends to the
                // RUN's — so resolving re-enters the loop, meets this same checkpoint over the same
                // spent budget, and parks again. What actually advances the run is the rebase
                // itself: performed by hand, or by a fix session the human hands it to.
                ? Park(
                    $"This branch is stacked on {observation.ParentBranch} and {observation.Detail}. Its rebase "
                    + $"budget is spent ({rebasesSpent}/{rebaseBudget} rebase(s)) — the parent branch has kept "
                    + "moving faster than this branch can follow it. Bring this branch onto its parent's head by "
                    + $"hand (`git rebase --onto {observation.OntoCommit} {observation.BoundaryCommit}`) and then "
                    + "resolve with h9k review resolve, or hand that rebase to a fix session with "
                    + "h9k review resolve --needs-fixes \"<guidance>\". Resolving on its own grants no further "
                    + "mechanical attempt: this budget counts the rebases already spent on this task, and only a "
                    + "fresh pull request or a manual h9k pr resolve reopen resets it — so a checkpoint that finds "
                    + "the parent still moved parks again.")
                : new StackedCheckpointVerdict(
                    StackedCheckpointAction.Replay, observation.BoundaryCommit, observation.OntoCommit,
                    observation.Detail),

        // No default arm on purpose: a verdict added later has to be routed deliberately rather
        // than inheriting whichever behaviour happened to be the fallback (the identical argument
        // CloseoutEngine's own early returns over this enum make). Reached only by a verdict this
        // switch has never been told about, so it proceeds without claiming anything — the one
        // choice that neither parks a run over an unread enum nor rewrites a branch on one.
        _ => Proceed(
            $"this checkpoint has no rule for the verdict {observation.Verdict} — proceeding without a rebase "
            + $"rather than acting on an observation it cannot read ({observation.Detail})"),
    };

    private static StackedCheckpointVerdict Proceed(string reason) =>
        new(StackedCheckpointAction.Proceed, string.Empty, string.Empty, reason);

    private static StackedCheckpointVerdict Park(string reason) =>
        new(StackedCheckpointAction.Park, string.Empty, string.Empty, reason);
}
