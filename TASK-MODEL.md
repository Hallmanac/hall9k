# Hall9k v0 — Domain Model Draft (Task 2 output)

Drafted 2026-08-16. House style: static decider pattern, sealed-record events one per file,
vertical slices, inline `SingleStreamProjection` only, UUIDNext v7 IDs, Marten 8.x +
WolverineFx 5.x, value objects over primitives and enums (§8).

Folder layout mirrors NTS: `Hall9k.Domain/Features/{Feature}/` with the aggregate at the slice
root. The Task slice's folder/namespace is `Tasks` (plural) — a namespace segment named
`Task` shadows `System.Threading.Tasks.Task` inside it. **Subfolders (`Commands/`, `Events/`, `Handlers/`, `Queries/`, `Projections/`, `Documents/`)
are used only where a slice is big enough to want them** — Task, Run, Project. Tiny slices
(Owner, Node, Connection) stay flat: aggregate, event(s), and projection as sibling files, no
subfolders until growth demands them. (Subfolders are interior organization of a slice, not part
of the vertical-slice pattern itself.)

---

## 1. Stream map

| Stream (aggregate) | Lifespan | Owns |
|---|---|---|
| **Idea** | hours–months | one thought, from capture through discovery to what it became (§10) |
| **Task** | days–weeks | the work's story: readiness contract, claims/leases, conversation, terminal outcome |
| **Run** | minutes–hours | one dispatch attempt: process, session, verification, PR, tokens |
| **Owner** | permanent | the human (§6.2 accountability root) |
| **Node** | permanent | one machine's identity |
| **Project** | permanent | repo binding + verify commands + agent policy |
| **Connection** | permanent | provider credential indirection (§10) |
| **Epic** | weeks–months | a named grouping of tasks: title, Jira link, Open/Closed state (log #100) |

Task ↔ Run linkage: `RunDispatched` carries `TaskId`; the Task stream never records run
internals. Reads join the two projections by `TaskId` (see §5 — no multi-stream projection,
per house style).

## 2. Task slice

### State split (proposal)

The §3.3 lifecycle mixes two concerns. Here they separate cleanly:

- **Task state** (work lifecycle): `Draft → Published → Queued | Blocked → Claimed → NeedsHuman ⇄ Claimed → Done | Abandoned`,
  with the failure path `Claimed → Failed → Queued | Done | Abandoned`
  (+ `NeedsRefinement` reserved, not built in v0). The first three states separate task
  *development* from task *dispatch* (log #34, §2.3 below): `Draft` is being developed and is
  invisible to the dispatcher, `Published` has passed the readiness gate and is assignable but
  not claimable, and only an explicit human assignment produces `Queued` (dependencies all
  closed out) or `Blocked` (some have not). Only `Done` and `Abandoned` are terminal:
  terminal states say how the story ended, and "ended in failure" is only true when a human
  walks away, which is what `Abandoned` means. `Failed` is a needs-human waypoint (log #27)
  with three human-only exits: `Failed → Queued` via `TaskRetried` (re-run, log #25),
  `Failed → Done` via `TaskResolved` (the objective was met despite the run failure, log #27),
  and `Failed → Abandoned` via `TaskAbandoned` (walk away). `Done` keeps one deliberate exit,
  `Done → Queued` via `TaskReopened` (PR follow-up runs, log #20 and §2.1 below); Abandoned
  stays a dead end.
- **Run state** (execution lifecycle): `Dispatched → Running → Verifying → UnderReview → AwaitingReview → Completed | Failed | Killed | Superseded`
  (`AgentSessionCompleted` enters Verifying — the agent process finishing is not the run finishing.
  `UnderReview` is the pre-PR review loop, with `ReviewParked` when it hands the diff to a
  human (§3.1). During PR closeout, AwaitingReview refines further: `ChecksFailing`,
  `ReviewPending`, `Conflicting`, and `CloseoutParked` record what the closeout monitor observed
  (see §2.2). `Completed` arrives only when the monitor observes the merge.)

Neither vocabulary is what a human reads. The display is three separate surfaces composed from
these two (log #66, §2.4): a **lifecycle state** in seven words, a **phase line** for whatever is
live, and an **attention** column saying whether a human is wanted and why.

### Events (`Features/Task/Events/`, one record per file)

```csharp
public sealed record TaskAdded(
    Guid Id,
    Guid ProjectId,
    string Objective,
    IReadOnlyList<string> AcceptanceCriteria,
    TaskType Type,
    string? AgentContext,
    TaskConstraints? Constraints,        // null = no budget, nothing auto-killed (log #11)
    ExternalReference? ExternalReference, // set when adopted via --from-issue (resolves open decision #9 for v0)
    DateTimeOffset AddedAt,
    Guid AddedByOwnerId,
    AgentModel? Model = null,            // the task's model override, most specific link in the log #33 chain
    IReadOnlyList<Guid>? BlockedBy = null, // dependency edges declared at creation (§2.3)
    bool StartsAsDraft = false);         // false is the PRE-SPLIT meaning, so a stream written before log #34
                                         // replays as it behaved: Queued on arrival, assigned to AddedByOwnerId
                                         // (the sole owner of a v0 install). Everything h9k creates now passes true.

// The lifecycle split (log #34, §2.3). Each edge is an explicit act with its own event:
public sealed record TaskPublished(     // Draft -> Published: the readiness gate passed
    Guid Id,
    DateTimeOffset PublishedAt,
    Guid PublishedByOwnerId);

public sealed record TaskRevised(       // Draft-only. Optional<T> carries "left alone" vs "set to this",
    Guid Id,                            // exactly as ProjectSettingsChanged does
    Optional<string> Objective,
    Optional<IReadOnlyList<string>> AcceptanceCriteria,
    Optional<string> AgentContext,
    Optional<IReadOnlyList<Guid>> BlockedBy,
    Optional<TaskType> Type,
    Optional<AgentModel> Model,
    DateTimeOffset RevisedAt,
    Guid RevisedByOwnerId);

public sealed record TaskReturnedToDraft( // Published -> Draft: the explicit revert (refused once assigned)
    Guid Id,
    string? Reason,
    DateTimeOffset ReturnedAt,
    Guid ReturnedByOwnerId);

public sealed record TaskAssigned(      // Published -> Queued (or Blocked): the dispatch trigger, always human
    Guid Id,
    Guid AssignedOwnerId,               // the claim guard reads this: a node claims only its owner's work
    IReadOnlyList<Guid> UnmetDependencies, // empty => Queued; otherwise Blocked until each closes out
    DateTimeOffset AssignedAt,
    Guid AssignedByOwnerId);

public sealed record TaskUnassigned(    // Queued/Blocked -> Published; refused while a lease is held
    Guid Id,
    string? Reason,
    DateTimeOffset UnassignedAt,
    Guid UnassignedByOwnerId);

public sealed record TaskDependencyCompleted( // a blocker reached TRUE closeout (RunCompleted, §2.2)
    Guid Id,
    Guid DependencyId,
    IReadOnlyList<Guid> RemainingDependencies, // empty => Blocked -> Queued
    DateTimeOffset CompletedAt);

public sealed record TaskDependencyFailed(    // a blocker can no longer close out (§2.3): the dependent holds.
    Guid Id,                                  // The dependent STAYS Blocked and reads as NeedsHuman (§2.3)
    Guid DependencyId,
    string Reason,
    DateTimeOffset ObservedAt);

public sealed record TaskDependencyRecovered( // that blocker is back in the pipeline (§2.3, log #61):
    Guid Id,                                  // the hold lifts, the dependent waits ordinarily again
    Guid DependencyId,
    string Observation,                       // this blocker only; what still holds is derived on apply
    DateTimeOffset ObservedAt);

public sealed record TaskClaimed(
    Guid Id,
    Guid NodeId,
    Guid OwnerId,
    int LeaseGeneration,                 // fencing token (log #7)
    Guid RunId,                          // minted by the daemon BEFORE claiming; RunDispatched uses the same id.
                                         // Gives the Task stream its run linkage with no extra events, and makes
                                         // worktree naming (wt-<task>-<run>) deterministic pre-spawn.
    DateTimeOffset ClaimedAt);

public sealed record TaskRequeued(
    Guid Id,
    RequeueReason Reason,                // LeaseExpired | RunFailedRetryable | HumanRequested
    DateTimeOffset RequeuedAt);

public sealed record QuestionAsked(
    Guid Id,
    Guid QuestionId,
    Guid RunId,
    string Question,
    DateTimeOffset AskedAt);

public sealed record AnswerProvided(
    Guid Id,
    Guid QuestionId,
    string Answer,
    DateTimeOffset AnsweredAt,
    Guid AnsweredByOwnerId);

public sealed record TaskCompleted(Guid Id, Guid RunId, string? PullRequestUrl, DateTimeOffset CompletedAt);
public sealed record TaskFailed(Guid Id, Guid RunId, string Reason, DateTimeOffset FailedAt);
public sealed record TaskAbandoned(Guid Id, string? Reason, DateTimeOffset AbandonedAt, Guid AbandonedByOwnerId);

public sealed record TaskReopened(       // Done -> Queued for a follow-up run on the existing PR
    Guid Id,                             // branch (PR closeout, log #18/#20). See §2.1.
    Guid PreviousRunId,                  // the run whose branch is resumed
    string Branch,                       // from that run's record — lives nowhere else on this stream;
                                         // the PR URL already does (TaskCompleted) and isn't repeated
    string? Reason,
    DateTimeOffset ReopenedAt,
    Guid ReopenedByOwnerId,
    FollowUpKind? Kind = null,           // ReviewFeedback | FailingChecks | Rebase; the launcher picks
                                         // the follow-up prompt from it. Null (pre-vocabulary events) = Unknown,
                                         // treated as ReviewFeedback (the historic meaning)
    bool Automatic = false,              // true when the closeout monitor reopened, false for a human
                                         // (h9k pr resolve). Automatic reopens count against the bounded
                                         // closeout budget; a manual reopen resets it (log #22, §2.2)
    string? ObstructionKey = null,       // this lap's obstruction identity (log #80, backlog 45): the
                                         // failing check name, or the exact set of unresolved thread ids.
                                         // Null on a manual reopen — the progress slate is wiped, not compared
    string? ObstructionSummary = null,   // the human-readable side, read back in a lifetime-ceiling park's
                                         // lap history
    IReadOnlyList<string>? KnownHumanReviewThreadIds = null,     // the two human-engagement comparison
    IReadOnlyList<string>? KnownPendingReviewRequestLogins = null); // points, carried forward exactly
                                                                     // like ObstructionKey (log #80, §2.2)

public sealed record TaskRetried(        // Failed -> Queued by explicit human decision (log #25):
    Guid Id,                             // infra failure around finished work must not strand it.
    Guid? PreviousRunId,                 // the failed run, when one was recorded
    string? Branch,                      // that run's branch as observed at retry time (null when no
                                         // run record exists); resumed by the next claim when it
                                         // survives, clean start from the base branch when gone
    string Reason,                       // required; shown by h9k task show. The failure itself
                                         // stays on the stream (retry appends, never erases)
    DateTimeOffset RetriedAt,
    Guid RetriedByOwnerId);              // human-only: no monitor appends this (log #11), so there
                                         // is no Automatic flag and no budget interaction

public sealed record TaskResolved(      // Failed -> Done by human attestation (log #27): the run
    Guid Id,                             // failed but the objective was met anyway. The failure
    string Reason,                       // stays on the stream (resolve appends, never rewrites);
                                         // Reason is required: an attestation without a why is a
                                         // guess (the AGENTS.md never-guess rule)
    string? PullRequestUrl,              // where the work landed, when known; a resolved task
                                         // shows Done with its PR
    DateTimeOffset ResolvedAt,
    Guid ResolvedByOwnerId);             // human-only, like TaskRetried (log #11)

// Reserved, not built in v0:
// public sealed record TaskSentToRefinement(...);
```

### Aggregate (`Features/Task/TaskAggregate.cs`)

```csharp
public sealed class TaskAggregate
{
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Objective { get; private set; } = string.Empty;
    public TaskType Type { get; private set; }
    public TaskState State { get; private set; }
    public TaskConstraints? Constraints { get; private set; }
    public ExternalReference? ExternalReference { get; private set; }
    public int LeaseGeneration { get; private set; }
    public Guid? ClaimedByNodeId { get; private set; }
    public Guid? CurrentRunId { get; private set; }      // from the latest TaskClaimed
    public Guid? PendingQuestionId { get; private set; }
    public string? PullRequestUrl { get; private set; }  // from TaskCompleted; survives a reopen
    public string? FollowUpBranch { get; private set; }  // set by TaskReopened, cleared by TaskCompleted:
                                                         // while set, the next claim resumes this branch
    public string? RetryBranch { get; private set; }     // set by TaskRetried, cleared by TaskCompleted:
                                                         // the failed run's branch, resumed if it survives
    public FollowUpKind FollowUpKind { get; private set; } // why the pending follow-up exists (prompt selection)
    public int CloseoutAttempts { get; private set; }    // automatic reopens since the last human touch:
                                                         // the lifetime-ceiling counter (§2.2, log #80)
    public string? LastAutomaticObstructionKey { get; }   // this task's most recent automatic lap's
                                                         // obstruction identity — the progress-cap counter's
                                                         // comparison point (§2.2, log #80)
    public int ConsecutiveObstructionLaps { get; }        // consecutive laps against LastAutomaticObstructionKey
                                                         // without clearing it (§2.2, log #80)
    public IReadOnlyList<string> AutomaticLapHistory { get; } // one short description per automatic lap,
                                                         // read back by a lifetime-ceiling park (§2.2, log #80)
    public IReadOnlyList<string> KnownHumanReviewThreadIds { get; }      // the two human-engagement
    public IReadOnlyList<string> KnownPendingReviewRequestLogins { get; } // comparison points as of the
                                                                          // last automatic decision (§2.2)
    public Guid? AssignedOwnerId { get; private set; }   // set by TaskAssigned; the claim guard's other half (§2.3)
    public IReadOnlyList<Guid> BlockedBy { get; }        // declared dependency edges
    public IReadOnlyList<Guid> UnmetDependencies { get; }// those not yet at true closeout; empty on a Queued task
    public IReadOnlyList<Guid> DeadDependencies { get; } // blockers observed Failed/Abandoned
    public string? DependencyFailureReason { get; private set; }

    private readonly List<string> _acceptanceCriteria = [];
    public IReadOnlyList<string> AcceptanceCriteria => _acceptanceCriteria;

    private readonly List<Guid> _runIds = [];            // accumulated from claims; run DETAILS stay a
    public IReadOnlyList<Guid> RunIds => _runIds;        // read-side query (RunListItem by TaskId)

    public void Apply(TaskAdded @event) { /* Id, ProjectId, contract fields, BlockedBy;
                                             State = Draft, or the pre-split Queued + AssignedOwnerId (§2.3) */ }
    public void Apply(TaskPublished @event) { /* State = Published */ }
    public void Apply(TaskRevised @event) { /* only the Optional fields that HaveValue */ }
    public void Apply(TaskReturnedToDraft @event) { /* State = Draft */ }
    public void Apply(TaskAssigned @event) { /* AssignedOwnerId; State = UnmetDependencies.Count == 0 ? Queued : Blocked */ }
    public void Apply(TaskUnassigned @event) { /* AssignedOwnerId = null; dependency bookkeeping cleared; State = Published */ }
    public void Apply(TaskDependencyCompleted @event) { /* drop it from Unmet + Dead; empty => Blocked -> Queued */ }
    public void Apply(TaskDependencyFailed @event) { /* record the dead blocker + reason; State unchanged */ }
    public void Apply(TaskDependencyRecovered @event) { /* drop that dead blocker; reason = what is left; State unchanged */ }
    public void Apply(TaskClaimed @event) { /* LeaseGeneration = @event.LeaseGeneration; ClaimedByNodeId; State = Claimed */ }
    public void Apply(TaskRequeued @event) { /* ClaimedByNodeId = null; State = Queued */ }
    public void Apply(QuestionAsked @event) { /* PendingQuestionId; State = NeedsHuman */ }
    public void Apply(AnswerProvided @event) { /* PendingQuestionId = null; State = Claimed */ }
    public void Apply(TaskCompleted @event) { /* PullRequestUrl; FollowUpBranch = null; State = Done */ }
    public void Apply(TaskReopened @event) { /* FollowUpBranch = @event.Branch; State = Queued */ }
    public void Apply(TaskFailed @event) { /* State = Failed */ }
    public void Apply(TaskRetried @event) { /* RetryBranch = @event.Branch; State = Queued */ }
    public void Apply(TaskResolved @event) { /* PullRequestUrl when provided; State = Done */ }
    public void Apply(TaskAbandoned @event) { /* FollowUpBranch/RetryBranch/PendingQuestionId = null; State = Abandoned */ }
}
```

### 2.1 Follow-up runs (PR closeout) — why `TaskReopened`, not a follow-up claim

A merged-in-review-but-not-yet-mergeable PR needs more work on its **existing branch**
(Decisions Log #18). Three shapes were considered:

- **A new task** — rejected: the follow-up belongs to the original task's audit trail; a
  second task would duplicate the contract and orphan the PR linkage.
- **A "follow-up claim" straight from Done → Claimed** — rejected: claiming is the daemon's
  job (capacity cap, claim race, fencing token). A CLI-initiated direct claim would need a
  parallel dispatch path beside the loop, exactly what the pipeline-reuse rule forbids.
- **`TaskReopened`: Done → Queued** — chosen. The reopened task re-enters the standard
  sweep → claim → launch pipeline untouched; the only branching point is in the launcher,
  which sees `FollowUpBranch` set and checks out the existing PR branch (worktree
  `CheckoutExistingAsync`) instead of cutting a new one, and hands the agent the follow-up
  prompt (resolve-review-threads skill + PR URL). Verification gates and the push run
  through the same `RunSupervisor`/`VerificationRunner`/`PullRequestOpener`; the opener
  sees the task already carries a PR URL, pushes in place, and records `PullRequestUpdated`
  on the run instead of opening a second PR.

Guardrails: only `Done` reopens (Abandoned stays a dead end; Failed has its own human-only
exits: `TaskRetried`, `TaskResolved`, and `TaskAbandoned`, logs #25/#27), and only with a PR URL
on the stream — the feature is PR closeout, not general task resurrection. `TaskCompleted`
consumes `FollowUpBranch`, so a completed follow-up leaves the task exactly as a first
completion does — reopenable again if more feedback arrives.

### 2.2 The closeout phase (automatic monitor, log #18/#22)

`PullRequestOpened` starts a phase, not an epilogue: the daemon's closeout monitor
(`PullRequestMonitor`/`CloseoutEngine`) polls each awaiting-review PR through gh on a
gentle interval. Each node watches the runs **it** executed (`RunDetails.NodeId`); the
task itself is Done and lease-free, so run provenance is the only honest owner. Per poll,
in priority order:

- **Merged** → `PullRequestMerged` + `RunCompleted` on the run (the reserved terminal
  event finds its meaning). The retained worktree (log #21) is removed and the task branch
  deleted everywhere it lingers: locally (`git branch -D`; rebase merges mean the tip is
  never an ancestor, the observed merge is the justification), on the remote (if the merge
  didn't already), and in remote-tracking refs (`git fetch --prune`).
- **Closed without merge** → `PullRequestClosed`; the run fails honestly, the worktree is
  removed, the branch is kept (it still holds unmerged work).
- **Checks completed and failing** (never acted on while any check is pending) →
  `PullRequestChecksFailed`, then an automatic `TaskReopened` (Kind = FailingChecks) in
  the same transaction. The follow-up flows through the unchanged claim/launch pipeline
  with the fix-the-CI prompt.
- **Unresolved review threads, from any reviewer** (log #62) → `ReviewFeedbackReceived`,
  then an automatic `TaskReopened` (Kind = ReviewFeedback) with the resolve-review-threads
  prompt. Copilot is one reviewer among many: a teammate's unresolved thread, and the pull
  request author's own self-review note, count identically and dispatch identically. The
  thread's FIRST comment names the reviewer, because agents only ever reply inside threads
  and never open one (the invariant is recorded in AGENTS.md beside the no-bot-identity
  rule); the event carries how many of the threads a human started, so the follow-up's
  dispatch reason can say a person is waiting. One silence the monitor cannot see: GitHub
  hides an unsubmitted (`PENDING`) review's comments from the API, so feedback arrives only
  on submit.
- **Errored Copilot review** → `ReviewErrored`, then `ReviewRerequested` once the review
  has been re-requested through the provider's API (never the website, which may be down
  when this matters; that was the origin incident's exact circumstance, PR #6 during the
  2026-08-17 GitHub partial outage). An errored review produces zero threads, so thread
  count alone would read as a clean pass; the observation holds the run at `ReviewPending`
  instead. The matching rule is deliberately conservative: a review authored by Copilot,
  taken from GraphQL `latestReviews` (per-reviewer latest, so a successful re-review
  supersedes the errored one structurally), whose body contains "unable to review"
  (Copilot's own failure notice, never arbitrary review text). Each errored review is
  re-requested exactly once (the recorded review URL is the dedup key across sweeps). A
  re-review that leaves threads flows through the bullet above; a clean one leaves the
  run waiting for the merge.
- **A quiet PR whose fixes were just pushed**, where the owner or the project opted in
  (log #62) → `ReviewRerequestedAfterFixes`, once the pull request's reviewers have been
  asked for another pass so whoever raised the findings countersigns that they were
  addressed. Off unless configured (`h9k owner set` / `h9k project set --rerequest-review`,
  project over owner over `DaemonOptions.DefaultReviewRerequest`), because each pass costs
  review quota. Only a follow-up run asks, each run asks at most once, and the passes are
  summed across the task's runs against `DaemonOptions.MaxReviewRerequestsAfterFixes`
  (default 2), its own counter beside the closeout budget rather than part of it. At the cap the
  pull request settles on the internal review, the thread replies, and CI.

**Bounded retries — two counters, not one** (log #80, backlog 45). `TaskAggregate.CloseoutAttempts`
is the absolute lifetime ceiling: it counts every automatic reopen since the last
human-initiated one, exactly as before, checked first and unconditionally against
`DaemonOptions.MaxAutomaticCloseoutRuns` (default 6 — generous, because "many different real
obstructions, one after another" on a busy pull request is the legitimate case this ceiling
exists to allow rather than to punish). Errored-review re-requests still spend the same
lifetime budget: every run that carried the task's current pull request contributes its
`ReviewRerequestCount`, summed since the most recent `h9k pr resolve` grant (`RunDetails.HumanGrantedAt`)
and scoped to the current pull request, so a `h9k task retry` onto a second pull request starts
that PR's closeout unencumbered by the first one's spend, and a grant late in a busy PR's life
restores the same full budget a grant early in its life would have (independent pre-PR review,
cycle 4 — an unscoped lifetime sum never shrinks, so `h9k pr resolve` eroded toward a no-op the
longer a PR ran). Countersign passes do not — `ReviewRerequestsAfterFixes` is counted against
`MaxReviewRerequestsAfterFixes` instead, so one budget running out never silently spends the
other.

Underneath the ceiling sits the progress cap, the part backlog 45 added: `TaskAggregate`
tracks `LastAutomaticObstructionKey` and `ConsecutiveObstructionLaps`, recording each automatic
lap's obstruction identity — mechanical, never judged. A failing-checks obstruction keys on the
check name(s); a review-feedback obstruction keys on the exact set of unresolved thread ids
present at dispatch (so a thread resolved, or a new one opened, is a different obstruction by
definition). A lap whose key matches the task's last one increments the count against
`DaemonOptions.MaxCloseoutLapsPerObstruction` (default 2); a lap whose key differs resets the
count to 1 — a different check, or a changed thread set, is progress, so it never counts against
this cap even while the lifetime ceiling above keeps climbing. Two mechanical
human-engagement signals grant one lap past the progress cap alone — never the lifetime
ceiling, which only `h9k pr resolve` can extend: a newly opened human-started review thread, or
a login newly holding a pending review request. A third candidate signal, a new top-level
pull-request comment, was cut before merge: agents here post top-level comments too (answering a
review body with `gh pr comment`), authored under the same login as a human's, so there is no
discriminator for one the way a review thread's starter has one. Each surviving signal is a set
diffed against a comparison point (`KnownHumanReviewThreadIds`, `KnownPendingReviewRequestLogins`)
that travels forward on `TaskReopened` alongside `ObstructionKey`, so the next decision compares
against what the last one actually saw.
The grant buys exactly one lap through the cap; it does not reset the obstruction's own count,
so a further automatic-only lap on the same still-open obstruction needs its own grant or parks.

At either cap the monitor appends `CloseoutParked` on the run instead of reopening, naming what a
human needs before spending their own attention: a lifetime-ceiling park reads back the full lap
history (`TaskAggregate.AutomaticLapHistory`, one short description per automatic lap); a
progress-cap park names the specific obstruction and how many laps it survived. The task **stays
Done** either way (deliberately, since parking must not break `h9k pr resolve`, whose guard
requires Done, and merge detection continues for parked runs), and `h9k status` surfaces it as
NeedsHuman. A manual `h9k pr resolve` resets both counters — the human asking for another
attempt is a fresh grant, exactly as before — and now also appends `CloseoutBudgetGranted` to
the run's own stream in the same transaction the task-stream reset lands in, so the grant is
legible from the run's own history too.

**Display composition** (`h9k status`, log #66 and §2.4): everything here reads as
**Delivered** until a merge is observed, and the current run's state composes the phase line
beneath it - the post-PR review watcher's own five readings of Copilot (log #89): "watching
PR #24 — Copilot review landed" with its comment-thread count, "watching PR #24 — awaiting
Copilot review · requested but not yet submitted", "watching PR #24 — Copilot reviewed an
earlier commit" with the same thread count and its own stale-review hedge (a real review that
happened, just against a since-superseded commit, distinct from nothing having happened at
all), plain "watching PR #24" with "no external review activity observed; its checks may
still be reporting" while the provider's CI picture is still incomplete, "watching PR #24 —
awaiting human review" once it is not, or plain "watching PR #24" with "no confirmed review
observation recorded; its checks may still be reporting" when no sweep has confirmed review
activity at all — either because none has run yet, or because a sweep read a real Copilot
review it could not compare against the head commit - plus the
failing checks or the unresolved thread count when those are what is observed instead, and
"automatic follow-ups stopped" when parked. The
quiet-checks-pending reading never claims a clean pull request or names the human as the last
gate, because the monitor does not act on checks or review
threads while CI is still reporting — it still records what it saw of Copilot's review state that
same sweep, but an absence of findings beyond that recorded observation is not itself an
observation of a clean pull request. A
Queued/Claimed task still carrying a pull-request URL is a follow-up in flight and says so
("follow-up on PR #24: building"), which is the distinction the old single `ClosingOut` bucket
could not make. **Done** appears only once the merge is observed; a run that ended without one stays
Delivered and quotes the reason the run recorded, which is how a pull request closed without
merging (`PullRequestClosed` records its own reason) reads differently from a gate failure a
human then resolved onto that pull request. `RunState.Failed` carries both, so neither the phase
nor the attention line infers a closure from it. A watched run that is no longer the task's
current run is retired with `RunSuperseded` (a newer follow-up owns the PR).

**The orphan sweep** (log #72): a run this node dispatched can leave the watch above by
failing rather than by merging — a crash, a kill, or (the case that motivated this) a stream
written before the closeout monitor existed at all. Its pull request does not stop existing
just because nothing is watching it. Every sweep therefore also gives one state-only `gh` read
(`InspectStateAsync` — merge/close facts only, none of the reviews or checks a watched run's
full inspection also gathers, since this sweep dispatches no follow-up either way) to each Done
task whose current run is `Failed` or `Killed`, still names a pull request (`PullRequestNumber`
is set), and was not already recorded closed (`FailureReason` is not
`PullRequestClosedWithoutMerge` — that row already knows the one thing an inspection could
tell it). A merge found there is recorded exactly as a watched one is: `PullRequestMerged` +
`RunCompleted`, dependents unblocked, the Jira comment where a reference exists. `RunCompleted`
is dated when this sweep observed the merge, never backdated to `PullRequestMerged.MergedAt` —
the orphan sweep is the one place those two times can be days apart, and recording the
platform's own completion under a fact it did not just witness is the never-guess rule applied
to time. A close without a merge is also recorded, exactly as the watched path records it
(`PullRequestClosed`, `FailureReason` becomes `PullRequestClosedWithoutMerge`): the row's
`AttentionComposer` rendering would otherwise keep pointing the human at `h9k pr resolve` for a
pull request nobody can merge, and the row would keep matching this query's own exclusion
filter forever. A still-open answer is the only true no-op — nothing is invented, and a dead run
is not one this engine dispatches a follow-up onto.

### 2.3 Task development vs task dispatch (log #34)

Two lifecycles used to be one. Discovery produces a rough task and refines it over hours or
days; dispatch is a human deciding *this should run now, and on whose nodes*. `TaskAdded →
Queued` meant the daemon claimed a half-formed thought within seconds of it being written down.

```
h9k task add          ->  Draft       being developed; editable; invisible to the dispatcher
h9k task revise       ->  Draft       objective / criteria / context / type / model / BlockedBy
h9k task publish      ->  Published   the readiness gate; immutable; assignable, NOT claimable
h9k task assign       ->  Queued      every dependency at true closeout
                      or  Blocked     at least one is not
h9k task unassign     ->  Published   refused while a lease is held
h9k task draft        ->  Draft       refused from Queued/Blocked onward (unassign first)
```

**Where validation lives.** Creation asks only for identity — a project and an objective. The
readiness contract (an outcome-phrased objective and at least one checkable acceptance
criterion, PLAN.md §4) is enforced once, at Publish, as an *invariant of that state* rather
than a toll booth at creation. Revision is Draft-only because every later state carries a
promise editing would break: Published promises "a human may assign this at any moment and it
satisfies the contract"; assigned promises "a node may read this at any moment", and revising a
claimable task races the dispatcher.

**The claim guard is one rule.** `task.State == Queued && task.AssignedOwnerId == node.OwnerId`.
There is no other path to a claim, which is also what makes multi-owner projects safe when they
arrive (backlog/IDEA-task-assignment.md): arbitrary pickup is structurally impossible rather
than policy-forbidden. The daemon's queue query stays the cheap indexed-friendly filter it
always was — state plus assigned owner — and dispatch order inside the ready set is unchanged:
FIFO by `AddedAt`. Dependencies and assignment shape the ready set, not its ordering.

**"Complete" means true closeout.** A `BlockedBy` edge is met only when the dependency's run
reached `RunCompleted` — which §2.2's closeout monitor appends when it observes the merge.
Nothing weaker counts, so Draft, Published, Queued, Claimed/Running and AwaitingReview
dependencies all block by the same rule, and a Done task whose pull request is still open still
blocks. Unblocking is driven from that same `RunCompleted` append, so whichever node observed
the merge unblocks the dependents and rings the doorbell; the dispatch loop re-evaluates every
Blocked task each cycle as the safety net (log #8: NOTIFY is a doorbell, polling is what makes
it correct).

**A blocker that dies.** A dependency dies when it can no longer reach true closeout, and there
is more than one door out of the closeout pipeline:

- it ended without one — `Failed` or `Abandoned`; or
- it reads `Done` while the run that would have carried the merge observation has itself ended
  without one: `h9k task resolve`'s attestation exit from `Failed` (log #27) leaves the task
  `Done` on a failed run, a pull request closed unmerged fails the run under a `Done` task, and
  a killed or superseded run is the same shape; or
- it reads `Done` with no run on it at all, so there is nothing left to observe.

A `Done` dependency whose run is still *in* the pipeline (`AwaitingReview`, `ChecksFailing`,
`ReviewPending`, `Conflicting`, `CloseoutParked`) is not dead — it simply has not got there yet,
and blocks.
Whichever door it went through, the dependent stays `Blocked` with `TaskDependencyFailed`
recording what was observed, and `h9k status` reads it as NeedsHuman — the same shape the §2.2
closeout park uses, and for the same reason: silently unblocking would dispatch work whose
premise died, and silence would strand it. Only the dispatch loop's sweep notices these; none
of them append a closeout event for the merge-driven path to react to. Origin incident
(2026-08-20): the rule first enumerated `Failed` and `Abandoned` alone, so a resolved dependency
held its dependents in `Blocked` forever, with no reason and without reading as NeedsHuman.

**A blocker that comes back.** Dead is a question the sweep asks fresh every cycle, never a
flag it sets once. `h9k task retry` puts a failed blocker back to `Queued`, which appends
nothing to its dependents, so the same sweep that recorded the hold is what notices the hold
has stopped being true: it appends `TaskDependencyRecovered` on each dependent, the recorded
death is dropped, and the task returns to plain `Blocked` with the ordinary waiting-on display.
Both records stay on the stream, because the hold happened and so did the recovery. The same
pass restates a hold whose *reason* changed rather than clearing it: a failed blocker a human
resolved is still dead (its run will never carry a merge), but the advice "retry or resolve it"
is no longer a lever the decider would accept, so the death is re-recorded as the one it now
is. And a blocker retried into a second failure is simply held again: hold, recover, hold, each
one observed. Origin incident (2026-08-21): the first overnight crash-recovery failed the
chain's head honestly and held both dependents, then `h9k task retry` put the blocker back to
work and the holds did not clear, leaving a board that read "act now" about a situation already
handled for what would have been the whole rebuild.

**Cycles.** Detection lives at Publish alone. A draft may transiently reference a cycle while a
graph is being authored; a cycle can never become assignable, and the refusal names the cycle
hop by hop rather than saying one exists somewhere.

**Migration.** `TaskAdded.StartsAsDraft` defaults to `false` — the pre-split meaning — so a
stream written before this decision replays exactly as it behaved: Queued on arrival and
assigned to `AddedByOwnerId`, who is the sole owner of a v0 install. That is an observed fact
rather than a guess at provenance, and it needs no marker document: rebuilding the projections
is correct by construction. It does need the rebuild to actually happen. The projections are
Inline, so a stream that stopped receiving events before the split still carries the pre-split
document — with no `assignedOwnerId` key at all, which the claim filter reads as nobody's work.
`TaskLifecycleProjectionBackfill` re-projects exactly those streams at daemon startup, keyed on
the absent key, so it is idempotent and self-terminating (a current document always writes the
key, as a value or as an explicit null). Origin incident (2026-08-20): the split first shipped
the filter without the rebuild, and every task in the dogfooding database became permanently
unclaimable — silently, because an unclaimable task looks exactly like an idle queue.

### 2.4 The display vocabulary (log #66)

Neither `TaskState` nor `RunState` is shown to a human as it stands. One field was answering four
questions at once - where the work is, what is happening right now, whether it wants a human, and
why - and the answer is three surfaces, composed in `TaskStatusComposer` and read by every screen.

**State** is the lifecycle, in seven words: `Draft`, `Published`, `Working`, `Delivered`, `Done`,
`Failed`, `Archived`. It is display-only; nothing about the persisted model changes. `Queued` and
`Blocked` both render as `Published`, with the difference moved onto that row's derived-facts line
("assigned and ready; the dispatcher has not claimed it yet", "waiting on 2 dependencies to close
out"), which is also where the ranking model's facts will land when it retires those two states. A
queued row names a dispatch slot only where `DispatchPressure` carries a current measurement saying
this node is full (Decisions Log #64); with none, it says it is ready and stops, because a queue
that is not moving has many causes and a stopped daemon is the commonest of them. `Delivered` is the new word: pushed, with
the merge not yet observed, which is the window the old display called `Done` while the pull request
was still open. `Done` now renders only at true closeout - the merge observed, or the task closed with
no pull request to watch - which is exactly the bar the dependency rule uses (§2.3), so the board and
the blocker rule finally agree on the word. The no-pull-request arm is answered before the current run
is read at all, because `TaskResolved` does not clear `CurrentRunId`: a task closed by hand keeps the
document of the run it was closed on top of, and reading that run's state would claim a push and a
pending merge for a task that never pushed anything. A run that ended without an observed merge stays
`Delivered` and says what it recorded, because closeout ended but not the way `Done` claims.

**Phase** is the run vocabulary's new home. `Dispatched`, `Running`, `Verifying`, `UnderReview`,
`AwaitingReview`, `ChecksFailing`, `ReviewPending`, `Conflicting`, `CloseoutParked` compose the line under a
`Working` or `Delivered` row and never appear in the Status column. It is **derived only** - no new
events - and it is composed from the run's records **plus one observation of the recorded process**.
`RunDetails` therefore records every session a run has in flight (each one's role, its lens when it
is a review pass, its pid, and its start time, which together are the identity the log #2 reuse
guard needs) - a list, because a review cycle runs one pass per active track at the same time
(log #59), and a run is reported as having lost its process only when every process it records is
gone. A phase never claims a session is doing something without observing the process:
a session on another node, or one whose start time was never recorded, reads as "liveness not
observed here" rather than as either answer. The two meanings of the old `ClosingOut` separate here
- "follow-up on PR #24: building, session alive" against "watching PR #24 — Copilot review landed"
(or awaiting Copilot, or plain "watching PR #24" when no review activity is confirmed yet -
either nothing has run, or a sweep read a review it could not classify, log #89) -
so "is it my turn?" never needs a log dive.

**Attention** is `AttentionComposer`, the single owner of the mapping from recorded facts to a
one-line cause and a lever. Needs-you or not is a column; the cause and the command are the line
under the row. Waiting-but-handled (a blocker already retried, a pull request the closeout monitor
is still working) is its own level and renders dim, so a reader can consciously ignore it - a board
that says "act now" about a handled situation trains its reader to ignore it. Every cause is quoted
from a record: `ReviewParked.Reason`, `CloseoutParked.Reason`, the dependency-failure record, the
recorded failure reason, the observed check and thread counts. A failure with no distinctly recorded
cause (token exhaustion, until backlog 40 lands) shows the text the machinery wrote rather than a
category nobody observed, and a park that recorded no reason says that rather than inventing one.

### Claim atomicity

The daemon claims by appending `TaskClaimed` with Marten's **optimistic concurrency on the
stream** (`AppendOptimistic` / expected-version). Two claimants racing → one succeeds, the
other gets a concurrency exception and moves on. No advisory locks, no claim table; the
stream version *is* the lock. Multi-daemon-safe from day one (§6.2).

### Value objects (slice root — see §8 for the type discipline these follow)

```csharp
public sealed record TaskConstraints(int? MaxTurns, long? MaxTokens, TimeSpan? MaxWallClock);

// Composite VO with parsing behavior: canonical form "github:owner/repo#42"
public sealed record ExternalReference(WorkItemProvider Provider, string Reference)
{
    public override string ToString() => $"{Provider}:{Reference}";
    public static ExternalReference Parse(string value) { /* split on first ':' */ }
}

// Closed-vocabulary VOs (house anatomy per §8; static instances shown, plumbing elided):
public sealed record WorkItemProvider   // GitHub, Jira, Unknown
public sealed record TaskType           // Feature, Bugfix, Refactor, Chore, Research, Unknown
public sealed record TaskState          // Draft, Published, Queued, Blocked, Claimed, NeedsHuman, Done, Failed,
                                        //   Abandoned, Unknown (+ NeedsRefinement reserved, not built in v0)
public sealed record TaskDependency     // one dependency as the lifecycle rules see it: id, objective, state,
                                        //   and whether it reached TRUE closeout (§2.3)
public sealed record RequeueReason      // LeaseExpired, RunFailedRetryable, HumanRequested, Unknown
public sealed record FollowUpKind       // ReviewFeedback, FailingChecks, Rebase, Unknown (§2.2 — prompt selection)
public sealed record AgentModel         // fable | opus | sonnet | haiku aliases, or any exact model id;
                                        // Unknown = "not set at this level" (Shared/ValueObjects, log #33)
public sealed record AgentRole          // Build, Review, Fix, Refinement, Unknown (log #33 per-role defaults)
```

## 3. Run slice

### Events (`Features/Run/Events/`)

```csharp
public sealed record RunDispatched(
    Guid Id,
    Guid TaskId,
    Guid NodeId,
    Guid OwnerId,                        // owner of the node AT dispatch time — frozen here so the §6.2
                                         // accountability chain survives any future node ownership change
    int LeaseGeneration,                 // stamped at birth; stale generation ⇒ output discarded (log #7)
    Guid SessionId,                      // daemon-minted, passed via --session-id (log #5)
    string WorktreePath,
    string Branch,
    ExecutorMode ExecutorMode,           // Subscription | ApiKey (log #1)
    DateTimeOffset DispatchedAt,
    bool IsFollowUp = false,             // resumes the task's existing PR branch (§2.1)
    AgentModel? Model = null);           // the model resolved for the Build role and actually spawned on
                                         // (log #33). Appended with a default, like the log #30 cache
                                         // counts: older streams replay as Unknown, never as a guess.

public sealed record RunProcessStarted(
    Guid Id,
    int ProcessId,
    DateTimeOffset ProcessStartedAt);    // PID + start time = identity, PID-reuse guard (log #2)

public sealed record RunResumed(         // after AnswerProvided: new process, same session (log #5)
    Guid Id,
    int ProcessId,
    DateTimeOffset ResumedAt);

public sealed record VerificationPassed(Guid Id, DateTimeOffset PassedAt);
public sealed record VerificationFailed(Guid Id, IReadOnlyList<string> FailedGates, DateTimeOffset FailedAt);
public sealed record PullRequestOpened(Guid Id, string PullRequestUrl, int PullRequestNumber, DateTimeOffset OpenedAt);
public sealed record PullRequestUpdated( // follow-up run pushed to the task's EXISTING PR (§2.1) —
    Guid Id,                             // the PR updates in place, no second PR. RunState -> AwaitingReview.
    string PullRequestUrl,
    int PullRequestNumber,
    DateTimeOffset UpdatedAt);

public sealed record AgentSessionCompleted( // the agent's claude process emitted its final result event and
    Guid Id,                             // exited; verification gates run next. RunState -> Verifying.
    DateTimeOffset CompletedAt);

// The input side stays three separate counts because each prices differently, and a cached
// session reports nearly all of its input as cache reads (log #30). The two cache fields are
// appended with defaults so streams written before they existed replay as zero, never as a guess.
public sealed record TokensRecorded(     // from the stream-json result payload, per run (§6.4)
    Guid Id,
    long InputTokens,                    // fresh, uncached prompt input
    long OutputTokens,                   // tokens the model generated
    decimal? CostUsd,                    // as the result reported it; the daemon never prices tokens itself
    DateTimeOffset RecordedAt,           // when the result payload was read
    long CacheReadInputTokens = 0,       // cache hits: where a resumed session's input actually lives
    long CacheCreationInputTokens = 0);  // cache writes, priced differently again

// Pre-PR review loop (log #24) — appended by the daemon's ReviewEngine between the gates
// and PullRequestOpener. Full findings text is a disk artifact (log #6), never payload.
// Only cycle 1 pays full discovery: a Discovery or FinalFullPass cycle runs one pass per
// still-active lens (log #59), so the dispatch and pass events come one per lens, while a
// middle Verify cycle dispatches exactly one, standing in for every still-active track
// (task: review cycles after the first) — ReviewCompleted stays the cycle's single merged
// milestone either way. Each lens is a track converging on its own terms (log #63): it
// concludes with its own event, and the loop ends with one ReviewSettled saying how
// merge-ready was reached.
public sealed record ReviewDispatched(   // one review pass spawned over the diff, fresh session;
    Guid Id,                             // one per lens for Discovery/FinalFullPass (two events);
    Guid SessionId,                      // exactly one, under ReviewLens.Verify, for a Verify cycle.
    int Cycle,                           // review rounds, from 1     -> UnderReview. Pid + start =
    int ProcessId,                       // adoption identity (log #2).
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model = null,            // resolved for the Review role in its own right (log #33)
    ReviewLens? Lens = null,             // which attention budget this pass carries; null on streams
                                         //   written before lenses existed (log #59)
    ReviewMode? Mode = null,             // Discovery, Verify, or FinalFullPass (task: review cycles
                                         //   after the first); null reads as Discovery
    string? HeadSha = null);             // git rev-parse HEAD at spawn, best-effort — what the NEXT
                                         //   cycle's Verify prompt (if any) reads "since" from
public sealed record ReviewPassCompleted( // ONE lens of the cycle returned its verdict (log #59);
    Guid Id,                             // that lens's own findings artifact:
    int Cycle,                           // review-<cycle>-<lens>-findings.md in the run directory
    ReviewLens Lens,                     // the lens rides on the event so "which lens found this"
    ReviewVerdict Verdict,               //   is a query over the stream, not an impression
    DateTimeOffset CompletedAt,
    IReadOnlyList<ReviewFindingRecord>?  // each finding's grade, scope tag, pointer, and the
        Findings = null,                 //   disposition the loop chose (log #63) — classification
                                         //   only, never the text. Null on pre-#63 streams.
    ReviewMode? Mode = null,             // which shape THIS pass's cycle took; null reads Discovery
    int? Turns = null,                   // the pass's own session cost — claude's own num_turns
    long? InputTokens = null);           //   and every billed input token, cache reads and
                                         //   writes included. Null on streams written before either
                                         //   field existed.
public sealed record ReviewCompleted(    // the CYCLE's merged verdict over every lens (log #59),
    Guid Id,                             // appended in the same transaction as the cycle's last
    int Cycle,                           // ReviewPassCompleted. Merged findings artifact:
    ReviewVerdict Verdict,               // review-<cycle>-findings.md in the run directory.
    DateTimeOffset CompletedAt);         // MergeReady needs EVERY lens clean; Unknown (any lens left
                                         //   no parseable verdict, OR a needs-fixes verdict naming
                                         //   nothing the platform could read as a finding, log #86)
                                         //   -> one re-prompt, then park (log #28)
public sealed record ReviewVerdictReprompted( // verdict-less pass resumed ONCE in the same session
    Guid Id,                             // (claude -p --resume, log #5) and told to conclude (log #28)
    Guid SessionId,                      // this leg's artifact identity — never the resumed transcript's
    Guid ResumedSessionId,
    int Cycle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset RepromptedAt,
    AgentModel? Model = null,            // the RESUMED session's model, carried and recorded rather than
                                         // re-resolved: a resume keeps what it started on (log #33)
    ReviewLens? Lens = null);            // which pass is being re-prompted; the one re-prompt is the
                                         //   CYCLE's, not each lens's (log #59)
public sealed record ReviewFixDispatched( // fix session in the same worktree, findings as prompt;
    Guid Id,                             // counted for the record; the loop's bounds are the
                                         //   per-track cycle caps (log #63)
    Guid SessionId,
    int Cycle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model = null,            // resolved for the Fix role, separately from Review (log #33)
                                         //   — UNLESS Escalated, in which case it is the Review
                                         //   role's model instead (log #90)
    bool Escalated = false,              // this round repeats an earlier fix round's own
                                         //   findings, AND the two roles actually
                                         //   resolve to different models (log #90)
    string? EscalationReason = null);    // non-null only when Escalated; the Daemon decides it,
                                         //   Domain only records it
public sealed record ReviewFixCompleted( // Fixed/Unknown -> gates re-run, then a fresh review (or, with
    Guid Id,                             //   every track concluded, settling); Disputed -> park. Even the
                                         //   terminal fix re-runs the gates: a settled ending ships
                                         //   commits no REVIEWER read, never unbuilt ones (log #63)
    int Cycle,
    ReviewFixOutcome Outcome,
    DateTimeOffset CompletedAt);
public sealed record ReviewTrackConcluded( // one track finished and went dormant (log #63). Clean = a
    Guid Id,                             //   reviewer read the tip and found nothing; Settled = the
    ReviewLens Lens,                     //   severity gate, scope routing, or the run settling out
    int Cycle,                           //   from under a track still asking for another cycle. A
    ReviewSettlement Settlement,         //   concluded track is never dispatched again by an ordinary
    IReadOnlyList<ReviewResidual>        //   cycle and is never reawakened by the OTHER track's fix
        Residuals,                       // sessions — only ReviewTrackReactivated, below, revives one.
    DateTimeOffset ConcludedAt);         //   Residuals: grade, scope, and fixed-unreviewed vs routed.
public sealed record ReviewTrackReactivated( // the mandatory FinalFullPass found a genuine new
    Guid Id,                             //   defect on a track that had already concluded (task:
    ReviewLens Lens,                     //   review cycles after the first) — the inverse of
    int Cycle,                           //   ReviewTrackConcluded, not a replacement of its record:
    DateTimeOffset ReactivatedAt);       //   the earlier conclusion stays on the stream as history.
public sealed record ReviewFindingRouted( // an out-of-scope non-high routes away from this diff
    Guid Id,                             //   (log #63): a Medium still mints a draft bug task of
    ReviewLens Lens,                     //   its own, a Low instead folds into the project's one
    int Cycle,                           //   standing sweep draft (SweepDraftTask, Decisions Log
    ReviewSeverity Severity,             //   #99). DraftTaskId null + FailureReason
    string Location,                     //   set on a failed courtesy that never fails the review loop.
    Guid? DraftTaskId,
    string? FailureReason,
    DateTimeOffset RoutedAt);
public sealed record ReviewSettled(      // the loop ended; PullRequestOpener may proceed (log #63).
    Guid Id,                             //   The verdict is always MergeReady; Settlement is how it
    int Cycle,                           //   was reached, and the counts are the residuals it left.
    ReviewSettlement Settlement,         //   Absent on runs whose review predates tracks, whose
    int ResidualsFixed,                  //   settlement is honestly unknown rather than assumed
    int ResidualsRouted,                 //   clean. A routing that failed left no draft, so it is
    int ResidualsRoutingFailed,          //   counted apart rather than reported as one that worked.
    DateTimeOffset SettledAt,
    int ResidualsRideAlong = 0);         // a ride-along (log #87) still unclaimed at settle time;
                                         //   0 for a stream that predates ride-alongs
public sealed record ReviewParked(       // budget spent, dispute, or no verdict: the human owns the
    Guid Id,                             // diff. Task stays Claimed, lease retained (the expiry sweep
    string Reason,                       // refreshes a parked lease, never requeues it — log #28).
    DateTimeOffset ParkedAt);            // -> ReviewParked
public sealed record ReviewParkResolved( // the human's verdict via h9k review resolve (log #28):
    Guid Id,                             // MergeReady -> the PR opens; NeedsFixes -> fix session with
    ReviewVerdict Verdict,               // Reason as its findings, fix budget restored (the manual
    string? Reason,                      // grant, log #22). -> UnderReview; the daemon resumes it.
    DateTimeOffset ResolvedAt,
    Guid ResolvedByOwnerId);

// Closeout observations (§2.2) — appended by the closeout monitor, never by agents.
// Provider timestamps are nullable: unreported is recorded as unknown, never guessed.
public sealed record PullRequestConflictObserved( // the branch conflicts with its base (backlog 44);
    Guid Id,                             // GitHub's own mergeable read, checked ahead of checks and
    DateTimeOffset ObservedAt);          // review threads because both are moot once a rebase is coming.
                                         // -> Conflicting, then an automatic TaskReopened (Kind = Rebase)
public sealed record PullRequestChecksFailed( // CI completed and failed; recorded only once nothing is
    Guid Id,                             // pending, so FailedChecks is the full picture. -> ChecksFailing
    IReadOnlyList<string> FailedChecks,
    DateTimeOffset ObservedAt);
public sealed record ReviewFeedbackReceived(  // unresolved review threads from any reviewer (log #62).
    Guid Id,                             // -> ReviewPending
    int UnresolvedThreadCount,
    DateTimeOffset ObservedAt,
    int? UnresolvedHumanThreadCount = null);  // null on events written before authorship was counted
public sealed record ReviewErrored(      // Copilot's latest review is an error placeholder ("unable
    Guid Id,                             // to review"); zero threads must not read as clean.
    string Reviewer,                     // -> ReviewPending; a re-request or a park is appended in
    string ReviewUrl,                    // the same transaction
    DateTimeOffset ObservedAt);
public sealed record ReviewRerequestedAfterFixes(  // opt-in countersign after a fix follow-up pushed
    Guid Id,                             // (log #62); its own pass cap, state unchanged
    IReadOnlyList<string> Reviewers,
    int Pass,
    DateTimeOffset RequestedAt);
public sealed record ReviewRerequested(  // the errored review re-requested via the API; draws on
    Guid Id,                             // the same automatic budget as follow-up dispatches
    string Reviewer,
    string ErroredReviewUrl,
    DateTimeOffset RequestedAt);
public sealed record CloseoutParked(     // a cap was spent (lifetime ceiling or per-obstruction progress
    Guid Id,                             // cap, log #80); the human owns the PR now, the monitor keeps
    string Reason,                       // watching for merge/close only. -> CloseoutParked
    DateTimeOffset ParkedAt);
public sealed record CloseoutBudgetGranted( // h9k pr resolve's grant, recorded on the RUN stream (log #80,
    Guid Id,                             // backlog 45) — the reset itself lands on the task stream as
    string? Reason,                      // TaskReopened(Automatic: false); this is the same grant made
    DateTimeOffset GrantedAt);           // legible from the run's own history. No state change.
public sealed record PullRequestMerged(  // the merge observed; RunCompleted follows in the same transaction
    Guid Id,
    DateTimeOffset? MergedAt,            // GitHub's timestamp via gh; null when unreported
    DateTimeOffset ObservedAt);
public sealed record PullRequestClosed(  // closed without merge: run -> Failed, branch kept
    Guid Id,
    DateTimeOffset? ClosedAt,
    DateTimeOffset ObservedAt);

public sealed record RunHandoffRecorded(  // what this run hands down to whatever depends on it (§3.2).
    Guid Id,                              // Captured at session end, appended HERE, in the merge
    HandoffOutcome Outcome,               // transaction, with PullRequestMerged and RunCompleted.
    string? Summary,                      // Bounded; null whenever the outcome records an absence.
    DateTimeOffset RecordedAt);
public sealed record ContextSynthesisDispatched(  // a wide fan-in condensed before this run starts (§3.2)
    Guid Id,
    Guid SessionId,
    int BlockerCount,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model = null);
public sealed record ContextSynthesisCompleted(   // false = fell back to the raw handoffs; never a failure
    Guid Id,
    bool Synthesized,
    DateTimeOffset CompletedAt);

public sealed record RunCompleted(Guid Id, DateTimeOffset CompletedAt);   // terminal: the PR merged (§2.2)
public sealed record RunFailed(Guid Id, string Reason, DateTimeOffset FailedAt);
public sealed record RunKilled(Guid Id, KillReason Reason, Guid? KilledByOwnerId, DateTimeOffset KilledAt);
public sealed record RunSuperseded(Guid Id, int SupersededByGeneration, DateTimeOffset SupersededAt);
```

```csharp
// Closed-vocabulary VOs (anatomy per §8). ExecutorMode is the smart-enum case — it OWNS the
// spawn-flag rules from log #1 instead of leaving them to switch statements in the daemon:
public sealed record ExecutorMode       // Subscription, ApiKey, Unknown
{
    public bool UsesBareFlag => this == ApiKey;          // --bare is API-key-only (log #1)
    public bool InjectsApiKey => this == ApiKey;
}

public sealed record KillReason         // BudgetExceeded, HumanRequested, Superseded, Unknown
public sealed record RunState           // Dispatched, Running, Verifying, UnderReview, ReviewParked (§3.1),
                                        //   AwaitingReview, ChecksFailing, ReviewPending, Conflicting,
                                        //   CloseoutParked (§2.2), Completed, Failed, Killed, Superseded, Unknown
public sealed record ReviewVerdict      // MergeReady, NeedsFixes, Unknown (§3.1)
public sealed record ReviewFixOutcome   // Fixed, Disputed, Unknown (§3.1)
public sealed record ReviewLens         // Conformance, Adversarial (§3.1); Unknown = a pass recorded
                                        //   before lenses existed, which covers Conformance without
                                        //   claiming it said so. Verify is the pseudo-lens a Verify
                                        //   cycle's one pass is recorded under, whose Covers answers
                                        //   true for both real lenses (task: review cycles after the
                                        //   first). CycleLenses (Conformance, Adversarial) is the
                                        //   seam (log #59)
public sealed record ReviewMode         // Discovery, Verify, FinalFullPass (§3.1, task: review
                                        //   cycles after the first) — the shape a cycle's dispatch
                                        //   took: two full passes, one verifying pass over the
                                        //   prior cycle's own findings, or the mandatory pre-settle
                                        //   full-rigor pass. Recorded on ReviewDispatched and
                                        //   ReviewPassCompleted; only cycle 1 is ever Discovery.
public sealed record HandoffOutcome     // Captured, NotAuthored, NotCaptured (§3.2),
                                        //   NotClosedOut (query-only: no run to ask yet), Unknown
```

The `RunAggregate` mirrors the Task shape: sealed class, private setters, one `Apply` per event,
`RunState` derived. The transcript is **not** here — it's the run's `stream.jsonl` on disk
(log #2/#6); the stream records milestones only.

### 3.1 The pre-PR review loop (log #24)

`VerificationPassed` no longer leads straight to the PR: the daemon's `ReviewEngine`
dispatches **independent review agents** — separate headless sessions with fresh context,
never the session that wrote the code — over the run's diff against the base branch.
Verified findings only (read the surrounding code, confirm the defect, discard the
unconfirmed), each with file:line, a defect statement, and a concrete failure scenario,
closed by a parsed `VERDICT:` line.

**Only cycle 1 pays full discovery** (task: review cycles after the first, origin: 576M
input tokens in one day re-reading 12k-line diffs with two lenses to judge 40-line fixes).
Cycle 1 is always `ReviewMode.Discovery`: one pass per lens, and `ReviewLens.CycleLenses`
is that list — **Conformance** asks whether the work meets its objective, its acceptance
criteria, and repo doctrine, while **Adversarial** assumes the code is wrong somewhere and
hunts defect classes without ever being told what the work was supposed to do. The passes
are dispatched together and awaited one at a time, so a cycle costs the slower pass rather
than the sum. Each appends its own `ReviewDispatched` and its own `ReviewPassCompleted`
carrying its lens and that lens's verdict; when the cycle's last pass lands, the findings
merge into one document and the verdicts merge into one `ReviewCompleted`, appended in the
same transaction as that last pass event. A middle cycle instead dispatches exactly one
`ReviewMode.Verify` pass, recorded under the pseudo-lens `ReviewLens.Verify`, standing in
for every still-active track — handed the prior cycle's own merged findings and fix
summary so it verifies the fix and its blast radius rather than re-deriving the diff.
Immediately before the run may settle, one mandatory `ReviewMode.FinalFullPass` cycle runs
both lenses fresh regardless of which had already gone dormant, so nothing ships on
delta-green alone; a track it reawakens with a real finding gets its own
`ReviewTrackReactivated` rather than being left at its old conclusion, and its own cycle
cap is measured from the cycle it was reawakened at, not the run's absolute cycle count, so
it gets a genuine chance to fix what that pass found. A run that converges clean at cycle 1
pays no extra pass at all. Because that relaxation means a track's own cap can never bound
a track the mandatory pass keeps reawakening, `FinalFullPass` carries an independent cap of
its own, `MaxFinalFullPassRounds` (3): however many cycles have run as `FinalFullPass`,
hitting that count without ever settling parks the run for a human, exactly like a capped
track does. **MergeReady requires every lens clean.**

All three numbers above are compiled defaults only when nothing overrides them (Decisions Log
#108): each is settable durably at the node (`h9k config set`), overridden per project (`h9k
project set`), and overridden again per task (`h9k task set-review-caps`, settable at any time —
even against a task whose run is live, which is the documented takeover lever for a task
observed grinding: a cap set at or below the cycles that track has run since its last human
takeover grant (0, if it has never had one — not the same as the absolute review cycle number
`h9k status`/`h9k task show` print, which never resets) parks it at the very next check, and 0
always parks immediately since that count can never be negative), strictly
`task > project > node > compiled default`, resolved per cap
independently. A fourth setting, the task-lifetime review-cycle budget (default 25), sums
`ReviewCycle` across every run and follow-up a task has had — immune to the resets a stranding,
retry, or follow-up round gives the three per-run caps above — and follows the same hierarchy
and visibility surfaces.

- **merge-ready** → that track concludes Clean and goes dormant; when the last track does,
  `ReviewSettled` records how the loop ended and `PullRequestOpener` proceeds (§2.2 follows).
- **needs-fixes** → **one** fix session runs in the same worktree with the *merged* findings
  of every live lens as its prompt; the gates re-run; a *fresh* set of reviewers looks again
  (review → fix → gates → review).
- **dispute** — the fix run judging a finding not-a-defect, human-territory, or wrongly
  graded — parks immediately with both positions on disk (findings + fix-position
  artifacts) rather than looping on judgment.

**Each lens is a track with its own cycle count and its own convergence rule** (log #63).
Since Decisions Log #87 both lenses grade every finding, and a pass earns a fix session only
when one of its findings is **Fix-dispositioned** (`ReviewFinding.Disposition`, not severity
alone — an out-of-scope finding nobody graded is `Fix` too, the conservative reading that
never routes an ungraded defect away on no evidence it is safe to). `ReviewEngine.
RecordReviewPassAsync` reclassifies the recorded verdict against `Disposition` in **both**
directions before either track's own convergence rule ever runs: a verdict is only ever
recorded merge-ready when **every** stated finding is `RideAlong`-dispositioned — a needs-fixes
verdict that clears that bar is demoted, and a verdict the lens itself already answered
merge-ready is promoted back to needs-fixes the moment it carries anything else, so a
mis-graded or ungraded `Fix` finding attached to a literal `VERDICT: merge-ready` line cannot
skip the fix it owes. A `Route` finding is deliberately not treated like a `Fix` finding here,
only kept from earning a merge-ready verdict the same way: a route-only pass stays needs-fixes
so `ReviewTrackPolicy.Decide`'s own pre-gate rule (below) can still keep that track alive for a
tip the *other* track's fix session may yet rewrite — demoting it would retire the track before
it ever reads that rewrite. A below-the-bar finding **rides along**
(`ReviewFindingDisposition.RideAlong`) rather than forcing a cycle.
Conformance ends the moment nothing it found meets the fix bar;
still finding something that does at `MaxComplianceReviewCycles` (3) parks the run, because
nothing automated is left to try. A below-the-bar finding rides along instead of being fixed
on its own **at every cycle, gate or no gate** — the fix bar reads only severity, never the
cycle. Adversarial runs under a **severity gate** that governs a different question, whether
the track is *forced* into another cycle regardless of severity: through
`AdversarialSeverityGateFromCycle - 1` a needs-fixes verdict with a Route finding still forces
the next cycle even though nothing attached meets the fix bar (a needs-fixes verdict whose
findings are all ride-alongs is demoted to merge-ready before it ever reaches this rule — the
pre-#87 rule was "any finding forces the next cycle AND is fixed there"; #87 split those two,
and only the forcing half survives before the gate), and from the gate cycle onward only a
`high` still forces it — a medium is still fixed there, it just stops re-triggering the loop on
its own. The **empty terminal case** (a cycle whose findings all
route away, so nothing is left to fix) ends the track from the gate cycle too, and not
before it: while the other track can still rewrite the branch, a track retired early would
never read the fix commits. It cannot spin on an unchanged tip, because a cycle with nothing
anywhere left to fix derives `Settling` and ends the run whatever the track decided. Its cap is `MaxAdversarialReviewCycles` (10), and highs
still appearing there park the run as "the machine kept finding real problems", not as a
spent budget. A track that concludes goes dormant and is deliberately never reawakened by
the other track's fix sessions.

**Scope routes findings, it does not rank them.** Every adversarial finding carries a
mechanical scope tag: in-scope if the defective line is in code this branch added or
changed, out-of-scope if it is pre-existing on the base branch. An out-of-scope **high** is
fixed here in a commit of its own; an out-of-scope **non-high** is not fixed here at all,
and the daemon routes it away instead (`ReviewFindingRouted`), inert until a human acts on
it — but where it lands then splits by grade (`ReviewSeverity.MeetsFixBar`): a **Medium**
still mints its own **draft bug task**, exactly as every non-high did before this split
existed, while a **Low** instead folds into the project's one standing **sweep** draft
(`SweepDraftTask`, Decisions Log #99) — so a serious pre-existing defect can never be
buried in a polish pile, and eight one-line Low findings cost one build-gate-review
pipeline instead of eight. A failed draft creation, of either kind, is recorded as a failed
routing and never fails the review loop; it is counted apart from the routings that worked,
because no draft exists for it. A defect is routed **once per run**: the fix session is
told to leave a routed line alone and every later reviewer has fresh context, so the same
line comes back for as long as anything else keeps the loop alive, and re-routing it would
turn one defect into one inert draft per cycle. Two reports are the same defect when they
name the same *place* rather than the same string (`ReviewFindingLocations`), so
`src/Legacy.cs:40` and `./Legacy.cs:40` match; a different stated line in the same file
deliberately does not, because collapsing by file would swallow a second, genuinely
different defect.

**A ride-along (Decisions Log #87) is settled within its own cycle, never carried into a later
one.** It lands in the cycle's own merged findings artifact under its own disposition group.
`ReviewEngine.RecordReviewPassAsync` first decides which tracks conclude this cycle from whether
*any* track's plan carries a `Fix` finding: if one does, only the tracks already saying
`Continues: false` conclude here (a track still saying `Continues: true` gets a fresh look at the
fix commits next cycle instead, so its own ride-alongs wait); if none does — the empty terminal
case (log #63) — every active track concludes THIS cycle regardless of its own `Continues`, since
the very next phase derivation settles the whole run anyway. It then decides each concluding
track's ride-alongs from whether a fix session is actually going to dispatch this cycle, which is
a narrower question than "any track carries a `Fix` finding": a track can carry `Continues: true`
and a real `Fix` finding while already at `ReviewTrackPolicy.CapReached`'s own cap, in which case
the next `DriveAsync` iteration parks that track instead of reaching the dispatch, and no fix
session ever reads the concluding tracks' ride-alongs — so the fix-dispatch fact is additionally
gated on no *continuing* plan already being capped. When a fix session does dispatch, it reads
the whole merged findings document — ride-alongs included — so every concluding track's
ride-alongs are folded in and recorded as `ReviewResidualDisposition.FixedUnreviewed`, exactly
like a genuine fix shipped without a second read. When no fix session is going to dispatch —
whether because nothing anywhere carries a `Fix` finding, or because the only `Fix` findings
belong to a continuing, capped track — each concluding track's ride-along has no later cycle to
wait for either way, so it is a residual, `ReviewResidualDisposition.RideAlong`, the instant its
track concludes. Either way
`RunAggregate.DeriveResidualTally` collapses ride-along residuals
per distinct location the same way it already does for `Routed` and `FixedUnreviewed`, and
`ReviewSettled` and `h9k task show` report the count alongside the existing fixed/routed ones —
recorded, never fixed, no cycle ever spent earning it one.

**What stays per cycle rather than per track**: one fix session over every live track's
findings, and one verdict re-prompt however many passes ended without a `VERDICT:` line or
with a needs-fixes verdict naming nothing the platform could read as a finding
(`ReviewVerdictValidation.NamesAFinding`, Decisions Log #86) — recorded as `ReviewVerdict.Unknown`
the same as a missing line, so it takes the same re-prompt-then-park path rather than parking a
human or spending a fix session over content that was never stated.
Two tracks do not double the fixing or the parking math.

**The terminal verdict stays MergeReady; `ReviewSettlement` says how it was reached.**
Clean means a reviewer read the final tip and found nothing; Settled means the gate, the
routing, or a human's park resolution ended it, and the **residuals** (grade, scope, and
fixed-unreviewed vs routed) are recorded so a settled ending never reads like a clean one.
`h9k task show` prints the distinction.

A parked run keeps its task Claimed and its lease alive (adoption refreshes the
heartbeat at startup): the worktree is the human's workspace. Review and fix sessions
record `TokensRecorded` on the run like any other session, and their transcripts live
beside the main session's in the run directory, named per pass:
`review-<lens>-<cycle>-<session>.stream.jsonl` (a pass recorded without a lens, from a
stream written before lenses existed, keeps the original `review-<cycle>-<session>` name,
which is what lets a daemon upgraded mid-review still find the running session's files).
The findings artifacts follow the same split: each lens writes its own words to
`review-<cycle>-<lens>-findings.md`, and the merged document the fix session reads and a
park points a human at stays `review-<cycle>-findings.md`. The engine is a state machine
over the run stream (`ReviewPhase`, derived in the aggregate), so a restarted daemon
resumes the loop exactly where the events left off, including a cycle whose passes were
only half dispatched: the missing lenses are topped up rather than the finished ones
re-run.

**A review lens prompt carries the task's prior human review-park rulings forward (log #88).**
Fresh context per cycle (log #59) stays the independence guarantee — what travels between
cycles is the ruling, not the reviewer's memory. `ReviewEngine.LoadPriorRulingsAsync` reads
every `ReviewParkResolved` recorded across every run the task has ever had
(`RunDetails.ReviewParkResolutions`, oldest first, so a retry's fresh run stream still inherits
rulings from an earlier one), except a thread-dispute park's resolution (log #62,
`ReviewCycle == 0` — not `ParkedFromState == RunState.Verifying`, which reads `UnderReview` rather
than `Verifying` on every dispute past the first and so would misclassify a second-or-later
resolve as a settled ruling): that park caught the run before any gate or reviewer
ever read the diff, so the human resolved a disputed thread, not a review finding, and it is not
recorded as a settled ruling. `AgentPromptBuilder.AppendSettledRulings` renders the newest 8
rulings, each reason summarized to 500 characters, under a heading that reads the two verdicts
differently: a merge-ready ruling is settled and re-raising it needs a stated reason something
changed, while a needs-fixes ruling is a human-confirmed defect the reviewer checks for and
reports again if the ordered fix never landed. Only a merge-ready ruling's reason is echo-stripped
before `ReviewVerdictValidation.NamesAFinding` reads the reviewer's output (a dismissal echoed
back must not count as naming a finding); a needs-fixes reason is left alone, because its defect
vocabulary is exactly what a legitimate re-raise of a still-unfixed confirmed defect would use.
The rendered block is appended to both
lenses, unconditionally paired with a pointer at whatever doctrine the project's own
AGENTS.md/CLAUDE.md documents (a decisions log among them, if it keeps one) — deliberately
generic rather than naming this platform's own PLAN.md by path, since `AgentPromptBuilder`
serves every registered project, not just this one. `h9k review resolve`'s `--merge-ready` gained
an optional `--reason <TEXT>` for the same reason: a human dismissing a finding as a false
positive can say why and have it reach the next fresh-context pass instead of vanishing the
moment the park cleared.

**A repeat fix round over the same findings escalates to the Review role's model (log #90).**
`ReviewFixEscalation.Reason` is the trigger, conservative by design: a location match via
`ReviewFindingLocations.SamePlace` against `RunAggregate.LastFixRoundFindingLocations` — the most
recent automated-findings fix round's own findings, not necessarily the immediately preceding
round and never the whole run's history (a human-findings round advances the cycle without
replacing this list, so it can lag by more than one round), which is what makes de-escalation
automatic once a repeated finding clears — or the human's own `h9k review resolve
--needs-fixes` reason literally naming a previous round's location. A mechanical redispatch of the
very same round (a budget-exhaustion retry re-entering `FixNeeded` with the cycle and
`RunAggregate.PendingHumanFindings` both unchanged) reuses that round's already-decided outcome
rather than asking the question again over content that has not changed. The escalation only
changes anything when the Review and Fix roles actually resolve to different models:
`ReviewEngine.DispatchFixSessionAsync` requires `reviewModel != fixModel` alongside a non-null
`ReviewFixEscalation.Reason` before setting `ReviewFixDispatched.Escalated`, so a default install
that has never set `--model-review`/`--model-fix` (log #82), or a task overriding both roles the
same way, resolves them identically and a repeated round there dispatches on the ordinary Fix
model exactly as it would have anyway. `h9k task show` prints a "Fix escalation" line while the
newest run's most recent fix dispatch is escalated.

### 3.2 Context routing along dependency edges (log #36)

A `BlockedBy` edge does double duty. It was declared for scheduling, but "this could not
start until that finished" is also a statement about *relatedness*: the blocker almost
certainly touched the same area or produced the thing the dependent builds on. So the graph
a human already declared answers the context question, and there is no second structure to
maintain.

**Capture and landing are two moments, deliberately.** The handoff text is read from the
agent's own session-end result (`HandoffParser` over the terminal result event's summary,
extending the parsing already there rather than adding a summarizer session) and written to
`~/.hall9k/runs/<run-id>/handoff.md`. The event that carries it, `RunHandoffRecorded`, is
appended by `CloseoutEngine` at **true closeout**, in the same transaction as
`PullRequestMerged` and `RunCompleted` and immediately before the dependents are unblocked.
That ordering is the guarantee: an unmerged run has no `RunHandoffRecorded`, so its summary
can never travel to work that builds on code which never landed.

**What lands is the task's handoff, not the completing run's.** Decision #22 makes review
follow-ups automatic, so a merged pull request is normally the original run (retired with
`RunSuperseded` when the follow-up was dispatched) plus a follow-up that resolved the review
threads and reached `Completed`. Selecting the completing run alone would hand a dependent the
thread resolution and leave the description of the feature unread in a superseded run's
directory. `CloseoutEngine` therefore composes the landed summary from the `handoff.md` of
every run that carried the pull request, in dispatch order, so the run that built the thing
leads. Failed and killed runs are excluded, and that exclusion *is* the retry case: a run that
died left work which never merged, so its summary must not travel, while a superseded run is
the opposite situation, the run whose work is in this merge.

**A run that closes out without a usable handoff is valid**, and `HandoffOutcome` says which
absence it is rather than collapsing them: `Captured` (the agent authored one), `NotAuthored`
(the result was read and carried none), `NotCaptured` (there was no session-end capture at
all: a park a human resolved by hand, an agent that never reported a result), `Unknown` (a
stream written before handoffs existed, and equally an artifact that exists but could not be
read, since an unread file is not an observed one). The three artifact states on disk (non-blank,
empty, absent) are exactly the three observations, which is what lets the append be honest
without guessing (the AGENTS.md never-guess rule). `NotClosedOut` belongs to the same
vocabulary but is never recorded on a run: only a query about a *task* can observe that
nothing carried it to true closeout, which is the honest answer for a blocker still in
flight or one a human attested Done without a merge (log #27). Where a composition finds
that no run authored a handoff, the recorded absence is the least certain of the ones the
reads observed, because a file that could not be read might have held the very text the
dependent wanted.

**Assembly is depth one, and the depth is the design.** When `DispatchEngine` claims a task,
`BlockerContextAssembler` reads `BlockerHandoffQuery` over the task's **immediate**
`BlockedBy` edges and stops. A chain A -> B -> C hands C what B learned and nothing of A's.
If C genuinely needs a fact from A, that is evidence of a missing A -> C edge, something a
human can fix, rather than a context gap the platform should paper over by walking further
back; the prompt tells the agent to say so in its own handoff. Accumulating ancestry would
also mean that by the time a long chain reaches its end, most of what the agent reads is
about work it will never touch. `TaskDependencyQuery.LoadGraphAsync` still loads the
transitive closure, because cycle detection at publish must see a cycle anywhere in the
chain; context assembly reads the first hop on purpose.

Which run a blocker's handoff is read from is settled by the run's own terminal state:
`RunState.Completed`, which only the closeout monitor's merge observation produces, and which
is where the composed summary lands. A blocker that died and was retried to a successful merge
therefore hands down the successful run's summary and never the failed one's, by construction
rather than by filtering. A blocker with no handoff falls back to its objective and acceptance
criteria: a blocker with nothing to say still says what it was for.

**Fan-in condenses above a threshold.** Eight tasks converging on an integration task is good
decomposition, not a smell, but eight handoffs is a heavy way to open a session. Above
`DaemonOptions.BlockerSynthesisThreshold` (default 3) a platform-dispatched synthesis session
condenses them into one document first, following the review-session patterns: resolved and
recorded model (log #33), `TokensRecorded` on the dependent's run, and artifacts
(`context-synthesis-*.stream.jsonl`, `blocker-context.md`) in that run's own directory. At or
below the threshold the handoffs pass through raw. A synthesis that dies or returns nothing
usable records `ContextSynthesisCompleted(Synthesized: false)` and the launch falls back to
the raw handoffs; condensing is an optimization over a context that already exists, so it may
never be the reason a dispatch loses one. It is also the only session a dispatch *blocks* on
(every other spawn is fire-and-forget, monitored in the background), and `LaunchAsync` is
awaited inside the dispatch loop, so `DaemonOptions.BlockerSynthesisTimeout` (default 5
minutes) bounds what one hung condenser can cost the node: the wait ends, the session is
terminated, and the run starts on the handoffs it already had.

`h9k task show` prints the same document the daemon would paste into the prompt, through the
same `BlockerContextDocument` renderer, so a human checking what an agent will start with is
reading that context rather than a second telling of it.

Reviewer findings deliberately do **not** travel downstream in v1; only the agent's own
handoff does. That is noted as an open extension in `backlog/IDEA-context-routing.md` rather
than built speculatively.

## 4. Reference aggregates (minimal v0 streams)

All event-sourced per the "event-sourcing first" principle; each is one or two events in v0.

```csharp
// Features/Owner/Events/
public sealed record OwnerRegistered(Guid Id, string Name, string? Email, DateTimeOffset RegisteredAt);

// Features/Node/Events/   (created at `h9kd install`)
public sealed record NodeRegistered(Guid Id, Guid OwnerId, string MachineName, string OperatingSystem, DateTimeOffset RegisteredAt);

// Features/Connection/Events/   (v0 has exactly one: github / gh-cli)
public sealed record ConnectionRegistered(
    Guid Id, Guid OwnerId, WorkItemProvider Provider, string ExternalAccountId,
    CredentialReference CredentialReference, DateTimeOffset RegisteredAt);

// Structured VO, not a bare string: canonical forms "gh-cli", "keychain:<name>", "env:<var>"
public sealed record CredentialReference(CredentialKind Kind, string? Identifier)
{
    public static readonly CredentialReference GhCli = new(CredentialKind.GhCli, null);
    public static CredentialReference Keychain(string name) => new(CredentialKind.Keychain, name);
    public static CredentialReference EnvironmentVariable(string name) => new(CredentialKind.EnvironmentVariable, name);
    // Parse/ToString round-trip the canonical string; CredentialKind is a closed-vocab VO per §8
}

// Features/Project/Events/
public sealed record ProjectRegistered(
    Guid Id, Guid OwnerId, Guid ConnectionId, string Name,
    string RepositoryPath,               // local clone/bare-repo path the daemon makes worktrees from
    Uri? RepositoryUrl,                  // the remote — plain git, provider-agnostic (GitHub today,
                                         // Azure DevOps et al. are a WorkItemProvider away, not a schema change)
    string BaseBranch,
    DateTimeOffset RegisteredAt);

public sealed record ProjectSettingsChanged(   // Optional<T> pattern: absent ≠ null
    Guid Id,
    Optional<IReadOnlyList<VerifyCommand>> VerifyCommands,
    Optional<bool> SkipPermissions,            // log #9: per-project opt-in
    Optional<int> MaxParallelAgents,
    Optional<IReadOnlyList<ContextLink>> ContextLinks,
    DateTimeOffset ChangedAt,
    Guid ChangedByOwnerId,
    Optional<CommitStyle> CommitStyle = default,  // how follow-up runs land fixes on the PR branch (log #26)
    Optional<AgentModel> Model = default);        // the project's model default (log #33). Unknown is a legal
                                                  // explicit value: it clears the override so the node's
                                                  // per-role and platform defaults decide again.

public sealed record VerifyCommand(string Name, string Command);   // "test", "dotnet test"

// Named pointers injected into every agent's context for this project: the agent follows them
// itself (via MCP, gh, or fetching). Gets "here's our Jira, figure it out" with zero connector
// work — connectors formalize specific providers later; links cover everything else forever.
public sealed record ContextLink(string Name, Uri Url);            // "jira", "wiki", "staging"
```

## 5. Projections & reads (all inline `SingleStreamProjection`, per house style)

| Document | Source stream | Serves |
|---|---|---|
| `IdeaDetails` | Idea | `h9k idea list`, `h9k idea show` (one document serves both: ideas are few and small) |
| `TaskDetails` | Task | `h9k task show` |
| `TaskListItem` | Task | `h9k status`, daemon queue query (`State == Queued`) |
| `RunDetails` | Run | `h9k task show`, `h9k logs` header |
| `RunListItem` (has `TaskId`, `TaskState`-relevant fields) | Run | joined client-side by `TaskId` |

**No multi-stream projection.** `h9k task show` issues two queries (task by id, runs by
`TaskId`); `h9k status` composes display state in the query handler. Single-stream inline
projections are the house default; multi-stream waits until a read genuinely demands it.

## 6. Telemetry documents (mutable, NOT event-sourced, NOT projections)

Per log #7/#11: heartbeats and liveness are telemetry, not domain facts. Plain Marten
documents in `Documents/`, upserted in place by the daemon. They must not live on projection
documents (a projection rebuild would wipe them).

```csharp
// Features/Task/Documents/
public sealed class TaskLease
{
    public Guid Id { get; set; }              // == TaskId
    public Guid NodeId { get; set; }
    public int LeaseGeneration { get; set; }
    public DateTimeOffset HeartbeatAt { get; set; }
}

// Features/Run/Documents/
public sealed class RunActivity
{
    public Guid Id { get; set; }              // == RunId
    public DateTimeOffset LastActivityAt { get; set; }   // from tailing stream.jsonl
    public long StreamBytesRead { get; set; }            // daemon tail cursor, restart-safe
}
```

Lease expiry sweep = query `TaskLease` where `HeartbeatAt < now - timeout`. Stall detection =
`RunActivity.LastActivityAt < now - 1h` for live runs (log #11).

## 7. Command flow — two doors, one decider

The conventional Wolverine arrangement routes every mutation through an `[AggregateHandler]`.
Hall9k's decision #8 makes the CLI a thin writer with **no Wolverine host**, so:

- **Daemon-side mutations** (claim, dispatch, verification, terminal states) use house-style
  `[AggregateHandler]` static handlers under full Wolverine — identical to NTS.
- **CLI-side mutations** (`task add`, `ask`, `answer`, `project add`, registrations) call the
  same static decider logic directly and append the returned event(s) with a lightweight
  Marten session + raw `NOTIFY`. To keep one source of truth, validation lives in the static
  decider methods (e.g. `TaskDecider.Add(...)`, `TaskDecider.Answer(...)`) that both paths
  invoke — the Wolverine handler is a thin adapter over the same decider.
- House `Domain*Exception` hierarchy carries over; the CLI maps them to exit codes + stderr
  instead of HTTP statuses.

Everything else follows the established house setup: an `AddMartenEventStore()`-style config
extension (`UseSystemTextJsonForSerialization(EnumStorage.AsString, Casing.CamelCase)`,
`UseLightweightSessions`, `IntegrateWithWolverine(UseFastEventForwarding)`), `IDomainAssemblyMarker`,
a `FakeEvent<T>` stub for DB-free projection tests, Alba + Testcontainers for integration,
xUnit + FluentAssertions.

## 8. Type discipline — value objects over primitives and enums

Standing standard for this codebase: any closed domain vocabulary is a single-file
sealed-record value object, not an enum and not bare strings. Enums are reserved for
in-process technical outcomes that are never persisted or serialized (none exist in this
draft; the first legitimate one will likely be a process-spawn result inside the daemon).

The value-object anatomy (this stack serializes with System.Text.Json throughout — no
Newtonsoft anywhere):

```csharp
[JsonConverter(typeof(TaskTypeJsonConverter))]
public sealed record TaskType
{
    public static readonly TaskType Feature  = new("Feature");
    public static readonly TaskType Bugfix   = new("Bugfix");
    public static readonly TaskType Refactor = new("Refactor");
    public static readonly TaskType Chore    = new("Chore");
    public static readonly TaskType Research = new("Research");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly TaskType Unknown  = new("");

    public string Value { get; }
    private TaskType(string value) => Value = value;   // private ctor preserves the closed set

    public static implicit operator string(TaskType? type) => type?.Value ?? string.Empty;
    public static implicit operator TaskType(string? value) => value.IsBlank() ? Unknown : new TaskType(value);

    public bool Equals(TaskType? other) => other is not null && Value == other.Value;
    public bool Equals(string? other) => other is not null && Value == other;
    public override int GetHashCode() => Value.GetHashCode();

    private sealed class TaskTypeJsonConverter : JsonConverter<TaskType> { /* bare string in/out */ }
}
```

Rules carried over:
- **Non-enforcing by design**: the implicit conversion accepts any string; the guarantee is
  "the set is defined once," not "invalid values are unrepresentable." Old/new payloads
  round-trip as themselves (event-stream-safe across versions).
- VOs are declared **directly on events and aggregates**, initialized to the `Unknown`
  sentinel; they serialize as the bare string, so streams and projections stay readable JSONB.
- Events reference other aggregates **by ID only**, never object graphs.
- **Query discipline**: convert the VO to `string` *before* a Marten LINQ predicate
  (`string queued = TaskState.Queued; … Where(t => t.State == queued)`) — LINQ providers do
  not reliably invoke implicit conversions; whether Marten's handles the boxed VO is to be
  verified in a spike, and until proven the convert-first rule is mandatory.
- **Identity naming**: Marten-native `Guid Id` (PascalCase), on aggregates, events, and
  projections alike.
- **Acronyms are spelled out in type, method, and property names** (`PullRequestOpened`, not
  `PrOpened`); ubiquitous ones (`Api`, `Url`, `Id`) stay. Method *parameters* may abbreviate
  where the parameter's type already carries the meaning.

## 9. Open items folded forward to task 3/4

- Exact Marten schema bootstrap for the CLI (AutoCreate.None after `h9kd install` runs migrations).
- Where `Optional<T>` and shared value objects land (`Hall9k.Domain/Shared/` vs `Hall9k.Contracts`).
- Package versions: Marten 8.17.0, WolverineFx(.Marten) 5.9.2, UUIDNext 4.2.3 — pinned once, centrally (Directory.Packages.props).

## 10. Idea slice (log #35)

Ideas sit in front of tasks: an idea undergoes **discovery** (what is this?), and a draft task
undergoes **refinement** (how does this become executable?). A task is an idea with intent, and
promotion is the hinge. Flat tiny-slice layout, like Owner and Node: aggregate, events, decider,
and projection as sibling files under `Features/Idea/`.

```csharp
// Features/Idea/  (one record per file)
public sealed record IdeaCaptured(Guid Id, Guid OwnerId, string Text, Guid? ProjectId, DateTimeOffset CapturedAt);
public sealed record IdeaRevised(Guid Id, string Text, DateTimeOffset RevisedAt, Guid RevisedByOwnerId);
public sealed record IdeaAssignedToProject(
    Guid Id, Guid ProjectId, Guid? PreviousProjectId, DateTimeOffset AssignedAt, Guid AssignedByOwnerId);
public sealed record IdeaPromoted(                 // names the draft it became; TaskAdded.SourceIdeaId
    Guid Id, Guid TaskId, Guid ProjectId,          // names this idea back (two-way provenance)
    string Objective, DateTimeOffset PromotedAt, Guid PromotedByOwnerId);
public sealed record IdeaDiscarded(Guid Id, string Reason, DateTimeOffset DiscardedAt, Guid DiscardedByOwnerId);

// IdeaState: Captured (in discovery) -> Promoted | Discarded. Both endings are terminal;
// there is no "refined" state, because refinement belongs to the draft, not to the idea.
```

Three things this slice deliberately does not do:

1. **It does not record what discovery produces.** Each idea owns a workspace directory,
   `~/.hall9k/ideas/<idea-id>/workspace` (`IdeaPaths`), derived from the id exactly as
   `RunPaths` derives a run's directory. Research notes, gathered files, and prototypes
   accumulate there; the stream carries milestones only. Per-file provenance is the
   attachments feature's job (IDEA-task-attachments), not this one's.
2. **It does not duplicate the task lifecycle.** Promotion emits an ordinary `TaskAdded`
   (a Draft, per log #34) whose agent context is the note's remainder plus the workspace
   pointer, and the human then walks the ordinary ceremony: revise, publish, assign.
3. **It does not interpret the note.** The objective is the first sentence, taken by a
   mechanical scan and printed back, or whatever `--objective` said. Nothing is inferred.

Reads follow the house rule: one inline `SingleStreamProjection` (`IdeaDetails`), carrying the
current note, every earlier version of it, the project (or its honest absence), and what the
idea became. No multi-stream projection: `h9k idea show` issues its own small queries for the
project, the owner, and the promoted task.
