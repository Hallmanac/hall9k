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
| **Task** | days–weeks | the work's story: readiness contract, claims/leases, conversation, terminal outcome |
| **Run** | minutes–hours | one dispatch attempt: process, session, verification, PR, tokens |
| **Owner** | permanent | the human (§6.2 accountability root) |
| **Node** | permanent | one machine's identity |
| **Project** | permanent | repo binding + verify commands + agent policy |
| **Connection** | permanent | provider credential indirection (§10) |

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
  `ReviewPending`, and `CloseoutParked` record what the closeout monitor observed (see §2.2).
  `Completed` arrives only when the monitor observes the merge.)

`h9k status` composes the display state: a claimed task shows its current run's state, and a
`Blocked` task whose blocker died reads as `NeedsHuman` while staying Blocked (§2.3).

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
    FollowUpKind? Kind = null,           // ReviewFeedback | FailingChecks; the launcher picks the
                                         // follow-up prompt from it. Null (pre-vocabulary events) = Unknown,
                                         // treated as ReviewFeedback (the historic meaning)
    bool Automatic = false);             // true when the closeout monitor reopened, false for a human
                                         // (h9k pr resolve). Automatic reopens count against the bounded
                                         // closeout budget; a manual reopen resets it (log #22, §2.2)

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
                                                         // the bounded closeout-retry counter (§2.2)
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
    public void Apply(TaskDependencyCompleted @event) { /* drop it from Unmet; empty => Blocked -> Queued */ }
    public void Apply(TaskDependencyFailed @event) { /* record the dead blocker + reason; State unchanged */ }
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
  prompt (resolve-copilot-reviews skill + PR URL). Verification gates and the push run
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
- **Unresolved Copilot review threads** → `ReviewFeedbackReceived`, then an automatic
  `TaskReopened` (Kind = ReviewFeedback) with the resolve-copilot-reviews prompt.
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

**Bounded retries.** `TaskAggregate.CloseoutAttempts` counts automatic reopens since the
last human-initiated one. Review re-requests spend the same budget: the watched run's
`ReviewRerequestCount` adds to the task's count when either path checks it. At the budget
(DaemonOptions.MaxAutomaticCloseoutRuns, default 2) the monitor appends `CloseoutParked`
on the run instead of reopening: the task **stays
Done** (deliberately, since parking must not break `h9k pr resolve`, whose guard requires
Done, and merge detection continues for parked runs), the run parks with the reason, and
`h9k status` surfaces it as NeedsHuman. A manual `h9k pr resolve` resets the budget; the
human asking for another attempt is a fresh grant.

**Display composition** (`h9k status`): Done+PR refines by the current run's state.
AwaitingReview while quiet, ChecksFailing/ReviewPending when observed, NeedsHuman when
parked, Done once merged (or closed). A Queued/Claimed task still carrying a PR URL is a
follow-up in flight: ClosingOut. A watched run that is no longer the task's current run
is retired with `RunSuperseded` (a newer follow-up owns the PR).

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
`ReviewPending`, `CloseoutParked`) is not dead — it simply has not got there yet, and blocks.
Whichever door it went through, the dependent stays `Blocked` with `TaskDependencyFailed`
recording what was observed, and `h9k status` reads it as NeedsHuman — the same shape the §2.2
closeout park uses, and for the same reason: silently unblocking would dispatch work whose
premise died, and silence would strand it. Only the dispatch loop's sweep notices these; none
of them append a closeout event for the merge-driven path to react to. Origin incident
(2026-08-20): the rule first enumerated `Failed` and `Abandoned` alone, so a resolved dependency
held its dependents in `Blocked` forever, with no reason and without reading as NeedsHuman.

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
public sealed record FollowUpKind       // ReviewFeedback, FailingChecks, Unknown (§2.2 — prompt selection)
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
public sealed record ReviewDispatched(   // independent reviewer spawned over the diff, fresh session.
    Guid Id,                             // -> UnderReview. Pid + start = adoption identity (log #2).
    Guid SessionId,
    int Cycle,                           // review rounds, from 1
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model = null);           // resolved for the Review role in its own right (log #33)
public sealed record ReviewCompleted(    // the verdict milestone; findings artifact:
    Guid Id,                             // review-<cycle>-findings.md in the run directory
    int Cycle,
    ReviewVerdict Verdict,               // MergeReady | NeedsFixes | Unknown (no parseable verdict ->
    DateTimeOffset CompletedAt);         //   one same-session re-prompt, then park; log #28)
