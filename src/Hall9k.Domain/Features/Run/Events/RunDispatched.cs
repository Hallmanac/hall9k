using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// OwnerId is the node's owner AT dispatch time — frozen so the accountability chain
/// (PLAN.md §6.2) survives any future node ownership change.
/// Model is the resolved model this run's build session was spawned on, recorded as an
/// observed fact (Decisions Log #33), appended with a default so streams written before
/// the chain existed replay as Unknown rather than as a reconstruction (the log #30
/// discipline: the unobserved is admitted, never guessed).
/// RunDirectory is resolved once here, exactly as WorktreePath is: where the run's prompt,
/// stream, verify logs and review files live, under the owning task's directory when the
/// project has a home and the platform-global location otherwise (ruled 2026-08-23, backlog
/// 49). This is the dispatch-time record, not a live pointer: a task's directory can move
/// under <c>tasks/_archive/</c> and back after dispatch (backlog 51, PLAN.md §16 #84), so every
/// consumer resolves the run's current location through
/// <see cref="Hall9k.Domain.Infrastructure.Storage.RunPaths.ResolveCurrentDirectory"/> rather
/// than trusting this value verbatim; old runs (an empty string, meaning "before this field
/// existed") and new ones resolve through <c>RunPaths.GlobalDirectory</c> and the recorded path
/// respectively, with no special case at the read site.
/// PrReviewBaseRefName is the pull request's base branch as RunLauncher read it at dispatch, for
/// a pr-review task only (cycle-3 conformance finding): the adversarial lens's diff and the
/// conformance lens's own re-diff must compare against the identical base, and a second live
/// `gh pr view` minutes later can disagree with the first — the base moved, or the read itself
/// failed — with nothing on the stream to say which base either lens actually used. Recording it
/// once here is what lets the conformance lens reuse this run's own dispatch-time read instead of
/// taking a second one. Null for a pr-review run dispatched before this field existed, or for any
/// non-pr-review run, which never carries one.
/// SessionName is the exact name the primary session's Claude Code process launches under
/// (task: every dispatched agent session launches under a human-readable id-and-role name,
/// see <see cref="Hall9k.Domain.Features.Run.SessionRoleName"/>) - "build" for an ordinary
/// dispatch or a plain follow-up, "rebase" or "checks" for those follow-up kinds, or the
/// pr-review task type's own "review-adversarial-1" (its primary session IS the adversarial
/// lens). Recorded here, at the one place that knows which of those this dispatch actually is,
/// rather than reconstructed later from <see cref="AgentRole.Build"/> alone, which cannot tell
/// the three apart. Blank on a stream written before this field existed.
/// ReviewStageComposition is which pre-PR review stages this run gets, resolved once here via
/// <see cref="ReviewStageCompositionResolver"/> (task: the review pipeline's stage composition
/// becomes configuration recorded per run) and frozen for the run's whole lifetime — see
/// <see cref="ReviewStageComposition"/>'s own doc for why this is resolved at dispatch rather
/// than re-checked live the way the review-cycle caps are. Null replays as
/// <see cref="ReviewStageComposition.FullPipeline"/> — the shape every run had before this
/// setting existed.
/// OpeningReviewSinceSha is the seed for this run's own opening Discovery cycle's diff
/// instruction (task: a lap reviews only what it changed) — the pull request head closeout
/// observed when it reopened this run's task, carried forward from
/// <see cref="Hall9k.Domain.Features.Tasks.TaskAggregate.FollowUpPullRequestHeadSha"/> at dispatch
/// time. Null for a fresh run, a Rebase follow-up (excluded, unchanged), a manual
/// h9k pr resolve reopen (no live pull-request inspection there to observe a head from), and any
/// stream written before this field existed — Discovery reads the full base-branch diff in every
/// one of those cases, exactly as it always has.
/// DispatchingNodeId is the physical daemon process that actually spawned this run, distinct
/// from <see cref="NodeId"/> whenever the latter carries the ceiling-exempt <see cref="Guid.Empty"/>
/// sentinel (independent pre-PR review, cycle 1, conformance lens): an ordinary dispatch's
/// NodeId already IS the dispatching node, but auto-pr-review's "now" speed launches a
/// <c>PrReview</c> task under the sentinel from whichever daemon's own sweep found the
/// candidate, and <see cref="Hall9k.Daemon.Execution.RunSupervisor"/>'s own sentinel-run
/// adoption needs to tell that daemon apart from any other node sharing the same database —
/// otherwise a restarting node could adopt, and fail, a sentinel run a different node's daemon
/// still has alive. Guid.Empty only on a stream written before this field existed; every dispatch
/// since carries the real dispatching node — equal to NodeId itself for an ordinary (non-sentinel)
/// dispatch, since that is the one node making the call there too.
/// BaseBranch is the branch THIS run's work sits on top of: the branch its worktree was cut from,
/// the branch its diff and review packet are computed against, and the branch its pull request
/// targets. Empty means "the project's own base branch", which is every ordinary run and every
/// stream written before this field existed — so nothing about the unstacked case changes, and
/// nothing here needs a backfill. It differs only for a stacked child (task: a stacked pull-request
/// edge exists as an explicit opt-in dependency), where it names the parent's branch until the
/// parent merges and the child is retargeted. Resolved once, here, exactly as WorktreePath and
/// Model are, so the branch the worktree was cut from, the range the reviewers read, and the base
/// `gh pr create` is given can never disagree — and frozen, so a parent branch that moves after
/// dispatch produces a retarget or a replay rather than silently reinterpreting a run in flight.
/// BaseCommit is <see cref="BaseBranch"/> resolved to a commit — this branch's fork point, observed
/// at the moment it was true: the start point a fresh cut resolved
/// (<c>Worktree.StartPointCommit</c>), carried forward unchanged by a follow-up that resumes the
/// branch (resuming does not move a fork point), and replaced by the commit a stacked replay is
/// dispatched to land on. It is a dispatch-time record of a fact that can move afterwards, and the
/// one thing that moves it is a rebase: <see cref="RunRebasedOntoBase"/>'s own apply advances the
/// aggregate's and the projection's copy to the commit the branch was actually rebased onto, so a
/// later replay never reads an upstream the branch no longer contains. It exists because a ref
/// cannot recover this: a force-pushed parent
/// rewrites the history the child shares with it, so <c>git merge-base</c> collapses to the base
/// branch and a replay from there re-applies the parent's old commit against its new one. Empty
/// when nothing was observed — a resumed branch whose predecessor recorded none, an unreadable
/// rev-parse, or a stream written before this field — and the replay refuses to dispatch on an
/// empty one rather than inventing a boundary (AGENTS.md's never-guess rule).
/// </summary>
public sealed record RunDispatched(
    Guid Id,
    Guid TaskId,
    Guid NodeId,
    Guid OwnerId,
    int LeaseGeneration,
    Guid SessionId,
    string WorktreePath,
    string Branch,
    ExecutorMode ExecutorMode,
    DateTimeOffset DispatchedAt,
    bool IsFollowUp = false,
    AgentModel? Model = null,
    string RunDirectory = "",
    string? PrReviewBaseRefName = null,
    string SessionName = "",
    ReviewStageComposition? ReviewStageComposition = null,
    Guid DispatchingNodeId = default,
    string? OpeningReviewSinceSha = null,
    string BaseBranch = "",
    string BaseCommit = "");
