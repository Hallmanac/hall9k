# Concepts

The layer under the [README](../README.md): what the moving parts are, what the words on the
board mean, and why the pipeline stops where it stops. This is an on-ramp, not a specification.
Where a section ends with a pointer, that pointer is the real reference and this page is the
summary that gets you there knowing what you are looking at.

- [The shape of the system](#the-shape-of-the-system)
- [Ideas and tasks](#ideas-and-tasks)
- [The task lifecycle](#the-task-lifecycle)
- [The three surfaces: state, phase, attention](#the-three-surfaces-state-phase-attention)
- [Dependencies and true closeout](#dependencies-and-true-closeout)
- [Runs](#runs)
- [Leases](#leases)
- [Verification gates](#verification-gates)
- [The pre-PR review loop](#the-pre-pr-review-loop)
- [Closeout](#closeout)
- [Owners, nodes, and connections](#owners-nodes-and-connections)

---

## The shape of the system

One machine is one **node**. A node runs three things:

- **`h9k`**, the CLI. It executes and exits. Humans use it, scripts use it, and headless agents
  use it mid-run. It is deliberately thin: it opens a lightweight database session, does its
  work, and leaves. It never hosts the message bus, and it works while the daemon is down.
- **`h9kd`**, the daemon. The resident orchestrator: the dispatch loop, lease management, the
  executor that spawns agent sessions, the verification runner, the review engine, and the
  closeout monitor.
- **Postgres**, in a container. Event streams via Marten, messaging via Wolverine.

There is no socket and no local HTTP API between the CLI and the daemon. **The database is the
bus.** The CLI writes to Postgres and rings a `NOTIFY` doorbell; the daemon listens, and also
polls on an interval because a doorbell is not a delivery guarantee. Anything the CLI writes is
durable whether or not a daemon is listening, which is why a stopped daemon costs latency and
never correctness.

Above all of it sits the **orchestrator window**: an interactive Claude Code session acting as
the conversational surface over `h9k`. It is stateless and disposable, because every fact lives
in Postgres. [AGENTS.md](../AGENTS.md) documents that role in full, and it is the role an
interactive session in this repository is in.

Architecture in depth: [PLAN.md §6](../PLAN.md).

## Ideas and tasks

An **idea** is anything entering the funnel, captured with no ceremony: one command, one
argument, a project optional. It undergoes **discovery**, which answers "what is this?", and it
owns a workspace directory at `~/.hall9k/ideas/<idea-id>/workspace` where research notes,
gathered files, and prototypes accumulate. The event stream records milestones only; file
contents stay on disk.

A **task** is an idea with intent. `h9k idea promote` is the hinge: discovery ends and
**refinement** begins, which answers "how does this become executable?". The idea's note seeds
the draft mechanically (its first sentence becomes the objective, the remainder becomes agent
context), the workspace pointer rides along, and provenance is recorded in both directions. An
idea that is discarded is closed with its reason and never deleted, because an idea that keeps
coming back is a signal and only a kept record can show it.

A task always maps to at most one external work item: a GitHub issue or a Jira card. **Content
belongs to the external system; everything operational belongs to the task.** The task carries
the agent-facing context, the run history, the dependencies, the pull requests, the token
economics, and the conversation.

Depth: [PLAN.md §3](../PLAN.md) for the funnel, [TASK-MODEL.md §10](../TASK-MODEL.md) for the
idea slice, [PLAN.md §4.1](../PLAN.md) for what the task entity carries.

## The task lifecycle

Task development and task dispatch are two lifecycles, split on purpose. Discovery produces a
rough task and refines it over hours or days; dispatch is a human deciding *this should run now,
and on whose nodes*. Collapsing the two meant the daemon claimed a half-formed thought seconds
after it was written down.

```
h9k task add          ->  Draft       being developed; editable; invisible to the dispatcher
h9k task revise       ->  Draft       objective / criteria / context / type / model / BlockedBy
h9k task publish      ->  Published   the readiness gate; immutable; assignable, NOT claimable
h9k task assign       ->  Queued      every dependency at true closeout
                      or  Blocked     at least one is not
h9k task unassign     ->  Published   refused while a lease is held
h9k task draft        ->  Draft       refused from Queued/Blocked onward (unassign first)
```

The edit-after-the-fact path is therefore `unassign → draft → revise → publish → assign`, each
step an explicit act. Revision is Draft-only because every later state carries a promise that
editing would break: Published promises the task satisfies the contract and may be assigned at
any moment, and an assigned task promises a node may read it at any moment.

**The readiness contract** is enforced once, at publish, as an invariant of that state rather
than a toll booth at creation. It wants an outcome-phrased objective and at least one checkable
acceptance criterion. Acceptance criteria are the highest-leverage field in the system: they are
what the verification gates and the reviewer agents test against. Adoption from a GitHub issue or
a Jira card never invents them, because a description is not a contract.

Beyond dispatch the states are `Claimed`, `NeedsHuman`, then `Done`, `Failed`, or `Abandoned`.
`NeedsHuman` is what an agent's recorded question puts the task in, and it is the one state with
no command behind it yet: `h9k ask` and `h9k answer` are Slice 2, so the row surfaces under
needs-you and a human answers it by hand. Only `Done` and `Abandoned` are terminal. **`Failed` is
a waypoint, not an end**: a failed state means there is an unsolved problem, and an unsolved
problem is not an outcome. It has exactly three human-only
exits, covered under [the recovery levers](operations.md#the-recovery-levers).

Depth: [TASK-MODEL.md §2.3](../TASK-MODEL.md), [PLAN.md §4](../PLAN.md), Decisions Log #34.

## The three surfaces: state, phase, attention

Neither the persisted task states nor the run states are what a human reads. One field was
answering four questions at once (where is the work, what is happening right now, does it want
me, and why), so the display is three separate surfaces composed from the underlying records.

**State** is the lifecycle in seven words: `Draft`, `Published`, `Working`, `Delivered`, `Done`,
`Failed`, `Archived`. `Queued` and `Blocked` both render as `Published`, with the difference
moved onto the row's facts line, and the persisted `Abandoned` renders as `Archived`.
`Delivered` means pushed with the merge not yet observed.
**`Done` renders only at true closeout**, which is the same bar the dependency rule uses, so the
board and the blocker rule agree on the word.

**Phase** is the line under a live row, and it is where the run vocabulary lives: `Dispatched`,
`Running`, `Verifying`, `UnderReview`, `ReviewParked`, `AwaitingReview`, `ChecksFailing`,
`ReviewPending`, `Conflicting`, `CloseoutParked`. It is derived, never stored, and it is composed from the run's
records **plus an observation of the recorded process**. A phase never claims a session is doing
something without seeing the process: a session on another node reads as "liveness not observed
here" rather than as either answer.

The two parks in that list are the ones to keep straight, because they take different levers.
`ReviewParked` is a run stopped before its pull request exists and `CloseoutParked` is one stopped
after, so `h9k task list --state ReviewParked` reaches exactly the runs `h9k review resolve`
answers, and `--state CloseoutParked` the ones `h9k pr resolve` does.

**Attention** is needs-you or not, and it is a column. The cause and the command that clears it
go on the line beneath, and every cause is quoted from a record rather than inferred.
Waiting-but-handled situations (a blocker already retried, a pull request the monitor is still
working) render dim as their own level, because a board that says "act now" about a handled
situation trains its reader to ignore it.

`h9k task list --state` still selects on the run vocabulary as well as the lifecycle words, so
`--state AwaitingReview` and `--state needs-you` both work.

Depth: [TASK-MODEL.md §2.4](../TASK-MODEL.md), Decisions Log #66.

## Dependencies and true closeout

`h9k task revise <id> --blocked-by <other-id>` declares an edge. The graph is enforced, never
inferred: two tasks that would rewrite the same file collide unless somebody says so. Sequencing
the ready set is currently a human judgment, and [AGENTS.md](../AGENTS.md) documents how to make
it. Cycle detection lives at publish alone, and the refusal names the cycle hop by hop.

**A dependency counts as met only at true closeout**: the pull request merged and the closeout
monitor observed the merge. Nothing weaker counts, so a blocker sitting on an open pull request
does not unblock its dependents yet. The board says so in the same word: that row reads
`Delivered` until the merge is observed, and `Done` is the moment the dependents are free.

A blocker can also *die*, meaning it can no longer reach closeout: it failed, it was abandoned,
it was resolved by hand onto a failed run, its pull request was closed unmerged, or it has no run
left to observe. When that happens the dependent stays `Blocked` and reads as needs-you, rather
than silently unblocking (which would dispatch work whose premise died) or silently stranding
(which would lose it). Deadness is a question the dispatch sweep asks fresh every cycle, so a
blocker put back to work by `h9k task retry` clears the hold on its own.

The edge does double duty. "This could not start until that finished" is also a statement about
relatedness, so the graph routes context: a dispatched run receives what its **immediate**
blockers wrote at their own closeout, exactly one hop, no further. That is the "What your
blockers handed down" section a dispatched agent reads.

Depth: [TASK-MODEL.md §2.3 and §3.2](../TASK-MODEL.md), Decisions Log #34, #36, #61.

## Runs

A **run** is one attempt at a task. It carries its own event stream, its own worktree, its own
branch (`task/<id>-<slug>`, cut from the base branch with `--no-track`), and its own directory
under `~/.hall9k/runs/<run-id>/` holding the prompt, the settings, the stream-json transcript,
and any review artifacts.

The agent is a detached `claude -p` process with `--output-format stream-json`. Detached is the
point: agents outlive the terminal that requested them and the daemon that spawned them. The
daemon records the pid and session id, tails the stream file, and monitors the run. Claude Code
subagents are deliberately not used as workers, because they run inside a parent session, flood
its context, and serialize on it.

A run's state machine is `Dispatched → Running → Verifying → UnderReview → AwaitingReview →
Completed | Failed | Killed | Superseded`. The agent process finishing enters `Verifying`, not
`Completed`: the agent finishing is not the run finishing. `Completed` arrives only when the
merge is observed.

One task can have several runs: a retry after a failure, and a follow-up dispatched onto an open
pull request. `h9k task show` lists them all with their outcomes, and `h9k logs <task>` renders
the newest by default, with `--run` for an earlier one.

Depth: [TASK-MODEL.md §3](../TASK-MODEL.md), [PLAN.md §6.3](../PLAN.md).

## Leases

Claiming is lease-based, and the lease tracks **daemon responsibility**, not agent progress.
Agents know nothing about leases.

The claim itself is a `TaskClaimed` event appended with optimistic concurrency on the stream, so
two claimants racing produce one winner and one concurrency exception. The stream version is the
lock; there is no claim table and no advisory lock. That is multi-daemon-safe from the first day,
which is the entire down-payment on the multi-node future.

Each task carries a **generation counter**, a fencing token. Every claim increments it, every run
records the generation it was dispatched under, and any state change arriving from a
stale-generation run is discarded. Correctness therefore does not depend on timing.

Daemon startup runs a fixed order, which is what makes a restart safe: **adopt** live recorded
runs first (reattaching, refreshing heartbeats, and processing anything that completed while the
daemon was down), then **sweep** expired leases back to the queue, then **claim** new work,
killing any superseded prior-generation process before redispatching. The net effect on one node
is that a healthy agent is never killed by lease mechanics.

Depth: [PLAN.md §6.2](../PLAN.md), Decisions Log #7, #12, #29, #69, #70.

## Verification gates

Before any review happens, the run's own gates run in its worktree: whatever the project
configures with `h9k project set --verify "name=command"`. For this repository that is
`dotnet build` and `dotnet test`. A gate failure fails the run with the failing gate and its
output recorded, fails the task with it, and releases the lease. Nothing automatic follows: the
task is `Failed` and waiting on one of the three human exits.

Gates are deterministic and cheap to trust, which is why they come first. Everything after them
is judgment.

## The pre-PR review loop

A passing gate does not open a pull request. The daemon dispatches **independent review agents**
over the run's diff against the base branch: separate headless sessions with fresh context, never
the session that wrote the code. Role separation is what makes the gate worth anything.

Every cycle runs one pass per **lens**, dispatched together:

- **Conformance** asks whether the work meets its objective, its acceptance criteria, and repo
  doctrine.
- **Adversarial** assumes the code is wrong somewhere and hunts defect classes, without ever
  being told what the work was supposed to do.

Findings must be verified: read the surrounding code, confirm the defect, discard the
unconfirmed. Each carries a `file:line`, a defect statement, and a concrete failure scenario.

**Each lens is a track with its own cycle count and its own convergence rule.** Conformance ends
only once everything it found rides along instead of earning its own fix — an out-of-scope
finding still routes elsewhere and keeps the track running even though it never meets the fix bar
itself — and reaching its cap parks the run only when a fix is still owed there; with nothing left
to fix, the cap settles the run quietly instead. A low or ungraded finding rides along instead of
being fixed on its own at every cycle, gate or no gate — what the severity gate actually changes
is whether the track is forced into another cycle regardless of severity: early cycles re-trigger
on any finding, including a routed one that never itself meets the fix bar, while later cycles
re-trigger only on a high, and a medium is still fixed there but stops keeping the loop alive on
its own. A track that
concludes goes dormant and is never reawakened by the other track's fix sessions. **Differing
cycle counts on one run are the design, not a fault** (a clean conformance track can be dormant
at cycle 2 while adversarial is still working at cycle 5).

When a cycle needs fixes, **one** fix session runs in the same worktree with the merged findings
of every live track, the gates re-run, and a fresh set of reviewers looks again. A finding the
fix session **disputes** (not a defect, human territory, wrongly graded) parks the run
immediately with both positions written to disk, rather than looping on judgment. That park is
what `h9k review resolve` answers.

Scope routes findings rather than ranking them. An out-of-scope high (a pre-existing defect on
the base branch) is fixed here in its own commit; an out-of-scope non-high becomes a **draft bug
task** that is inert until a human publishes it.

The terminal verdict is always MergeReady, but the *settlement* records how it was reached:
Clean means a reviewer read the final tip and found nothing, Settled means the severity gate,
routing, or a human's resolution ended it, with the residuals recorded. A settled ending never
reads like a clean one.

**Fresh context does not mean no memory (Decisions Log #88).** Each cycle's reviewers are new
sessions with no memory of the task's earlier cycles, but the prompt they are handed carries
forward what a human already settled on this task: every prior `h9k review resolve` verdict and
reason, so a finding a human already dismissed with evidence, or one they confirmed as a real
defect, is not re-raised or re-litigated as though the question were new. The project's own
doctrine (AGENTS.md/CLAUDE.md, and any decisions log it documents) rides along the same way, so a
deviation already ratified there reads as a deliberate choice rather than an oversight the
reviewer just caught. A thread-dispute park (Decisions Log #62) is the one park that plays no
part in this: it settles a disputed comment thread before any reviewer ever reads the diff, not a
review finding, so its resolution is not carried forward as a settled ruling.

**A repeat fix round over the same findings escalates to the review role's model (Decisions Log
#90).** When a fix session dispatches over substantially the same findings its immediately
preceding fix round already tried — the same location an automated pass keeps returning, or a
human's own `--needs-fixes` reason restating it — that fix session runs on the review role's
model instead of the fix role's, so the observed dodge-and-redo failure mode (a weaker model
sidestepping a defect rather than fixing it) gets a stronger model exactly where it recurs. This
only changes anything when the two roles actually resolve to different models: a default install
that has never set `--model-review`/`--model-fix`, or a task overriding both the same way,
resolves them identically, and a repeated round there dispatches on the ordinary fix model exactly
as it would have anyway. De-escalation is automatic the moment a later round moves on to a
genuinely different finding, with no separate reset step. `h9k task show` prints a "Fix
escalation" line while the newest run's most recent fix dispatch escalated this way.

Depth: [TASK-MODEL.md §3.1](../TASK-MODEL.md), Decisions Log #24, #59, #62, #63, #88, #90.

## Closeout

`PullRequestOpened` starts a phase, not an epilogue. The daemon opens the pull request (agents
never do, and there is deliberately no create-pr skill), and then the closeout monitor polls it
on a gentle interval through `gh`. Each node watches the runs it executed, because run provenance
is the only honest owner once the task itself is lease-free.

Per poll, in priority order:

- **Merged.** The run completes, dependents unblock, the retained worktree is removed, and the
  branch is deleted locally, remotely, and in remote-tracking refs.
- **Closed without merge.** The run fails honestly. The worktree goes; the branch stays, because
  it still holds unmerged work.
- **Copilot's review state, recorded every sweep the pull request is still open.** Landed,
  requested-but-pending, reviewed-an-earlier-commit (a real review that happened, just against
  a commit the pull request has since moved past), absent, or unknown (no confirmed review
  activity — either nothing has run yet, or a sweep read a review it could not classify because
  the provider left its commit unreported), together with whether the
  provider's CI picture is still incomplete — informational only, never a `RunState` transition,
  and appended ahead of every
  branch below (including the checks-pending short circuit and a parked run's merge/close-only
  handling), so it lands on the run stream even when nothing else acts this sweep.
- **Conflicting with its base branch.** GitHub's own `mergeable` read, never inferred from how
  long the branch has sat open. Checked ahead of checks and threads because both readings are
  moot against a diff about to be superseded by a rebase, and every merge into the base makes
  every other open pull request staler; nothing else was watching for it (backlog 44). A
  follow-up run is dispatched onto the branch with the rebase-onto-main prompt.
- **Checks completed and failing.** Never acted on while any check is still pending. A follow-up
  run is dispatched onto the branch with the fix-the-CI prompt.
- **Unresolved review threads, from any reviewer.** A follow-up run is dispatched with the
  resolve-review-threads prompt. Copilot is one reviewer among many: a teammate's thread and the
  author's own self-review note count and dispatch identically.
- **An errored Copilot review.** Re-requested exactly once through the provider's API, because an
  errored review produces zero threads and thread count alone would read as a clean pass.

Two silences are worth knowing about. GitHub hides an unsubmitted (`PENDING`) review's comments
from the API entirely, so a pull request can look quiet while feedback is being written: never
read silence as "the reviewer had nothing to say". And the monitor does not act on checks or
review threads while CI is still reporting — the pending-checks read short-circuits ahead of both
— even though it still records what it saw of Copilot's review state on the run stream that same
sweep; which is why the quiet phase line says "no external review activity observed; its checks
may still be reporting" rather than claiming a clean pull request.

Automatic follow-ups are bounded by two counters, not one. A **progress cap**
(`MaxCloseoutLapsPerObstruction`, default 2) counts consecutive laps spent on the *same*
obstruction — the same failing check, the same set of unresolved thread ids — and resets the
moment a lap actually clears something, so a busy pull request grinding through different real
problems never trips it. A **lifetime ceiling** (`MaxAutomaticCloseoutRuns`, default 6) is the
true runaway backstop: every automatic lap spends it regardless of which obstruction it answered,
and nothing bypasses it but `h9k pr resolve`. A human engaging with the pull request since the
last automatic decision — a newly opened review thread, a fresh pending review request — grants
one lap past the progress cap alone, because a person showing up is itself proof the loop isn't
running away; the lifetime ceiling still applies underneath that grant. At either cap the monitor
parks the run instead of reopening, so the task keeps the closed state it reached when its pull
request opened and the board goes on showing it as `Delivered`. Merge detection continues, and the
row reads needs-you with `h9k pr resolve` as the lever, naming the specific obstruction (a
progress-cap park) or the full lap history (a lifetime-ceiling park) so the human knows what the
machine already tried. A manual resolve resets both counters, because a human asking for another
attempt is a fresh grant.

**The platform never merges.** That is the last human checkpoint and it stays one.

Depth: [TASK-MODEL.md §2.2](../TASK-MODEL.md), Decisions Log #18, #22, #62, #80, #81.

## Owners, nodes, and connections

**Every node belongs to a human.** Not to an agent, not to a service. Whatever autonomy agents
gain, a person is responsible for every run their nodes perform, and the chain is queryable: this
pull request came from this run, on this node, belonging to this human.

An owner record exists even when there is exactly one, because "the user implied by context" is
not a record. Every domain id is a UUIDv7, so records born on different machines by different
people can coexist in one view without collision. No event stream or projection assumes a single
owner.

**Agents have no identity of their own.** They act as the owner's git and `gh` identity, and the
work is authored by the human: no bot accounts, no `Co-Authored-By` trailers. The audit trail
lives in Hall9k, not in commit cosmetics. Personas (reviewer, implementer, fix) are roles, which
are prompt templates and tool policies, not accounts.

A **connection** is an external account this install can reach: provider, account, and a
*reference* to where the credential lives (`env:`, `keychain:`, `file:`). The secret itself never
reaches an event payload. Projects bind to a connection rather than to "the machine's GitHub".

Jira is a **read** connection plus an agent-mediated pen. The platform never authors a card,
because issue types, required fields, and routing rules are the organisation's configuration:
`h9k task push-to-jira` dispatches a session into the project's own repository where its
card-authoring skills live, and that session finishes by calling `h9k task link-jira`, which
reads the key back through the connection before recording anything. Agent-facing commands are
observation gates: an agent's claim is an argument that gets checked, never a fact that gets
accepted.

Depth: [PLAN.md §6.2, §6.6, §10](../PLAN.md), Decisions Log #65.