public sealed record ReviewVerdictReprompted( // verdict-less reviewer resumed ONCE in the same session
    Guid Id,                             // (claude -p --resume, log #5) and told to conclude (log #28)
    Guid SessionId,                      // this leg's artifact identity — never the resumed transcript's
    Guid ResumedSessionId,
    int Cycle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset RepromptedAt,
    AgentModel? Model = null);           // the RESUMED session's model, carried and recorded rather than
                                         // re-resolved: a resume keeps what it started on (log #33)
public sealed record ReviewFixDispatched( // fix session in the same worktree, findings as prompt;
    Guid Id,                             // counted against MaxAutomaticReviewFixRuns
    Guid SessionId,
    int Cycle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model = null);           // resolved for the Fix role, separately from Review (log #33)
public sealed record ReviewFixCompleted( // Fixed/Unknown -> gates re-run, fresh review; Disputed -> park
    Guid Id,
    int Cycle,
    ReviewFixOutcome Outcome,
    DateTimeOffset CompletedAt);
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
public sealed record PullRequestChecksFailed( // CI completed and failed; recorded only once nothing is
    Guid Id,                             // pending, so FailedChecks is the full picture. -> ChecksFailing
    IReadOnlyList<string> FailedChecks,
    DateTimeOffset ObservedAt);
public sealed record ReviewFeedbackReceived(  // unresolved Copilot review threads. -> ReviewPending
    Guid Id,
    int UnresolvedThreadCount,
    DateTimeOffset ObservedAt);
public sealed record ReviewErrored(      // Copilot's latest review is an error placeholder ("unable
    Guid Id,                             // to review"); zero threads must not read as clean.
    string Reviewer,                     // -> ReviewPending; a re-request or a park is appended in
    string ReviewUrl,                    // the same transaction
    DateTimeOffset ObservedAt);
public sealed record ReviewRerequested(  // the errored review re-requested via the API; draws on
    Guid Id,                             // the same automatic budget as follow-up dispatches
    string Reviewer,
    string ErroredReviewUrl,
    DateTimeOffset RequestedAt);
public sealed record CloseoutParked(     // automatic budget spent; the human owns the PR now, the monitor
    Guid Id,                             // keeps watching for merge/close only. -> CloseoutParked
    string Reason,
    DateTimeOffset ParkedAt);
public sealed record PullRequestMerged(  // the merge observed; RunCompleted follows in the same transaction
    Guid Id,
    DateTimeOffset? MergedAt,            // GitHub's timestamp via gh; null when unreported
    DateTimeOffset ObservedAt);
public sealed record PullRequestClosed(  // closed without merge: run -> Failed, branch kept
    Guid Id,
    DateTimeOffset? ClosedAt,
    DateTimeOffset ObservedAt);

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
                                        //   AwaitingReview, ChecksFailing, ReviewPending, CloseoutParked (§2.2),
                                        //   Completed, Failed, Killed, Superseded, Unknown
public sealed record ReviewVerdict      // MergeReady, NeedsFixes, Unknown (§3.1)
public sealed record ReviewFixOutcome   // Fixed, Disputed, Unknown (§3.1)
```

The `RunAggregate` mirrors the Task shape: sealed class, private setters, one `Apply` per event,
`RunState` derived. The transcript is **not** here — it's the run's `stream.jsonl` on disk
(log #2/#6); the stream records milestones only.

### 3.1 The pre-PR review loop (log #24)

`VerificationPassed` no longer leads straight to the PR: the daemon's `ReviewEngine`
dispatches an **independent review agent** — a separate headless session with fresh
context, never the session that wrote the code — over the run's diff against the base
branch. Verified findings only (read the surrounding code, confirm the defect, discard
the unconfirmed), each with file:line, a defect statement, and a concrete failure
scenario, closed by a parsed `VERDICT:` line.

- **merge-ready** → `PullRequestOpener` proceeds; the closeout phase (§2.2) begins as before.
- **needs-fixes** → a fix session runs in the same worktree with the findings as its
  prompt; the gates re-run; a *fresh* reviewer looks again (review → fix → gates → review).
  Bounded by `DaemonOptions.MaxAutomaticReviewFixRuns` (default 2, the §2.2 budget
  pattern); exhaustion parks the run (`ReviewParked` → NeedsHuman in `h9k status`) with
  the findings artifact attached.
- **dispute** — the fix run judging a finding not-a-defect or human-territory — parks
  immediately with both positions on disk (findings + fix-position artifacts) rather
  than looping on judgment.

A parked run keeps its task Claimed and its lease alive (adoption refreshes the
heartbeat at startup): the worktree is the human's workspace. Review and fix sessions
record `TokensRecorded` on the run like any other session, and their transcripts live
beside the main session's in the run directory (`review-<cycle>-<session>.stream.jsonl`).
The engine is a state machine over the run stream (`ReviewPhase`, derived in the
aggregate), so a restarted daemon resumes the loop exactly where the events left off.

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
