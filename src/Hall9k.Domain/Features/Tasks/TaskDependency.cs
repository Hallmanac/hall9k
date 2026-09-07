using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Extensions;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// One task as a dependency of another (Decisions Log #34). A dependency is met only at
/// <em>true closeout</em> — the closeout monitor observed the merge and appended RunCompleted
/// — so Draft, Published, Queued, Claimed/Running and AwaitingReview dependencies all block
/// by the same rule, and a Done task whose pull request is still open still blocks.
/// </summary>
/// <param name="ProjectId">
/// The project this dependency belongs to. Carried because one rule needs it: a stacked edge is
/// only honourable within a single project, since the child's worktree is cut from the parent's
/// branch in the child project's own repository, and a branch in another project's repository is
/// not there to cut from (independent pre-PR review, cycle 1, adversarial lens). A plain
/// blocked-by across projects is harmless by comparison — it never reads the blocker's branch —
/// which is why this is read for the stacked edge alone.
/// <para>
/// <see cref="Guid.Empty"/> means the snapshot's producer recorded no project, and every reader
/// treats that as unknown rather than as a project that differs (AGENTS.md's never-guess rule):
/// <c>TaskDecider.Publish</c> refuses a cross-project stacked edge only on an observed mismatch.
/// <c>TaskDependencyQuery</c> — the one production producer — always records it.
/// </para>
/// </param>
/// <param name="CurrentRunState">
/// The state of the run the dependency currently hangs on, or null when it has none: what
/// says whether a merge observation can still arrive. Null is the honest answer for "no run
/// to observe", never a stand-in for one (the AGENTS.md never-guess rule).
/// </param>
/// <param name="PullRequestUrl">
/// The pull request this dependency recorded, or null when it never opened one. Carried so a
/// dependent's advice can name h9k pr resolve only when that command would actually be
/// accepted: it reopens a done task onto its existing pull-request branch, and refuses a task
/// that has no pull request to follow up on.
/// </param>
/// <param name="Type">
/// The dependency's own task type. Carried so a dependent's advice can tell a pr-review
/// task's <c>PullRequestUrl</c> — the pull request it reviewed, never one this platform opened
/// or pushed to — apart from an ordinary task's own PR: <c>TaskDecider.Reopen</c> refuses a
/// pr-review task outright (AGENTS.md: it "never writes to the pull request or the remote in
/// any form"), so advice naming h9k pr resolve must never reach one (adversarial review,
/// cycle 7).
/// </param>
/// <param name="RunPullRequestNumber">
/// The pull request number <see cref="CurrentRunState"/>'s own run document carries, read from
/// <c>RunDetails.PullRequestNumber</c> — populated for every run since S1-03, unlike a mirror on
/// the lean <c>RunListItem</c> join would be, which would need its own backfill to agree with it
/// on every run recorded before such a mirror shipped (independent pre-PR review, cycle 1, both
/// lenses). Null on a run that never recorded one of its own, which <see
/// cref="CloseoutCanStillArrive"/> reads as honestly unwatched rather than guessing.
/// </param>
/// <param name="RunFailureReason">
/// <see cref="CurrentRunState"/>'s own run document's recorded failure reason, read from
/// <c>RunDetails.FailureReason</c> for the same reason <see cref="RunPullRequestNumber"/> is.
/// Carried so <see cref="CloseoutCanStillArrive"/> can exclude the one failure the closeout
/// orphan sweep itself excludes — a pull request the monitor already observed closed without
/// merging, where a repeat inspection would only relearn a fact already on the stream. The same
/// observation is the one thing that unmakes a <see cref="IsDelivered"/> reading, since a closed
/// pull request's URL stays recorded on the task forever.
/// </param>
public sealed record TaskDependency(
    Guid Id,
    string Objective,
    TaskState State,
    bool IsClosedOut,
    RunState? CurrentRunState,
    string? PullRequestUrl,
    TaskType Type,
    IReadOnlyList<Guid> BlockedBy,
    int? RunPullRequestNumber = null,
    string? RunFailureReason = null,
    Guid ProjectId = default)
{
    /// <summary>
    /// This blocker can no longer reach true closeout, so anything waiting behind it waits
    /// forever unless a human is told. There is more than one door out of the closeout
    /// pipeline: the task ended without a closeout (Failed, Abandoned), or it reads Done
    /// while the run that would have carried the merge observation has itself ended without
    /// one — h9k task resolve's attestation exit from Failed (log #27), a pull request closed
    /// unmerged, a killed or superseded run — or it reads Done with no run to observe at all.
    /// Origin incident (2026-08-20): the first cut of this rule enumerated Failed and
    /// Abandoned only, so a resolved dependency stranded its dependents in Blocked silently.
    /// </summary>
    public bool IsDead =>
        State == TaskState.Failed
        || State == TaskState.Abandoned
        || (State == TaskState.Done && !IsClosedOut && !CloseoutCanStillArrive);

    /// <summary>Whether this dependency still holds its dependents back.</summary>
    public bool Blocks => !IsClosedOut;

    /// <summary>
    /// Delivered in the display vocabulary (PLAN.md §16 #63): the task reached Done and has a pull
    /// request of its own open — pushed, reviewed internally, branch largely settled, waiting only
    /// on the merge. This is the bar a <em>stacked</em> dependent starts at rather than true
    /// closeout (task: a stacked pull-request edge exists as an explicit opt-in dependency, slice
    /// one), which is the whole point of the edge: the child builds on a branch that exists.
    /// <para>
    /// True closeout implies this and more, so <see cref="BlocksStackedChild"/> tests both rather
    /// than only this: a parent that merged and had its branch deleted is past Delivered, not short
    /// of it, and <see cref="IsClosedOut"/> is the only honest reading of that.
    /// </para>
    /// <para>
    /// A pull request the monitor observed CLOSED without merging is not one that is open, so it is
    /// not Delivered however long its URL stays recorded — and it stays forever:
    /// <c>CloseoutEngine.RecordClosedAsync</c> appends the closure to the RUN's stream and leaves
    /// the task Done carrying the same URL it always had. Origin (independent pre-PR review,
    /// 2026-09-07, adversarial lens): a parent whose pull request a human closed without merging
    /// read Delivered forever, which released a stacked child that had not dispatched yet — the
    /// child cut its branch from a dead parent, ran its whole pipeline, opened a pull request
    /// against a branch whose own pull request can never merge, and then sat off the merge bar
    /// promising a retarget that could never come.
    /// </para>
    /// </summary>
    public bool IsDelivered =>
        State == TaskState.Done && PullRequestUrl.IsNotBlank() && !PullRequestClosedUnmerged;

    /// <summary>
    /// The one observation that unmakes a Delivered reading: this dependency's own run records
    /// <see cref="RunDetails.PullRequestClosedWithoutMerge"/>, the fact the closeout monitor writes
    /// when it sees the pull request closed rather than merged — the same field, read for the same
    /// reason, that <see cref="StillWatchedByOrphanSweep"/> uses to know the sweep is done with it.
    /// Absent it, nothing is claimed: a run that recorded no failure reason is a pull request
    /// nobody observed closed, not one guessed open (AGENTS.md's never-guess rule).
    /// </summary>
    private bool PullRequestClosedUnmerged => RunFailureReason == RunDetails.PullRequestClosedWithoutMerge;

    /// <summary>
    /// Whether this dependency still holds back a dependent <em>stacked on</em> it. Delivered is
    /// enough: the parent's branch is pushed and its pull request open, which is everything a
    /// stacked child needs to cut its own branch from and target its own pull request at.
    /// </summary>
    public bool BlocksStackedChild => !IsClosedOut && !IsDelivered;

    /// <summary>
    /// Whether this dependency can no longer reach even <em>Delivered</em>, so a dependent stacked
    /// on it waits forever. Narrower than <see cref="IsDead"/> by exactly the arm a stacked edge
    /// does not care about: a Done blocker whose merge observation will never arrive has still
    /// delivered a branch and a pull request, so a child stacked on it is released rather than held
    /// — reporting that as a dead blocker would park the child for a hold that never applied to it.
    /// Failed and Abandoned are dead for either kind of edge.
    /// <para>
    /// The third arm is the mirror of the first two, and it is the reason this is not simply
    /// Failed-or-Abandoned: a Done blocker that <see cref="BlocksStackedChild"/> still holds back is
    /// one that reached the end of its own lifecycle without ever passing either bar — a pull
    /// request observed closed without merging (<see cref="IsDelivered"/>'s own origin incident), or
    /// a task with no pull request at all, which is <c>h9k task resolve</c>'s attestation exit from
    /// Failed (Decisions Log #27). Nothing further arrives on its own from Done, so the stacked
    /// child behind it waits forever unless a human is told — the identical stranding
    /// <see cref="IsDead"/>'s own origin incident (2026-08-20) records for a plain edge. A child
    /// that already dispatched is unaffected either way: its edge left the unmet set at release, and
    /// <c>TaskDependencyResolver</c> only ever re-asks about dependencies still on it.
    /// </para>
    /// </summary>
    public bool IsDeadForStackedChild =>
        State == TaskState.Failed
        || State == TaskState.Abandoned
        || (State == TaskState.Done && BlocksStackedChild);

    /// <summary>
    /// A Done task closes out when the closeout monitor observes its pull request merge. Most of
    /// the time that only happens while the run carrying the pull request is still in the
    /// pipeline — no run, or a run already terminal on some ending other than Completed, means
    /// there is nobody left to observe anything. But <c>CloseoutEngine</c>'s own orphan sweep
    /// (Decisions Log #72) keeps watching a Failed or Killed run that still carries a pull
    /// request number nothing has recorded as closed without merging, and completes closeout
    /// against it unaided — so a run terminal on Failed or Killed is not automatically dead the
    /// way Superseded or an absent run is. <see cref="StillWatchedByOrphanSweep"/> is this
    /// predicate's mirror of <c>AttentionComposer.IsOrphanSweepCandidate</c>'s first arm (the two
    /// must never disagree about which pull requests the sweep is still going to find), narrowed
    /// to data this record actually carries: the sweep's other arm, the missing-run shape that
    /// resolves a task-recorded pull-request URL nobody's own run document ever parsed a number
    /// onto, needs <c>PullRequestUrls.ParseNumber</c> from Hall9k.Connectors, which the domain
    /// layer never references (AGENTS.md's reference graph) — a run whose own document never
    /// recorded a number still reads dead here, which is the pre-existing, narrower gap that
    /// shape was always going to leave (independent pre-PR review, cycle 3: a resolved blocker's
    /// pull request the orphan sweep is actively watching read dead to its dependents).
    /// </summary>
    private bool CloseoutCanStillArrive =>
        CurrentRunState is { } run && (!run.IsTerminal || StillWatchedByOrphanSweep(run));

    /// <summary>
    /// Mirrors <c>CloseoutEngine.PollOnceAsync</c>'s own orphan-candidate query and
    /// <c>AttentionComposer.IsOrphanSweepCandidate</c>'s first arm: only Failed or Killed is ever
    /// swept (Superseded is not), only a run that recorded a pull request number of its own is a
    /// candidate, and a run the monitor already observed closed without merging is excluded — that
    /// run already told the sweep everything an inspection could, so it never re-enters the
    /// candidate set.
    /// </summary>
    private bool StillWatchedByOrphanSweep(RunState run) =>
        (run == RunState.Failed || run == RunState.Killed)
        && RunPullRequestNumber is > 0
        && RunFailureReason != RunDetails.PullRequestClosedWithoutMerge;

    /// <summary>The id fragment h9k accepts on the command line, as every surface prints it.</summary>
    private string ShortId => Id.ToString("N")[^8..];

    /// <summary>How the dependency reads in an error a human has to act on.</summary>
    public string Describe() => $"{ShortId} \"{Objective}\" ({State.Value})";

    /// <summary>
    /// Why this blocker will never close out and what moves it, as the first half of the
    /// sentence a human reads on the parked dependent. Only meaningful when
    /// <see cref="IsDead"/> — the caller has already decided that.
    /// </summary>
    public string DescribeDeath() =>
        // Failed still has exits (retry, resolve); Abandoned is a dead end by design, so
        // its only honest remedy is on the dependent's side — never advise a lever the
        // decider will refuse (review finding, 2026-08-20). Each branch carries its own
        // remediation so the caller's trailing mechanics compose grammatically.
        State == TaskState.Failed
            ? $"Dependency {Describe()} ended there and will never close out on its own. "
              + "Retry or resolve it, or revise this task's dependencies"
            : State == TaskState.Abandoned
                ? $"Dependency {Describe()} was abandoned and will never close out on its own; "
                  + "abandoned is a dead end by design, so revise this task's dependencies"
                : $"Dependency {Describe()} reads Done, but {DescribeAbsentMerge()}, so the merge observation "
                  + $"true closeout waits on will never arrive. {DescribeDoneRemedy()}";

    private string DescribeAbsentMerge() =>
        CurrentRunState is null
            ? "it has no run left to carry a pull request"
            : $"its run ended {CurrentRunState.Value} rather than Completed";

    /// <summary>
    /// What actually moves a Done-but-never-closing-out blocker. Merging its pull request by
    /// hand does nothing here: the closeout monitor only inspects runs still in the watch set
    /// (AwaitingReview, ReviewPending, CloseoutParked), and this blocker is dead precisely
    /// because its run left it — so a merge made on that advice is never observed and the hold
    /// never lifts. h9k pr resolve dispatches a follow-up run onto the existing pull-request
    /// branch, which puts the pull request back under watch, and that run's merge is observed.
    /// Named only when the decider would accept it: a reopen needs a recorded run to follow up
    /// on and a pull request to follow up about (review finding, 2026-08-21), and never for a
    /// pr-review task, whose PullRequestUrl names the pull request it reviewed rather than one
    /// of its own to reopen — TaskDecider.Reopen refuses it outright (adversarial review, cycle 7).
    /// </summary>
    private string DescribeDoneRemedy() => (CurrentRunState, PullRequestUrl.IsBlank(), Type == TaskType.PrReview) switch
    {
        (null, _, _) => "Nothing is left to put back under watch, so the only lever is on this side: "
                     + "revise this task's dependencies",
        (_, true, _) => "It has no pull request to put back under watch, so the only lever is on this "
                     + "side: revise this task's dependencies",
        (_, _, true) => "It is a pull-request review with no pull request of its own to put back under "
                     + "watch, so the only lever is on this side: revise this task's dependencies",
        _ => $"Put it back under watch with h9k pr resolve {ShortId} — the follow-up run that "
             + "dispatches rejoins the closeout monitor's watch set, and its merge is observed — "
             + "or revise this task's dependencies",
    };
}
