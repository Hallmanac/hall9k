# Concepts

The layer under the [README](../README.md): what the moving parts are, what the words on the
board mean, and why the pipeline stops where it stops. This is an on-ramp, not a specification.
Where a section ends with a pointer, that pointer is the real reference and this page is the
summary that gets you there knowing what you are looking at.

- [The shape of the system](#the-shape-of-the-system)
- [The judgment layer: templates and skills](#the-judgment-layer-templates-and-skills)
- [Ideas and tasks](#ideas-and-tasks)
- [The task lifecycle](#the-task-lifecycle)
- [The three surfaces: state, phase, attention](#the-three-surfaces-state-phase-attention)
- [Dependencies and true closeout](#dependencies-and-true-closeout)
- [Runs](#runs)
- [Leases](#leases)
- [Verification gates](#verification-gates)
- [The pre-PR review loop](#the-pre-pr-review-loop)
- [Closeout](#closeout)
- [Catching a node up](#catching-a-node-up)
- [The orchestrator's presence](#the-orchestrators-presence)
- [The orchestrator feed](#the-orchestrator-feed)
- [The feed courier](#the-feed-courier)
- [Owners, nodes, and connections](#owners-nodes-and-connections)
- [Identity, fleet, and team](#identity-fleet-and-team)
- [Replication scopes](#replication-scopes)

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

There is no socket and no local HTTP API between the CLI and the daemon. **Inside one node, the
database is the bus.** The CLI writes to Postgres and rings a `NOTIFY` doorbell; the daemon listens,
and also polls on an interval because a doorbell is not a delivery guarantee. Anything the CLI
writes is durable whether or not a daemon is listening, which is why a stopped daemon costs latency
and never correctness.

Between nodes there is a second store, and it is git. A registered project's own remote carries a
ledger under `refs/hall9k/ledger/*` (identity, membership, one record per task, and who holds it)
and a message outbox per node under `refs/hall9k/messages/<node-id>`. The daemon on each node
writes to its own outbox and reads everyone else's, and every project-scoped event replicates over
those outboxes, so a person's several machines (their **fleet**) and a project's several people
(its **team**) converge without a server. Nothing on the database side is shared: each node keeps
its own Postgres, and the ledger is what lets two of them agree. See [Identity, fleet, and
team](#identity-fleet-and-team).

Above all of it sits the **orchestrator window**: an interactive Claude Code session acting as
the conversational surface over `h9k`. It is stateless and disposable, because every fact lives
in Postgres. [ORCHESTRATOR-WINDOW.md](../ORCHESTRATOR-WINDOW.md) documents that role in full — a
dispatched headless session never loads it, only an interactive one, which is the role an
interactive session in this repository is in.

Architecture in depth: [PLAN.md §6](../PLAN.md).

## The judgment layer: templates and skills

**Hall9k is prescriptive about the lifecycle and permissive about judgment.** The lifecycle — how
a task moves, what gates a merge, who is authorized to push, when a run parks for a human — is
fixed in code and [AGENTS.md](../AGENTS.md); nothing there or in a dispatched session's own prompt
gets to reinterpret it. Judgment — the norms an agent session reads to decide *how* to act inside
that lifecycle — lives in the template and skill layers instead: a prompt builder's own judgment
prose ships as markdown template files (`~/.hall9k/templates`, published and overridable exactly as
`~/.hall9k/skills` already is) rather than a C# string literal, and a repo or home skill is the
identical discipline for a workflow instead of a prompt section. The parsed contracts a template's
own prose is never allowed to carry a literal copy of — a finding's line grammar, a verdict's
vocabulary, a handoff's marker — stay fixed in code for the same reason the lifecycle does: an
operator's edit to one of those would silently break the daemon's own parsing, not merely read
differently to the agent.

`.claude/templates/` is a sibling of `.claude/skills/`, one subdirectory per prompt builder, never
merged into the skill set: `h9k install` publishes it to the identical canonical-directory,
content-hash, publish/retire/override discipline (`~/.hall9k/templates`), but a template is never
seeded into a project home's `skills/` or `.claude/skills` adapter, never carries a `SKILL.md`, and
never appears in the skill list a work prompt renders — publishing one anywhere a harness enumerates
skills would add its description to every session's first turn, which is out of bounds (PLAN.md §16
#175). `PromptTemplates` (`Hall9k.Domain.Infrastructure.Storage`) is the one
mechanism a builder, the CLI, the daemon, and the test suite all use to find one: this checkout's own
source copy first, the install's own canonical copy otherwise — the builder assembles the prompt
from templates itself, whichever process hosts it, and hands a session the finished string, never a
path into either copy.

## Ideas and tasks

An **idea** is anything entering the funnel, captured with no ceremony: one command, one
argument, a project optional. It undergoes **discovery**, which answers "what is this?", and it
owns a workspace directory at `~/.hall9k/ideas/<idea-id>/workspace` where research notes,
gathered files, and prototypes accumulate. The event stream records milestones only; file
contents stay on disk.

A **task** is an idea with intent. There is no single graduation ceremony: `h9k task add
--from-idea <id> --objective "…"` cuts one draft task from an idea through the ordinary add door,
and it fans out into as many tasks as discovery produces — invoked repeatedly, one idea yields
many. Each cut needs its own objective (several tasks fanned out from one idea cannot share its
first sentence), the idea's whole note and its workspace pointer ride along as agent context
automatically, and provenance is recorded in both directions: the task names the idea, and the
idea's own stream names every task cut from it (`h9k idea show` lists the whole fan-out with each
task's current state). Cutting a task never ends the idea — discovery may keep producing — so an
idea reaches one of its two terminal states only by an explicit, separate human act: **concluded**
(`h9k idea conclude <id> --reason "…"`, discovery produced something) or **archived** (`h9k idea archive
<id> --reason "…"`, it did not). `h9k idea promote` survives as sugar over cutting exactly one task with
the note's first sentence as the objective and concluding in the same breath, for the common case
where a single idea deserved a single task and nothing more is coming.

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
h9k task revise       ->  Draft       objective / criteria / context / type / model / BlockedBy / epic
h9k task publish      ->  Published   the readiness gate; immutable; assignable, NOT claimable
h9k task assign       ->  Queued      every dependency at true closeout
                      or  Blocked     at least one is not
h9k task unassign     ->  Published   refused while a lease is held
h9k task draft        ->  Draft       refused from Queued/Blocked onward (unassign first)
```

The edit-after-the-fact path is therefore `unassign → draft → revise → publish → assign`, each
step an explicit act. Revision is Draft-only because every later state carries a promise that
editing would break: Published promises the task satisfies the contract and may be assigned at
any moment, and an assigned task promises a node may read it at any moment. The one exception is
`--queue-first`/`--clear-queue-first`: a scheduling fact rather than part of the readiness
contract, so a call that names only it is let through in any live state except Abandoned —
Queued, Blocked, a currently Claimed task (for its next turn in the queue), even a Done one (for
the follow-up run a later reopen might dispatch) — see [PLAN.md Decisions Log #127](../PLAN.md).

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

**State** is the lifecycle in nine words: `Draft`, `Published`, `Working`, `Delivered`, `Waiting`,
`HeldElsewhere`, `Done`, `Failed`, `Archived`. `Queued` and `Blocked` both render as `Published`,
with the difference moved onto the row's facts line, and the persisted `Abandoned` renders as
`Archived`. `Delivered` means pushed with the merge not yet observed. `Waiting` is a pr-review
task whose posted review is waiting on its author (PLAN.md #160). `HeldElsewhere` is a
Claimed task another node currently holds (idea 202383dc, M2a); see [Identity, fleet, and
team](#identity-fleet-and-team) for the holder mechanics and `h9k task take` as the lever.
**`Done` renders only at true closeout**, which is the same bar the dependency rule uses, so the
board and the blocker rule agree on the word.

**Phase** is the line under a live row, and it is where the run vocabulary lives: `Dispatched`,
`Running`, `Verifying`, `UnderReview`, `ReviewParked`, `LaunchHeld`, `AwaitingReview`, `ChecksFailing`,
`ReviewPending`, `Conflicting`, `CloseoutParked`. It is derived, never stored, and it is composed from the run's
records **plus an observation of the recorded process**. A phase never claims a session is doing
something without seeing the process: a session on another node reads as "liveness not observed
here" rather than as either answer.

The two parks in that list are the ones to keep straight, because they take different levers.
`ReviewParked` is a run stopped before its pull request exists and `CloseoutParked` is one stopped
after, so `h9k task list --state ReviewParked` reaches exactly the runs `h9k review resolve`
answers, and `--state CloseoutParked` the ones `h9k pr resolve` does.

`LaunchHeld` is different from either park: it means the *node*, not this run's own work, is
what's waiting. A session that exits at once with no work done — one turn, zero tokens,
sub-second, the shape of an expired credential or an unreachable API rather than a real failure —
raises a node-wide hold that stops the dispatcher claiming and makes every in-place error retry on
that node wait on the hold too, instead of failing a queue of tasks one by one. `h9k status` names
the cause and the fix on a NEEDS YOU line while the hold stands; a probe on a doubling backoff
relaunches the oldest held run to test whether the node works again, and once one relaunch
actually records tokens, the hold clears and every run it held resumes in place with no human
lever needed. See [PLAN.md §16](../PLAN.md), Decisions Log #174.

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

### Stacked pull requests

One dependency edge behaves differently, and only when a human explicitly says so.
`h9k task add --stacked-on <parent>` (or `h9k task revise --stacked-on`, `--clear-stacked-on`)
declares the task **stacked on** that blocker rather than merely blocked by it. The tool never
infers a stack from an ordinary `--blocked-by`: the edge is reserved for slices of one feature
that are genuinely cohesive, and the declaration is always the human's.

What the edge changes, and nothing else does:

- **It dispatches at the parent's `Delivered`, not its merge.** Pull request open, internal review
  done, branch settled — that is everything a stacked child needs. A parent whose pull request was
  *closed* without merging never delivered anything, however long its URL stays on the task: the
  child stays blocked and reads as needing you, the same way any dead blocker does.
- **Its branch is cut from the parent's branch head**, not from the base branch, and **its pull
  request opens against the parent's branch**, which is what forms the stack on GitHub. One
  exception, and it is the mainline race rather than an edge case: the child dispatches at the
  parent's `Delivered`, so the parent can merge — and its closeout deletes its branch — while the
  child is still building. A pull request cannot open against a branch that is gone, so the child's
  opens against the project's base instead, which is exactly where the retarget below would have
  put it. The run still records the parent's branch as its base, because the branch still physically
  carries the parent's commits and the replay that drops them is still owed; closeout observes the
  merged parent on its next sweep and dispatches it. An origin that could not be *read* is not a
  branch that is gone: the base stands as recorded, and the run fails honestly rather than moving a
  pull request off a live parent on a guess.
- **Its diff, review packet, self-review hunt and end-of-work recompose are all computed against
  the parent's branch**, so its reviewers read the child's own delta rather than the parent's
  already-reviewed work alongside it. Every one of those ranges names the recorded fork point as a
  *commit*, not the parent branch as a ref: a parent that force-pushes a review lap mid-session
  moves that ref, and a recompose reset to the resulting merge base would rewrite the parent's
  commits as the child's own history. The review prompts name it for the same reason and one more —
  the range a reviewer reads is also the range that decides in-scope from out-of-scope, so a ref
  moved out from under it has parent-owned defects graded as this pull request's own and fixed on
  the child's branch. Where nothing was ever recorded, they fall back to the parent's branch rather
  than inventing a boundary.
- **It catches up to its parent's head at two checkpoints, and never on every push.** A parent that
  reached `Delivered` keeps moving: its own closeout reopens it for unresolved threads and failing
  checks, and each follow-up pushes again. A child that rebased on every one of those would spend
  its run chasing a branch instead of building on it, so it catches up at two points chosen for
  what reads the tree next — immediately before its own first review cycle, so its reviewers read a
  true delta, and immediately before the mandatory final full pass, so nothing is pushed on a stale
  base — and nowhere in between. Each catch-up is *mechanical*: the same `git rebase --onto` replay
  the merged-parent case below uses, between the same two exact commits, followed by the full
  build/test gate over the moved tip and **no review cycle** — the commits are the ones already
  written, on a new base. It spends the child's own rebase budget, the same one the replays below
  spend, and past that cap it parks for a human. What is *never* used is a plain merge-base
  `git rebase origin/<parent>`, for the same reason the boundary below is a recorded commit: against
  a force-pushed parent it replays the child's copies of the parent's commits against the parent's
  new ones. A conflict is not something a mechanical catch-up resolves — the branch is restored to
  its own tip and the run parks with the command it tried. Where a sweep cannot observe the parent
  at all and GitHub reports the child conflicting anyway, the judgment session that follow-up
  dispatches is told the same rule: replay from the recorded fork point with `git rebase --onto`,
  and dispute rather than guess when no fork point was ever recorded. For the same reason, every
  fixup-and-autosquash instruction a stacked child's follow-up carries names an observed commit too,
  never `origin/<parent>` — a fold against a force-pushed parent would rewrite its already-reviewed
  commits as the child's own authored history.
- **It is not at the merge bar until it is retargeted.** The board never tells you "the merge is
  yours" about a pull request aimed at its parent's branch, and a pre-approved one is not
  auto-merged either.
- **When the parent merges, the daemon retargets the child onto the base branch** and dispatches a
  *mechanical replay*: one `git rebase --onto`, between two exact commits — the boundary everything
  at or before which belongs to the parent, and the freshly observed commit it lands on. That
  boundary drops the parent's now-duplicated commits (the project rebase-merges, so they land on
  the base under new shas). It is the parent's own head where the child's branch still contains it
  (observed directly), and the fork point the child's run recorded at its cut where it does not —
  never a `git merge-base`, because a force-pushed parent rewrites the shared history and merge-base
  then gives the wrong answer. The recorded fork point is trusted only once git confirms the branch
  actually contains it: a replay that was dispatched but never landed leaves a record naming a
  commit the branch never reached, and replaying from there would carry the parent's own work as
  this task's. That reads as unobserved — the next sweep asks again — rather than as a boundary. The
  same check runs wherever else that record is re-asserted: a later run that resumes the branch
  carries the fork point forward only while the branch still contains it, and records none at all
  when it does not, so no follow-up prompt is handed a boundary nothing observed. Nothing moves the pull request's base unless the replay is actually
  going to be dispatched: if the child is out of budget or otherwise owed a park, it parks with the
  stack left intact rather than aimed at the base with the replay undone. The
  replay runs the gates and triggers **no review cycle** — nothing new entered the branch, so there
  is nothing for a reviewer to have an opinion about. Where the repository deletes head branches on
  merge, GitHub has usually moved the base already — it retargets every open child itself when it
  deletes the parent's branch — and the sweep records what it found rather than claiming a move it
  did not make; `h9k task show` reads that account back. Closeout leaves the parent's deletion on
  origin to GitHub for exactly this reason, because a raw ref deletion closes the children instead
  of retargeting them ([docs/operations.md](operations.md#one-setting-that-lives-on-the-repository-not-in-hall9k)).
- **A parent branch that moves without merging dispatches the same replay** onto its new head,
  bounded by the child's own rebase budget (`MaxStackReplayRuns`). Past that cap the child parks
  for a human, which is the honest signal that the two branches are not converging. Ordinarily that
  move is a review lap folding fixes into its own commits and force-pushing, but a commit merely
  appended since the child was cut is the same observation and gets the same replay — so the
  recorded account says the head moved, never that it was rewritten, which nothing here observed.
- **A parent that dies parks the child, wherever the child is.** Abandoned, ended `Failed`, or Done
  having never delivered a pull request that can merge (its own closed unmerged, or it never opened
  one) — its branch is one nothing further arrives on, so a child mid-run parks before its next
  checkpoint rebase and a child with a pull request open parks instead of being retargeted. Either
  way the park names which door the parent took and leaves the branch exactly as it was, because
  what happens next is a decision: put the parent back on its feet (`h9k task retry` for one that
  ended `Failed`, `h9k pr resolve` for one whose pull request closed unmerged) and hand the child
  back, or abandon the child with its parent. What no resolve does is move a run onto a different
  base — the base a run watches is frozen when it is dispatched and a claimed task's stacked edge
  cannot be revised, so work that belongs on the project's base continues as a fresh, unstacked
  task rather than in this run. It is the same "is this blocker dead" rule the board already uses
  to hold a dependent visibly rather than unblock it silently, asked one bar lower for a stacked
  edge.
- **A parent that merged somewhere other than the base branch parks the child**, base untouched.
  That takes a human: the platform's own merge bar never merges a pull request still aimed at its
  parent's branch. There is no base the child can be moved onto mechanically from there without
  dropping the work its parent merged into, and ordering a stack three levels deep is not something
  this slice does (see [scope.md](scope.md#stacked-pull-requests)).

A plain `--blocked-by` task behaves exactly as it always has.

#### Standing on a pull request another install owns

`h9k task add --stacked-on-pull-request <number>` (or `h9k task revise --stacked-on-pull-request`)
declares the same edge against a pull request rather than against a task in this install's records.
It is what a reviewer on her own node needs: she is stacking her Playwright tests on a teammate's
pull request, her node never held that teammate's run, so the parent never reaches `Delivered` here
and nothing local could ever release her task. Nothing is required to exist for the parent — no
task, no mirror, no issue.

Everything above stays true, with three differences:

- **The pull request being *open* is the parent's `Delivered`.** That is the whole bar. A pull
  request the repository does not have yet — a number declared before the teammate opened it — is
  ordinary waiting, not a problem to hand anybody; a pull request that *closed without merging* is
  the dead parent, and the child reads as needing you with the situation named. That reading holds
  even for a child the platform had already released: until it actually dispatches, a parent that
  stops being open takes the release back, because a run cut then would sit on the base branch
  carrying none of the parent's work. A pull request
  already merged when the child first dispatches leaves nothing to stack on, so the child is an
  ordinary task on the base branch, which is exactly where the retarget would have put it. The
  earlier-start checkpoints above are for local parents only.
- **The state is read from GitHub on the closeout watcher's own cadence**, by one sweep, once per
  tick, for every *assigned* stacked child this owner has that is still waiting, running, or under
  review. A child still sitting in Draft or Published is assigned to nobody, so nothing looks at its
  parent and nothing would dispatch it if it did — `h9k task assign` is what starts the watch, and
  the claim doors and `h9k task show` say so rather than telling you to wait. Everything downstream
  — the release, the branch a fresh cut starts from, the retarget, the replay, `h9k task show` —
  reads what that sweep recorded rather than asking GitHub again, so a dispatch never waits on a
  network call and two readers can never disagree inside one sweep. The practical consequence for a
  human: the board can be a few minutes behind the browser, so every line about the parent is
  labelled as an observation, with when that reading was taken. An unchanged look records nothing,
  so that timestamp is when the parent last *moved*, not when it was last looked at. A look that
  *failed* records nothing either — the child keeps whatever was last actually seen, and the next
  sweep asks again.
- **No `--blocked-by` edge travels with it**, because there is no local task to name. The hold is a
  second, independent one beside the unmet-dependency set.

The merge, the force-push and the death are all the same operations as above, driven by that
observation instead of by a local closeout event: the merge retargets the child onto the base
branch and dispatches the same mechanical replay, a head that moved without merging dispatches the
replay alone onto the new head, both spend the same rebase budget and park past the same cap, and a
pull request that closed unmerged parks the child with the situation named.

When the pull request itself names an issue or tracker item it closes, `h9k task show` prints it —
and names a local task carrying that same reference if one happens to exist. Nothing requires one
to: the edge is declared by pull request number precisely because not every repository the team
works in tracks its backlog in GitHub issues.

Depth: Decisions Log #144, #146, #153.

## Runs

A **run** is one attempt at a task. It carries its own event stream, its own worktree, its own
branch (`task/<id>-<slug>` by default, or whatever the project's own `--branch-template` renders,
cut from the base branch with `--no-track`), and its own directory
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

One task can have several runs: a retry after a failure, a follow-up dispatched onto an open
pull request, and, after another node forcibly takes the task, a run on that node that resumes the
first one's branch. The run the takeover left behind is stopped and recorded as superseded by
takeover, a kind of `Killed` and never `Failed`, with its transcript kept; and if the branch it was
working on never left the machine that held it, the new run starts clean from the base branch
rather than pretending to resume (see [Identity, fleet, and team](#identity-fleet-and-team)).
`h9k task show` lists them all with their outcomes, and `h9k logs <task>` renders
the newest by default, with `--run` for an earlier one.

Depth: [TASK-MODEL.md §3](../TASK-MODEL.md), [PLAN.md §6.3](../PLAN.md).

## Leases

Claiming is lease-based, and the lease tracks **daemon responsibility**, not agent progress.
Agents know nothing about leases.

The claim itself is a `TaskClaimed` event appended with optimistic concurrency on the stream, so
two claimants racing produce one winner and one concurrency exception. The stream version is the
lock; there is no claim table and no advisory lock. That is multi-daemon-safe against one database
from the first day.

Across nodes, the lease is not what decides who has a task. The task's record in the project ledger
names a **holder**, that record is the truth about who has the task, and a node writes itself in
before it claims (see [Identity, fleet, and team](#identity-fleet-and-team)). The lease below is how
a holding node keeps its own claim honest against its own daemon restarting, and everything described
here holds unchanged within one node.

Each task carries a **generation counter**, a fencing token. Every claim increments it, every run
records the generation it was dispatched under, and any state change arriving from a
stale-generation run is discarded. Correctness therefore does not depend on timing.

Daemon startup runs a fixed order, which is what makes a restart safe: **adopt** live recorded
runs first (reattaching, refreshing heartbeats, and processing anything that completed while the
daemon was down), then **sweep** expired leases back to the queue, then **claim** new work,
killing any superseded prior-generation process before redispatching. The net effect on one node
is that a healthy agent is never killed by lease mechanics. A run adopted mid-verification-gate is
the one case where adoption does not simply reattach: if the gate the old daemon started is still
alive, that process tree is ended (`ProcessManagerBase.TerminateTree`) before the pipeline resumes
and re-runs the gate from the start — a gate the old daemon left running is ended, never raced with
a freshly spawned one, which is what closes off a stale process still holding the gate's own log
file locked out from under the new attempt.

Depth: [PLAN.md §6.2](../PLAN.md), Decisions Log #7, #12, #29, #69, #70.

## Verification gates

Before any review happens, the run's own gates run in its worktree: whatever the project
configures with `h9k project set --verify "name=command"`. For this repository that is
`dotnet build` and `dotnet test`. A gate failure fails the run with the failing gate and its
output recorded, fails the task with it, and releases the lease. Nothing automatic follows: the
task is `Failed` and waiting on one of the three human exits. One mandatory gate is the exception:
the full-scope gate that runs immediately before the pull request settles gets one narrow repair
lap inside the same run — no task reopen — when the run's branch was rebased onto its base
immediately beforehand and the gate fails on that real rebase; only a repair round that cannot
make the gate pass parks the run for a human, rather than failing it the way the ordinary
fail-hard contract described here otherwise would (PLAN.md §16, Decisions Log
#173).

Every headless session runs these gates in the foreground and never with a background tool
(`run_in_background`, `Monitor`, `ScheduleWakeup`) still pending when its turn ends — the session's
process is killed the instant it finishes, so a backgrounded gate is left waiting on a
notification that never arrives, and the daemon tears down a completed session's own process tree
before the next gate or session touches the same worktree (PLAN.md §16 #167).

One `--verify` gate can be marked **host-coupled** (`h9k project set --verify-gate-filter
"name=filter"`) for tests that reach outside the process itself — git, the process table, the
toolchain, Docker — and so collide when several gates run in parallel on one machine. A
host-coupled gate runs only at a run's first verification and its final full pass; every
intermediate review-cycle pass in between skips it outright, recorded as such rather than as an
instantaneous pass, so `h9k task show`'s Gates cell reads "skipped (host-coupled)" for that pass.
When it does run, it is serialized against every other run's own host-coupled gate on the same
node — at most one runs at a time — and a run waiting its turn reports that wait as its own phase
(`h9k task show` says "waiting for the host-coupled gate slot"), never as a failure. A dispatched
session is never the one running it: every gate list a session's own prompt shows names a
host-coupled gate as the daemon's own serialized gate rather than printing its bare command, with
one narrow exception — a single touched test class, scoped by name — so a session never races the
daemon's own serialized slot for the same permits by hand-running the gate's full command itself.

Depth: PLAN.md §16, Decisions Log #225.

A session also never generates host load to reproduce or prove a flaky or timing-dependent test:
no parallel copies of a suite or test, no stress or spin loops, no deliberate memory pressure, no
CPU pinning. The host also runs the daemon, Postgres, and other sessions, so loading it to chase
one test starves all of them. A flake is reproduced deterministically instead — a fake, controlled
scheduling, or an injected delay — with the gate then run once, in the foreground, the same as any
other gate; a flake that will not reproduce deterministically is left best-effort and said so
plainly in the handoff, rather than proven at the host's expense (PLAN.md §16
#169).

Each gate is also validated once, at `h9k project set --verify` time, against a clean checkout of
the project's own base branch — a gate that cannot pass there refuses the whole `project set`
outright (`--accept-broken-gate` records it anyway, with a loud warning). A run that later fails a
gate races a fresh comparison against that same clean base against a short recording budget (one
daemon poll sweep) rather than waiting on it unconditionally: the recorded failure reason says the
gate also fails on clean base when the comparison answers within that budget — in practice, a
cached verdict from an earlier run against the identical base commit — and reports the bare
failure otherwise, so a real regression in the run's own branch is never held from a human behind
however long a freshly run comparison takes. A failed host-coupled gate never gets this
comparison at all: the comparison would spawn the identical gate command a second time, outside
the node's own serialized host-coupled slot, so the recorded failure reason says plainly that the
comparison was skipped because it would run a second host-coupled suite outside the node's
serialized host gate, rather than paying for an answer nobody could trust anyway.

Gates are deterministic and cheap to trust, which is why they come first. Everything after them
is judgment.

A `dotnet test`-shaped gate inside a fix cycle is narrowed to the tests reachable from that
cycle's own touched commits, via an injected `--filter`, whenever the diff's touched files can
all be mapped to test classes with confidence; anything the resolver cannot read or map falls
back to the full suite. The run's very first gate pass and the mandatory `FinalFullPass` cycle
immediately before the pull request always run the whole suite regardless, so nothing merges on
scoped green alone. Each `dotnet test`-shaped gate's own log (`verify-{gate name}.log`, so a gate
named `test` writes `verify-test.log`) opens with a `# hall9k test gate:` header recording which
mode actually ran and why.

## The pre-PR review loop

A passing gate does not open a pull request. The daemon dispatches **independent review agents**
over the run's diff against the base branch: separate headless sessions with fresh context, never
the session that wrote the code. Role separation is what makes the gate worth anything.

Only cycle 1 pays full discovery: it runs one pass per **lens**, dispatched together:

- **Conformance** asks whether the work meets its objective, its acceptance criteria, and repo
  doctrine.
- **Adversarial** assumes the code is wrong somewhere and hunts defect classes, without ever
  being told what the work was supposed to do.

A middle cycle instead dispatches one **Verify** pass standing in for whichever lenses are still
active, handed the prior cycle's own merged findings and fix summary rather than the whole diff
again, so it can confirm the fix and check its blast radius instead of rediscovering the diff from
a blank slate. Immediately before the run may settle, one mandatory **FinalFullPass** cycle runs
both lenses fresh — whether or not a track had already gone dormant — so nothing reaches the
remote on delta-green alone; a track it reawakens with a genuine new finding is recorded
reactivated rather than left stuck at an earlier conclusion, and a run that converges clean at
cycle 1 pays no extra pass at all. "Fresh" is context, not diff range: a FinalFullPass whose run
already paid for an earlier full-scope read reads only the commits since that read's own head,
falling back to the whole branch when no such boundary is on record or it no longer resolves
against HEAD — every full-scope read still starts where the previous one left off, so no commit
ever reaches the remote unread at full scope by a fresh context, only reread by fewer of them.
Which shape a cycle ran under — Discovery, Verify, or FinalFullPass — is a deterministic engine
decision recorded on the run stream, and both Verify and the mandatory FinalFullPass resolve their
own configurable model (`--model-review-verify` and `--model-review-finalpass`, each independently
defaulting to whatever the plain Review model resolves to), separately from the Review model
Discovery keeps using and from each other — Verify's confirm-the-fix-and-check-blast-radius job is
deliberately the cheapest to run at a lighter model, while the mandatory FinalFullPass is the
expensive full-branch read (43 percent of all review input tokens per the 2026-09-01 architecture
review's own measurement) that a different install might point at a different model to measure.

Reawakening a track deliberately gives it a fresh per-track cycle budget, measured from the cycle
it was reawakened at rather than the run's absolute cycle count, so it gets a genuine chance to fix
what the mandatory pass found. That relaxation means the per-track cap alone cannot bound a track
the mandatory pass keeps reawakening cycle after cycle, so the mandatory pass carries its own
separate round cap: however many times it has run for this run, hitting that count without ever
settling parks the run for a human, the same way a capped track does.

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
its own. The mandatory FinalFullPass immediately before the pull request opens tightens that same
in-scope bar to High alone: a medium there rides along exactly as a low already does elsewhere,
carried onto the pull request as a residual rather than earning a fix-and-reverify cycle of its
own. A track that
concludes goes dormant and is never reawakened by the other track's fix sessions. **Differing
cycle counts on one run are the design, not a fault** (a clean conformance track can be dormant
at cycle 2 while adversarial is still working at cycle 5).

When a cycle needs fixes, **one** fix session runs in the same worktree with the merged findings
of every live track, the gates re-run — scoped to the fix's own touched tests when the resolver
can map them with confidence, full otherwise — and a fresh set of reviewers looks again. A
finding the fix session **disputes** (not a defect, human territory, wrongly graded) parks the
run immediately with both positions written to disk, rather than looping on judgment. That park
is what `h9k review resolve` answers.

A per-track cap and the mandatory pass's own round cap are not the only way a run reaches
`h9k review resolve`. A task also carries a **lifetime review-cycle budget**
(`LifetimeReviewCycleBudget`, default 20) that counts every review cycle the task has ever spent,
across every run and every follow-up it has had — a stranding, a `task retry`, and a `pr resolve`
follow-up all spend from the same odometer, and nothing resets it, unlike the per-track caps a
`ReviewParkResolved` verdict does reset. Checked at every settle point, so it can park a run that
just converged cleanly with nothing left to fix. Because the settle point is the only place it is
checked, `h9k review resolve --needs-fixes` on that park earns exactly one more cycle before the
run re-parks there again, not a reprieve from the budget itself; raising the budget for this task
alone is `h9k task set-review-caps`. The per-track caps and this lifetime budget are each
settable at three levels with the same override order — task, then project, then node, then the
compiled default — so a node operator can lower the defaults platform-wide, a project can loosen
or tighten them for its own review culture, and a task can override either, live, even while its
run is already in progress.

Scope routes findings rather than ranking them. An out-of-scope high (a pre-existing defect on
the base branch) is fixed here in its own commit; an out-of-scope non-high becomes a **draft bug
task** that is inert until a human publishes it — a medium mints one of its own exactly as
before, while a low folds into the project's one standing sweep draft instead (Decisions Log
#117): a repeat of the same file and the same stated line updates that item's evidence rather than
duplicating it, so eight one-line pre-existing defects cost one build-gate-review pipeline rather
than eight. A human grooms and publishes the sweep once it is fat enough; the moment it
publishes, the next routed low finding starts a fresh one.

A loop that reaches a verdict on its own always reaches MergeReady, but the *settlement* records
how it was reached: Clean means a reviewer read the final tip and found nothing, Settled means the
severity gate, routing, or a human's resolution ended it, with the residuals recorded. A settled
ending never reads like a clean one.

**A loop can also end without ever reaching a verdict at all.** Between passes, the engine checks
whether the pull request already merged — a human can merge a follow-up's PR while its own review
loop is still dispatching passes, since nothing about the loop blocks a human's own merge — and if
so, stops there rather than keep reviewing a branch that can never ship any differently. This ending
carries neither a verdict nor a settlement; `h9k task show` reports it as its own outcome, distinct
from both Clean and Settled. If the run's worktree carried commits the merge itself never included,
the platform saves the patch and the commit list to disk before closing the run out and routes a
**re-land draft task** naming them, inert until a human publishes it, the same as a draft bug task.

**Fresh context does not mean no memory (Decisions Log #88).** Each cycle's reviewers are new
sessions with no memory of the task's earlier cycles, but the prompt they are handed carries
forward what a human already settled on this task: every prior `h9k review resolve` verdict and
reason, so a finding a human already dismissed with evidence, or one they confirmed as a real
defect, is not re-raised or re-litigated as though the question were new. The project's own
doctrine (AGENTS.md/CLAUDE.md, and any decisions log it documents) rides along the same way, so a
deviation already ratified there reads as a deliberate choice rather than an oversight the
reviewer just caught. A thread-dispute park (Decisions Log #62) is the one park that plays no
part in this: it settles a disputed comment thread before any reviewer ever reads the diff, not a
review finding, so its resolution is not carried forward as a settled ruling. A human's own
`h9k review fixed` rides the same surface, with the two shapes told apart: a fix with commits
settles nothing (they are in the diff, and checking them is the point), while a `--no-change`
entry's reason is read as a dismissal exactly as a `--merge-ready` reason is. A cycle is never
both: `h9k review fixed` refuses `--no-change` over a branch tip it watched move, so a dismissal
is never recorded over commits the next pass is reading.

**A human at the wheel can take the fix role herself (Decisions Log #148).** On an
interactive-mode task, the review-verdict-to-fix boundary has four choices rather than three:
`h9k review proceed` dispatches the fix session the verdict asked for, `h9k review resolve
--needs-fixes "<redirect>"` dispatches one carrying their redirect instead, `h9k review resolve
--merge-ready` overrules the finding outright, and `h9k review fixed` records that the human did
the fix by hand. The last one dispatches nothing: those commits are already on the branch, so the loop
re-enters at the same fix-to-re-review boundary a completed fix session lands on — the gates run
over those commits, that boundary asks for their go, and then a fresh review pass reads them
scoped to the parked cycle's own head, exactly as a fix session's would have been. It spends no
automatic fix budget and no cap a fix session consumes, while the review cycle it opens counts
exactly as one a fix session opens does. The same lever works at a closeout-side fix park, on a
follow-up reopened by a changes-requested review or by failing checks; there the branch is
already published, so it pushes the fix before the reviewers and the pull request's own checks
read it.

**A repeat fix round over the same findings escalates to the review role's model (Decisions Log
#90).** When a fix session dispatches over substantially the same findings an earlier fix round
already tried — the same location an automated pass keeps returning, or a human's own
`--needs-fixes` reason restating it — that fix session runs on the review role's
model instead of the fix role's, so the observed dodge-and-redo failure mode (a weaker model
sidestepping a defect rather than fixing it) gets a stronger model exactly where it recurs. This
only changes anything when the two roles actually resolve to different models: a default install
that has never set `--model-review`/`--model-fix`, or a task overriding both the same way,
resolves them identically, and a repeated round there dispatches on the ordinary fix model exactly
as it would have anyway. De-escalation is automatic the moment a later round moves on to a
genuinely different finding, with no separate reset step. `h9k task show` prints a "Fix
escalation" line while the newest run's most recent fix dispatch escalated this way.

**The fix session ends with a mandatory self-check phase before it hands back.** Scaled down from
the build session's own adversarial self-review to a single pass rather than a loop, it runs once
every finding is fixed or disputed: for each finding it fixed, it sweeps for every other site
sharing the same defect shape and fixes or clears each one inside the branch's own changes, states
what the replaced code did that the new code no longer does and confirms the difference is
intended, and runs the touched tests in the foreground, waiting for them to finish, before it
concludes — so a half-applied fix or a fix-introduced regression is caught by its own author
instead of costing a separate verify lap.

Depth: [TASK-MODEL.md §3.1](../TASK-MODEL.md), Decisions Log #24, #59, #62, #63, #88, #90, #92, #93, #113.

## Closeout

`PullRequestOpened` starts a phase, not an epilogue. The daemon opens the pull request (agents
never do, and there is deliberately no create-pr skill), and then the closeout monitor polls it
on a gentle interval through `gh`. Each node watches the runs it executed, because run provenance
is the only honest owner once the task itself is lease-free.

Per poll, in priority order:

- **Merged.** The run completes, dependents unblock, the retained worktree is removed, and the
  branch is deleted locally and in remote-tracking refs. The remote deletion itself is skipped
  where the repository's own `delete_branch_on_merge` setting already owns it (checked via `gh`
  before anything is deleted) — closeout leaves that deletion to GitHub, because a raw ref
  deletion there closes a stacked child instead of retargeting it
  ([docs/operations.md](operations.md#one-setting-that-lives-on-the-repository-not-in-hall9k)).
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
- **A human reviewer's changes-requested review on the current head.** Checked ahead of the
  thread branch below, because a changes-requested review ordinarily opens threads too and the
  narrower fact is the one that matters. The review's own body and every inline comment are
  carried onto the reopen as findings — each with the file and line the comment had, and the
  thread a reply would land inside, including a comment written as a *reply* inside a thread an
  earlier review opened, which is how a second look most often arrives — and the fix lap is handed
  them rather than sent to rediscover them. What earns this its own lap is what happens on disagreement: the session posts nothing and
  resolves nothing, and instead parks with the reviewer's point, its own reasoning, and a **proposed
  reply the implementer sends, edits, or drops** (`h9k review resolve --post-reply-as-written` /
  `--post-reply "<text>"` / `--post-nothing`). No agent and no orchestrator ever posts a
  disagreement to a person. When the lap pushes, that reviewer's review is re-requested on the new
  head. A **bot's** changes-requested review is deliberately not this: Copilot's findings stay on
  the automated thread path below, which argues and resolves on its own.
- **Unresolved review threads, from any reviewer.** A follow-up run is dispatched with the
  resolve-review-threads prompt. Copilot is one reviewer among many: a teammate's thread and the
  author's own self-review note count and dispatch identically. This is the path for every review
  that carries no changes-requested verdict — a reviewer whose only review is a comment, a bot's,
  or threads left behind with no review state at all. A comment a reviewer leaves *after* asking
  for changes is not one of those: their verdict is read as the one that stands until they
  themselves approve or dismiss it, so it stays on the lap above. Before touching any code, this
  lap gives every thread exactly one disposition — fix, decline (with reproduction-grade evidence),
  or route (filed as a card) — so a wrong or already-addressed thread no longer buys a full fix
  lap. A decline or a route on a **bot's** thread gets its in-thread reply and then a resolve. On
  a thread a **person** opened it gets neither: the lap drafts the reply and parks it, and the
  owner sends it, edits it, or drops it with the same three `h9k review resolve` choices the
  changes-requested lap's disagreement park takes. Answering a question counts as a decline, so a
  question a person asked is drafted and parked too. The thread stays open and unanswered until
  they decide, and a lap can legitimately push nothing at all. The decline rate is recorded per
  thread on the run stream. Three things back the rule up: every in-thread reply routes through
  `h9k pr reply`, which refuses a decline or a route into a person's thread and records the
  attempt; the `gh` routes into a thread are refused by the session's own PreToolUse guard, on
  either shell; and the park itself is read off the lap's own triage, so a lap that declined a
  person's thread and then closed as if it were finished parks anyway, with a blank draft for the
  owner to fill in or drop.
  One thread never buys this lap in the first place — an unresolved thread a person opened that
  asks nothing, beside a review of theirs that asks nothing ("nice, my own PR needs this too").
  A thread that reports a defect asks something even in plain indicative form ("this throws when
  the list is empty"), so it dispatches like any other request. The exception is reported on the
  phase line as a human thread that asks nothing rather than dispatched, and it still holds a
  pre-approved merge exactly as any other unresolved thread does.
- **An errored Copilot review.** Re-requested exactly once through the provider's API, because an
  errored review produces zero threads and thread count alone would read as a clean pass.

Two silences are worth knowing about. GitHub hides an unsubmitted (`PENDING`) review's comments
from the API entirely, so a pull request can look quiet while feedback is being written: never
read silence as "the reviewer had nothing to say". And the monitor does not act on *checks* while
CI is still reporting — an incomplete picture would hand a follow-up a partial failure list, so
the pending-checks read short-circuits ahead of the failing-checks branch — even though it still
records what it saw of Copilot's review state on the run stream that same sweep; which is why the
quiet phase line names how long a check has been pending rather than claiming a clean pull
request. **Review feedback is not held behind that read** (Decisions Log #164,
Brian's ruling: a broken CI may be what the review found, so the fix lap must be allowed to run):
unresolved threads or a changes-requested review dispatch their lap whatever the checks are doing,
and a failing check seen on the same sweep rides in that one lap's prompt rather than buying a
second. A pending check still holds the *merge* either way.

Automatic follow-ups are bounded by two counters, not one. A **progress cap**
(`MaxCloseoutLapsPerObstruction`, default 2) counts consecutive laps spent on the *same*
obstruction — the same failing check, the same set of unresolved thread ids, the same
changes-requested review url — and resets the
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

**The platform never merges — unless a task opts in.** That is the last human checkpoint by
default, and it stays one, with one deliberate exception: a task published or later set
`h9k task publish --pre-approved` / `h9k task set-pre-approved <id> on` removes the owner as a
synchronous gate at its own pull request. For that task alone, once every obstruction above has
already had its say — no conflict, no pending or failing check, no unresolved thread, no errored
review — the daemon reads GitHub's own review decision, outstanding requested reviewers (Copilot
handled on its own bounded settle window), and thread resolution, and rebase-merges the moment
every one of them reads satisfied, with no agent session in the loop. A required human approval or
an outstanding reviewer is a visible, self-resuming wait, never a park; every other human waypoint
(Failed, a review park, a cap trip) still stops it exactly as it would an unflagged task. Nothing
about this changes the merge's own meaning: it is still the platform's one true-closeout moment.

**Pre-approval has three values, and the third one waits for a person.** `off` is the default.
`on` is what the paragraph above describes. `after-human-review` is the same automatic merge with
two more gates in front of it: at least one human reviewer must have been requested on the pull
request at some point, and every requested reviewer must have approved the current head. It exists
because plain pre-approval can merge before anybody has been asked to look at all, so a task can
now start pre-approved and still wait for whichever reviewers you end up adding. With nobody
requested it simply waits, and the board says so, naming you as the one who adds a reviewer or
flips the mode; flipping it to `on` is the emergency path and merges on the next sweep. Nothing in
hall9k names a reviewer and nothing in hall9k requests a review — reviewers are GitHub's business
and a human's — so what the platform stores is only your standing instruction about when to merge.

**The people a pull request is waiting on are named.** Wherever the last closeout observation
recorded logins, `h9k status` and `h9k task show` say whose review the merge is waiting on rather
than "waiting on human approval": the outstanding requested reviewers, whoever requested changes,
and — under `after-human-review` — which requested reviewers have not approved the current head.
On a task that is not pre-approved, the same reading changes the Delivered line from "the merge is
yours" to `awaiting review from <logins>` while any of that is true, and back again once none of it
is. Where nothing was recorded — a branch rule wanting an approval nobody has been asked for, or a
verdict whose author the last observation did not record — the line says the fact without inventing
a login for it, or a reason for the login's absence.

Depth: [TASK-MODEL.md §2.2](../TASK-MODEL.md), Decisions Log #18, #22, #62, #80, #81, #135, #150.

## Catching a node up

Nodes replicate their event streams to each other through the project's own ledger repository. The
ordinary case is live: a node queues the project-scoped events it appends and its peers apply them
on their next message sweep. Everything below is about the other case, history a node does not
have, and the point is that a node's own state decides which of three mechanisms applies to it.

**A brand-new node bootstraps.** The first time a node's sweep finds no applied history at all for
a project, and knows of at least one peer, it asks a ranked peer for everything: the voucher who
invited it first, then owner-role members, then any member. This is automatic, it fires once, and
the gate is strict — it means *nothing at all*, not "less than I expected". A node that has already
applied a single event, or added a task of its own, has left this state for good.

**Any node gap-fills.** When a peer's outbox has content past a numeric hole this node cannot read
across — a squash on the sender's side, a lost push — the node asks other members for that sender's
own missing history, cascading to the next ranked candidate when one declines or goes quiet. Also
automatic, and it recurs whenever the hole is noticed again.

**Any node asks for a held tail's own genesis.** A replicated event whose stream has never started
here and which is not that stream's first event is held rather than applied, and replays the moment
some later envelope happens to carry the genesis. "Happens to" was the gap: nothing asked for it,
so a tail served without its head sat held until a human noticed the task missing from the board.
Now the sweep asks. A stream whose held records have outlived one sweep interval, and which has no
local stream behind them, gets one broadcast request for itself: at most ten new asks a sweep, one
ask per stream, with a cooldown after each so a decline or an answer that arrives still missing the
genesis does not re-arm the ask on the next tick. After three tries the node stops asking and says
so; the tail is still held and still replays if the genesis ever turns up, but a genesis nobody in
the fleet can serve does not become servable by being asked for a fourth time. This is the mechanism
that lets a freed or truncated stream finish on its own.

**The nodes of one owner's fleet reconcile with each other.** The nodes of one fleet are not peers in
the ordinary sense: every one of them is supposed to hold every fleet- and team-scoped event of
each project it registers, so that any one of them can answer a new teammate's bootstrap in full.
So on every sweep, for each node of this owner's own fleet that this node has no reconcile record
for, it asks that one node for everything it holds of the project. Addressed to that node alone,
never broadcast, because only that node's answer is of any use to this one. The node on the far end
asks back the moment it reads the request, so the pair settles in both directions with nobody
typing anything, and each side's own record is what stops a third ask. The exchange is recorded per
(peer, project) and `h9k status` shows it in progress or complete with its counts; `h9k project
reconcile <project>` runs it again by hand. An exchange that goes unanswered is asked once more and
then reported, and a peer that has since left the fleet has its record closed unanswered instead,
since nothing out there will answer it and the hand command only reaches the current fleet. A
sibling this node is already bootstrapping off waits its turn: the two are never in flight with the
same peer at once, and the reconcile is asked in full once that bootstrap closes.
The origin is concrete: on 2026-09-21 the Mac held
almost none of arx-platform's history while the Windows node held all of it, and a bootstrap that
ranks one answerer had no way to notice.

**An established node pulls, or nothing happens.** A node that is none of the four above (not
brand-new, not looking at a hole, holding no stream's tail without its head, and already reconciled
with its own fleet) never asks for anything older on its own, and there is no state it can reach
that changes that. `h9k task pull <task-id>` asks every member for one stream by id; `h9k project pull <project>
--since <global-sequence|all>` asks for a whole project's history from a bound. `h9k task add
--from-issue` queues the first of those two by itself when the project's ledger names a task this
node does not hold. All three are broadcasts to the whole project rather than ranked cascades,
because a CLI command has no live trust chain or transport to rank peers from; all three queue an
envelope and stop, and the daemon's next sweep is what sends it.

**Why an explicit pull reaches further than a sweep does.** Each node records a replication
switch-on point when it first replicates anything, and nothing it appended before that point
travels on its own. That keeps a node's pre-replication back catalogue inert rather than flooding a
project the day it adopts replication — but it also means a task published before its node switched
on is unreachable by any open-ended mechanism, forever. So an ask that names what it wants, and only
such an ask, is served from below the answering node's switch-on point: one named stream, or a named
sequence bound. An ordinary flush and a gap-fill name nothing and both still stop there.
Naming is the rule rather than a human's hand being on it, which matters because the held-tail ask
above names one stream and comes from the sweep: it asks a peer to complete history that already
partly arrived here, which is the case the switch-on point was never meant to strand, and it can
reach exactly that one stream and nothing else. A fleet sibling's reconcile comes from the sweep too
and names a bound of zero, which is the whole of the answering node's own log. What earns it that
reach is not the naming on its own but who is asking: another node of the same fleet is entitled to
everything this one holds for the project, and withholding it is what split the fleet in the first
place. What never bends, however specific the ask or however closely related the asker: a
currently-private task
or idea is never served, and no node is ever handed its own history back. Answers apply by origin
event id, so a pull, or a reconcile, over streams a node already holds changes nothing.

**The other automatic ask that reaches just as far: a brand-new node's bootstrap.** A node joining a
project holds nothing of it, asks every peer once, and has nobody to type an explicit pull on its
behalf, so the bootstrap is answered from the start of the answering node's own log too, rather
than from its switch-on point. Where the held-tail ask gets there by naming one stream, the
bootstrap gets there by being asked only once. Without it, a member who joins today receives only
what each peer appended after it switched replication on: the tail of everything older, or nothing
at all. It is one answer per new node rather than a recurring cost, which is what tells it apart
from the gap-fill: that one is minted whenever a hole is noticed, on a node that already holds the
project's recent history, and it keeps the bound. The private and own-history exclusions apply to a
bootstrap exactly as they do to everything else.

**What a pull cannot reach: a stream a node holds only the tail of.** A replicated event is
appended to the local stream, never inserted in front of what is already there. A task that was
open on its own node when that node switched replication on is exactly this shape elsewhere: the
ordinary flush shipped what happened after the switch-on point and nothing before it, so a peer
holds the tail with no `TaskAdded` under it. Applying the older half now would replay that stream
backwards and leave the task reading as it did the moment it was created, so the receiving node
refuses those events instead, and `h9k task pull` says so up front rather than queueing an ask that
can only be refused on arrival. The full history stays readable on the node that produced it. A
node holding one of those partial streams cannot pass it on either, in a bootstrap answer or any
other: nothing says which project a stream with no genesis under it belongs to, and the platform
forwards nothing on a guess, so a member joining this week receives the whole streams and none of
the partial ones.

What ends that wait is the daemon's own startup repair. It finds every local stream whose first
event is a replicated copy that is not its aggregate's genesis, across tasks, ideas, epics and
runs, reading the events rather than the documents they projected. Each such stream is put back the
way it would have been had this node never received anything for it: every replicated event is
held, the partially-applied documents and the dedupe rows go, and the stream id itself is released
so a real genesis can start it. A native event this node's own dispatcher appended onto the
phantom, along with the task lease a claim of it left behind, is dropped rather than held: replaying
a claim of a run that never started would only leave the task stuck again with nothing alive to
conclude it. Once the repair has run, `h9k task pull` reads the stream as absent and asks, and the
held tail replays in origin order behind the answer. A stream the repair cannot explain safely is
left exactly as it is and named in the daemon log with the reason.

**A pull brings the whole story, not just the stream you named.** An ask for a task's stream is
answered with that task's own run streams too, each whole — a run's stream id is not derivable from
its task's, so an answer carrying the task alone lands a finished task reading as Delivered with no
laps and no sessions, which is what happened to task 3727884f on 2026-09-21. And a task that lands
naming blocked-by or stacked-on ids whose streams are not here has each of those asked for
automatically, one request per missing dependency and the graph walked again as each one arrives,
so `h9k task assign` is never refused for a dependency the platform could have fetched.

`h9k status` shows every outstanding request while it stands, whichever mechanism minted it, and
names whose dependency an automatic one is fetching. A broadcast closes when a member answers it,
or when one says it holds nothing that matches — which means a re-run of the same pull asks again
rather than reporting an ask that already came back. A request genuinely still in flight is
reported as such, and `h9k task pull --again` is what closes it out and replaces it.

A request a decline closed inside the last day is listed too, naming the node that declined it and
when, so an ask that was refused reads differently from one still in flight. Under both, the pane
counts how many streams this node is holding the tail of and how many of those it has stopped
asking about. Every decline is recorded on the request by node and time, including the ones from
members that answered after the first decline had already closed it: one peer saying it holds
nothing reads very differently from all of them saying it.

Depth: [scope.md](scope.md), Decisions Log #236 and #260.

## The orchestrator's presence

An orchestrator window is a live process on one machine, and nothing else on the platform could
answer "is one actually up right now" until it started saying so itself. A window registers
(`h9k orchestrator register --project <name> --session <name> --pid <pid>`) as the first step of
its own start-up, and deregisters (`h9k orchestrator deregister`) when it closes or restarts —
the launch anchor both recipes generate runs both calls automatically, so an operator never types
either by hand. `h9k orchestrator status` and the `h9k status` header both print the identical
sentence: the live window's session name, agent CLI, process id and age, or "none live" with when
one was last shut down or lost.

**Liveness is checked against the process table, not trusted from the record.** A registration
says a window declared itself live at some point; the daemon's own presence sweep, and both CLI
surfaces on demand, ask the operating system whether that process id is still the one running —
so a terminal closed without a clean deregister reads as gone within one sweep interval rather
than forever. Checked by process id alone, which is why this works for any vendor's agent CLI, not
only Claude Code.

**Registering a second live window for the same project is refused by default**, naming the one
already registered; `--replace` takes over from it deliberately. Two windows driving one board
both dispatch against the same queue and both drain the same feed, and only the operator knows
whether the second terminal is the one they meant to use.

Presence is a fact about one machine: it never replicates to another node, the same way a process
table itself never could. It is what the feed courier below asks before ever spawning — a courier
that found nobody to deliver to would just be another kind of noise.

Depth: [PLAN.md §16](../PLAN.md), Decisions Log #237.

## The orchestrator feed

The three surfaces above answer "where does everything stand right now". The feed answers the
other question an orchestrator window opens with: **what happened while nobody was watching.**

`h9k orchestrator feed --project <name>` prints it — every event past this project's own cursor
that the project's interest filter admits, oldest first, grouped by task, each as one plain line
the feed's own table of event type to wording composes for it. `--drain` advances the cursor after
printing, so a window's start-up step takes what is new and leaves the next one a clean slate;
without it the same items come back, which is what makes a plain read safe to repeat. `--since
<time>` reads history from a point in time (`45m`, `6h`, `3d`, `2w`, or an instant) and never
touches the cursor, so a second window catching up cannot consume what the first has not read.

**It is not a second store.** An item is an event that is already on this node's own log, past a
per-project cursor, that a filter admits — so a sweep costs one query, nothing is buffered
anywhere, and nothing is lost by not reading the feed. There is exactly one truth about what
happened, and the feed is a way of reading it.

**The filter is one deterministic table from event type to level**, with no model anywhere in it:
the same event always lands in the same band on every node. Three nested bands, each a superset
of the one before, set per project with
`h9k project set <name> --orchestrator-feed actionable|transitions|everything`:

| Level | What it carries |
|---|---|
| `actionable` | Only what somebody is owed: parks and disputes, gate and run failures, a merge that stays failed, daemon trouble, and any message from a person or another node's window. |
| `transitions` *(default)* | Everything `actionable` carries, plus the work's own movement: task state changes (published, working, delivered, done, failed, abandoned), ideas logged or updated, and claims or takeovers involving another node. |
| `everything` | Everything `transitions` carries, plus the machinery's own movement: a run's phase changes. |

A message from a person is admitted at **every** level, including the narrowest — a colleague or
another node's window asking something is never filtered out by a reading preference. The
daemon's own machine traffic (the JSON its claim reactors post to each other) is not a message
from a person and never appears, the same exclusion `h9k messages` already applies. A note's own
feed line is the one that opens with an id, because it is also the one the line cannot finish: the
body is quoted to a hundred and sixty characters, and the id in front of it is what
`h9k message show <id>` takes to print the rest. Every other line is about a task the group
heading already names.

An event type the table does not name is not in the feed at all. That is deliberate: the feed is
what an orchestrator would want to know, not a mirror of the log, so a new event type ships
silent and adding it is a decision somebody makes.

Depth: [PLAN.md §16](../PLAN.md), Decisions Log #241.

## The feed courier

Presence answers whether a window is up; the feed answers what it missed. Neither one, on its
own, gets the news to a window that is already open — an orchestrator still had to think to ask.
The feed courier is what closes that loop: the daemon spawns a short-lived, cheap-model agent
that delivers a project's undrained feed items into its live orchestrator session and exits, so
no window ever has to poll for what happened.

**Four conditions gate every spawn**, checked once per project on a short sweep: the feed has
undrained items at that project's own level, an orchestrator is live for it on this node (the
presence section above), no courier for that project is already running, and a batching wait has
elapsed since the last one. The wait is what turns a burst of activity into one delivery instead
of many: it is zero once the feed has been quiet for ten minutes, and ramps up toward a ceiling —
`h9k project set <name> --courier-max-wait`, sixty seconds by default — the more recently
something new has landed. A park, a dispute, daemon trouble, or a message from a person
dispatches at once regardless of that wait; a per-day spawn cap (five hundred by default) is the
backstop against a genuine storm even of those. A manual `h9k orchestrator feed --drain` holds a
short lease on the project's own cursor while it runs, and the courier never spawns into that
window.

**The courier's own prompt is deliberately small** — no recipe, no AGENTS.md, none of the
repository context an ordinary dispatch carries. It is the feed items exactly as `--drain` itself
would print them, plus one instruction: address the orchestrator's own registered session by name
through Claude Code's cross-session mesh (the `SendMessage` tool every headless session already
carries) and report back whether the send landed. Delivery is an adapter keyed off the
orchestrator's own registered CLI — Claude Code today, another vendor's CLI whenever one earns its
own adapter — and a project whose orchestrator runs under a CLI with no adapter simply keeps its
feed undrained, logged, rather than guessing at a mechanism nobody has described.

**The daemon drains the feed itself, never the courier.** The sequence to drain through is
captured before the courier is ever spawned, so an item that arrives mid-delivery is never
credited to a message that never carried it; once the courier reports delivery, the daemon
advances the cursor to that captured sequence. A session that fails, times out, or never reports
back leaves the cursor exactly where it stood, and the identical items are what the next courier
—or the next manual `--drain`— sees.

**It runs as a run with no task** — the seam a courier needed and no earlier role did, since
every other dispatched session is either a task's own build/review/fix work or, for card
publication, a task's own errand with no run of its own. A courier is neither: it belongs to a
project, not a task, so it opens its own stream and is recorded with its own model, its own
token spend, and its own outcome, folded into `h9k status`'s spend line the same as any other
role's. It is its own role in the model-by-role policy (`h9k config set --model-courier`); its
own field ships blank exactly like every other role's ("ask the project or platform default"),
but its *resolution* is the one deliberate exception — a blank courier bottoms out at
`claude-sonnet-5`, cheap by construction, rather than falling all the way through to the platform
default the way every other role's blank does.

Depth: [PLAN.md §16](../PLAN.md), Decisions Log #245.

## Owners, nodes, and connections

**Every node belongs to a human.** Not to an agent, not to a service. Whatever autonomy agents
gain, a person is responsible for every run their fleet performs, and the chain is queryable: this
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

Jira is a read-and-write connection with a compose/execute write path. The platform never authors a
card's *content*, because issue types, required fields, and routing rules are the organisation's
configuration: `h9k task push-to-jira` dispatches a session into the project's own repository
where its card-authoring skills live, and that session composes a payload but makes no Jira call
itself. It submits the payload through `h9k task write-jira`, which is the sole executor of every
Jira write (Decisions Log #102, #114): hall9k validates the payload, records the intent before
anything is sent, executes it against the Jira Cloud REST API with the same registered credential
the read side uses, and verifies by reading the item back.
Agent-facing commands are observation gates: `write-jira` and `h9k task link-jira` (for recording
a pre-existing card) both read the key back through Jira before recording anything, so an agent's
or an operator's claim is an argument that gets checked, never a fact that gets accepted.

**An assignment names an owner, not a node.** `h9k task assign <id> <owner>` puts the task in the
queue every node of that owner's fleet reads from (Decisions Log #34), and the first free dispatcher
in that fleet claims it, wherever it runs, exactly as if only one node existed. `--node
<id-or-fragment>` narrows that to one specific node of the owner's own fleet: only that node's
dispatcher claims it, and every other node of the fleet skips it, logging why once a sweep
rather than silently. Placement is advisory to dispatch alone — it never changes whose work the
task is, and it never touches the ledger holder a claim writes; it only narrows *which* node of the
fleet gets to claim it. The owner's fleet is the owner's own root node, the one whose
key established it in this project's ledger, plus every node currently vouched into it — the root
never needs `h9k node vouch` against itself, since the ledger already names its own node without
one. A node outside both sets is refused outright (`h9k node vouch` first), and `--node` with
nothing named clears an existing placement, handing the choice back to whichever node gets there
first.
Placement and the takeover levers agree by construction (idea 202383dc, items 4 and 5): a forced
`h9k task take --force` or a cooperative grant that moves an already-*placed* task rewrites the
placement to the taker's own node in the same event, so the node that just lost the task stops
trying to reclaim it without a second `h9k task assign --node`. A never-placed task stays unplaced
through either lever, keeping the automatic cross-node recovery it had before placement existed.

GitHub gets a write path of its own, because an issue's shape (title, body, labels) is uniform
enough for the platform to author deterministically, with no agent needed. A project's **backlog
policy** (`h9k project set --backlog none|github-issues|jira`) decides how every published task is
tracked: `github-issues` has `h9k task publish` run `gh issue create` itself and record the result
through `h9k task link-issue`, the same observation-gate pattern `link-jira` uses; `jira` makes the
same publication request `push-to-jira` does. Either policy gates the publish itself first: a
draft with no linked item yet, and no publication already pending, is refused until a human or
orchestrator links what a search of the tracker found, attests none exists with
`h9k task publish <id> --no-existing-item`, or attests that this task should skip tracking
altogether with `h9k task publish <id> --untracked` — for internal chores and platform tasks that
should not pollute a team's tracker — so the platform never mints a duplicate from a search it
cannot perform itself. The two attestations say opposite things and are refused together as
contradictory; `--untracked` on a project with backlog policy `none`, or any policy this build
doesn't recognize, is refused as meaningless; and `--untracked` on a task with a publication
request already outstanding (`h9k task push-to-jira`, run by hand while still a Draft) is refused
too, since that session mints its card regardless of the flag.

Depth: [PLAN.md §6.2, §6.6, §10](../PLAN.md), Decisions Log #65, #95, #96, #97.

## Identity, fleet, and team

Everything above describes one node, one machine with its own database. Two facts push past that.
A person usually has more than one machine, and a project usually has more than one person. This
section is how Hall9k stays one system across both, with no server in the middle: each node proves
who it is with a signing key, the people and machines a project trusts are recorded in files on the
project's own git remote, and a task always has exactly one node that holds it.

Four words carry the whole model, and this page uses each in exactly one sense:

- A **node** is one machine's install: one `h9k`, one `h9kd`, one database.
- An **owner** is the human a node belongs to. An owner's identity is one **root**, defined below.
- An owner's **fleet** is the owner's own nodes, and only those. When the docs say fleet they mean
  "the machines of one person", never "everybody on the project".
- A project's **team** is its members, each of whom brings their own fleet.

**Every node has a signing key.** The first time a command needs to sign something, usually
`h9k project add` running `h9k project join` behind it, the node generates an ed25519 key with
`ssh-keygen` under `~/.hall9k/keys/<node-id>/id_ed25519` and never generates another. The private
key file is readable by your account alone (mode 0600 on macOS and Linux), it is never written to a
project's ledger or to an event, and it is a secret of the same class as `credentials/`. What the
platform publishes is the public half, and a **fingerprint** of it: the lowercase hex SHA-256 of the
key, sixty-four characters, chosen over OpenSSH's own fingerprint format because that format's `/`
and `+` cannot appear in a git ref name and this one has to. Every write a node makes to the
project's ledger is a git commit signed with that key, so any other node can check who wrote it.

**An owner is one root.** The first node to join a project that has no owner yet establishes a
**root**: it writes `owners/<fingerprint>/root.yaml`, using its own key, and that fingerprint
becomes the owner id everywhere in Hall9k, which is why `h9k owner show` prints a long hex string
as the owner's id and `--to owner:<fingerprint>` takes the same string. The node whose key
established the root is the owner's **root node**. The root has no separate vouch entry, since it is
what vouches for everything else, so it counts as a member of the fleet without one.

**The fleet is the root node plus every node vouched into it.** Adding a second machine of your own
is a **vouch**: `h9k node vouch <node-id>`, run on a node already in the fleet, writes the new
node's id and public key into `owners/<root>/nodes/<node-id>.yaml` on every non-archived project you
are registered to. `h9k node revoke <node-id>` writes `owners/<root>/revoked/<node-id>.yaml`
instead, and whichever of the two came latest, in the order of the ref's own commits, wins, so
vouching again undoes a revocation made by mistake. Only a node that is itself currently in the
fleet may vouch or revoke, and the command refuses before it pushes anything when this one is not.
Because trust is recomputed at every read rather than remembered, a revocation reaches every other
node the next time it reads the ledger, and it also voids every membership write the revoked node
ever signed, until a later vouch of the same node restores them.

**A project's members have one of two roles.** Membership is one file per person, at
`members/<root-fingerprint>.yaml` on `refs/hall9k/ledger/members`, and the role in it is `owner` or
`member`, with nothing in between. The first join on a project writes the genesis entry, and it is
unconditionally an owner. An owner-role member may mint member invites, remove a member
(`h9k project member remove <project> <fingerprint>`, which deletes the file rather than marking it),
and can never remove the last owner. `h9k project members <project>` lists what the ledger shows
right now, recomputed on every run rather than cached: each root fingerprint, the login this install
knows for it when there is one, the role, that root's fleet, and whether it verified. A member who
is not an owner can do everything a member's own work needs and cannot change who else is on the
team. A project also has its own generated **project key** (a twenty-six-character ULID written into
the genesis entry, deliberately not derived from anyone's fingerprint), and a project whose ledger
predates that key gets one, once, from `h9k project assign-key`.

**Invites are how anyone new is admitted, and the secret never touches the ledger.** There are two.
`h9k node invite` is for another machine of yours: it prints a secret once and records only the
secret's hash, in `owners/<root>/invites/<invite-id>.yaml` on every non-archived project you belong
to. `h9k project invite <project> [--role owner|member]` is for another person: the same, in one
project, refused unless your root is an owner there, and the new member's role is `member` unless
you say otherwise. Both expire after 72 hours by default (`h9k config set --invite-expiry-hours`)
and both are single use. The person on the other end runs `h9k project join <project> --invite
<secret>`, which writes an HMAC of the secret and their own key fingerprint into their own node file
as proof that they hold the secret, and then nobody has to do anything else: the daemon on the node
that minted the invite notices the proof on its next invite sweep (every twenty seconds by default),
vouches the node in or adds the member, and marks the invite spent. A newcomer who registers a
project somebody else already owns does not need to know any of this up front. `h9k project add`
registers it locally, writes nothing to the remote, names the owner, and asks for an invite,
straight away in a terminal or by printing the exact command to run once you have one.

**GitHub confirms the person, and the ledger never records the account.** Registration reads the
GitHub account `gh` is signed in as, and `h9k project add` refuses when there is none, because
a Jira connection tracks cards and says nothing about who may write to a repository. Joining
additionally checks, before it generates a key or writes a byte, that the account can push to the
project's repository, since a node cannot write a ledger ref it has no push access to. The same
round trip records this install's own role and, when that role includes push, the repository's
collaborators, as read-only observations in the local database. Hall9k never calls a GitHub endpoint
that would change a permission. None of this is a signed claim that a fingerprint belongs to a
GitHub account: the trust between people is the invite, and GitHub is what tells this node that the
person running it is allowed to touch the repository at all.

**The ledger is a set of refs on the project's own remote.** Nothing here needs a Hall9k server,
because your git host already carries the project. Hall9k adds refs under `refs/hall9k/` to the
same `origin` your branches live on. They are not branches, nothing merges them, an ordinary `git
clone` or `git fetch` does not bring them down, and every one of them is signed, plain text, and
readable by anyone who can read the repository:

| Ref | What it holds | Who writes it |
|---|---|---|
| `refs/hall9k/ledger/owners/<fingerprint>` | An owner's `root.yaml`, and under it the vouches, revocations, carried vouches, and invites | The owner's own nodes |
| `refs/hall9k/ledger/nodes/<node-id>` | One node's own file, announcing its id, its public key, and the owner it claims | That node alone |
| `refs/hall9k/ledger/members` | One file per member, with the role | Owner-role members, and the invite sweep |
| `refs/hall9k/ledger/records` | One `records/<task-id>.yaml` per task: its contract, its state, and who holds it | The node that publishes the task, and the node that holds it |
| `refs/hall9k/ledger/prompt-addenda` | The project's prompt addenda | The daemon only |
| `refs/hall9k/ledger/run-skill` | The project's run skill | The daemon only |
| `refs/hall9k/messages/<node-id>` | One node's outbox: numbered message envelopes | That node alone, one writer per outbox |

Messages are how nodes talk to each other, and events ride them. `h9k message send` queues a note
addressed to `node:<node-id>`, to `owner:<fingerprint>`, or to `project`; the daemon's message sweep
pushes it to the sender's outbox ref on a jittered cadence (15 to 25 seconds while there is
something to send or read, 30 to 45 when idle, both adjustable with `h9k config set
--message-poll-*`), and every other node reads the outboxes it is entitled to on its own sweep.
A receiver accepts a message only from a sender whose key is currently trusted for that exact node
id in this project, and records what it ignored. Messages are signed and not encrypted, so a
`fleet`-scoped item's text is addressed to your fleet but readable on the remote by anyone who can
read the repository (see [Replication scopes](#replication-scopes)). A sender rewrites its own outbox
as a fresh commit that drops what it already sent beyond the retention window (48 hours by default,
`Hall9k__MessageRetention`), which is why a node that was away longer asks for its missing history
instead of reading it, as [Catching a node up](#catching-a-node-up) describes. This is also why "the
database is the bus" is true only inside one node: across nodes, the bus is these refs.

**Trust is recomputed on every read.** A node never remembers who is trusted; it reads the owner
refs and the members ref and replays them, oldest first, accepting each vouch or membership write
only if its signer was already trusted when the replay reached it. A stranger who pushes a
self-consistent root and node file to the repository is simply not a member, so everything they
wrote is ignored, recorded rather than silently dropped, and reported: `h9k project members` prints
an "Unverifiable writes ignored" block and `h9k status` names each one. The one thing this cannot
protect against is a force-push over a ledger ref by someone with push access, which rewrites the
history the replay reads. Git gives push access no finer lock than the ref itself.

**A node can carry a vouch into a project the root has never touched.** A node already vouched under
your root on one project can join a brand-new project's ledger with no `--owner` and no `--invite`:
`h9k project join <new-project> [--from-project <source>]` writes the root file and a bundle
carrying the source vouch and the signed commits behind it, which every other node verifies offline
without fetching the source project. It is refused when no source vouch exists, when the node's key
is revoked on the source, or when the new project already has a root for that owner.

**A task has one holder, and the ledger record says who.** Once a task is published, its record on
`refs/hall9k/ledger/records` carries a **holder**: the node currently responsible for it. A node
writes itself into the record before it claims the task, and if that write fails no run launches
(a fetch, push, or signing failure holds the claim and it retries on the next sweep). The holder is
cleared at true closeout, on `h9k task abandon`, when the holder's own run lease expires, when the
holder grants a take request, and by `h9k task release` in the window after a task's work is
delivered but before its pull request merges. The holder is the truth about who has a task; the lease
described under [Leases](#leases) is how the holding node keeps its own claim honest, not how two
nodes decide between themselves. The write that sets a holder is conditional on the value the
writer just read, so two nodes racing for one task never both win, and the loser is told who did.
From any other node the task reads as `HeldElsewhere` in the Status column and in `h9k task show`,
which names the holding node and how long it has held the task, and `--state HeldElsewhere` or
`--state attention-heldelsewhere` selects them. The other node's dispatcher leaves the task alone,
and a task still waiting in the queue whose holder is another node says so in its facts line until
the holder clears. `h9k status` only counts held-elsewhere tasks in its header, since nothing about
them is asked of you.

**Asking for a task is `take`, and the holder's project decides how it answers.** `h9k task take
<id> --reason "..."` asks the holder. A task nobody holds has nothing to negotiate and is
claimed through the ordinary lock on this node's next dispatch sweep; a task this node already
holds says so; and a task another node holds gets a claim request sent to that node. The
project's take policy (`h9k project set <project> --take-policy auto|ask`) says how it answers.
Under `auto`, the default, the holder's node grants at once when no run of that task is live there,
releasing the holder and reassigning the task to the requester's owner so the requester's dispatch
claims it, and refuses, naming when the live run started, when one is. Under `ask` the request is
parked for the holder's person, who answers with `h9k task grant <id>`, or with `h9k task refuse <id>
--reason "..."` which tells the requester why. `grant` is refused while a run of the task is live on
the holder's node, under either policy, and names `h9k run kill` as the way to stop it first. A
request nobody answers within the project's take timeout (30 minutes, `h9k project set <project>
--take-timeout`) is not resolved for you: nothing expires, and the requester's `h9k status` and
`h9k task show` change their wording to name `--force` as the way on. Both sides see the request in
`h9k status` while it stands.

**Forcing it is a person's judgment about a node that has gone quiet.** `h9k task take <id> --force
--reason "..."` overrides the holder unilaterally, requires that your own root hold the owner role in
the project, and prints the evidence it has (who holds the task, since when, and how far this node
has read that node's outbox and as of when) before it acts, because Hall9k cannot tell "offline"
from "slow" and does not pretend to. It only works on a task that carries a ledger holder: an
interactive claim (`h9k task work`) never writes one. Like the ledger write it makes, it is
conditional, so if two people force the same task at once the loser is told who got there first. If
the previous holder still has a live run, it is stopped the next time the takeover reaches that node
and recorded as superseded by takeover, never as a failure, with its transcript kept and no pull
request action following from it. The new holder resumes the old run's branch only if that run
pushed it. A branch that never left the other machine exists nowhere else, so the next run starts
clean from the base branch with none of that work in it.

**A handoff note travels with the task.** `h9k task handoff <id> --text "..."` (or `--file`), run on
the node that holds the task, leaves a note for whoever holds it next: what is done, what is half
done, what to watch. It is written into the task's record, shown by `h9k task show`, delivered as a
nudge (a message, never the note itself) to the project or, with `--to`, to one owner's fleet, and put
ahead of the agent's own context in the first run that resumes the task's branch on a new node. It
is capped at four thousand characters, and is for work still in flight, unlike the closeout handoff
a merge gives a dependent task.

**Placement chooses among the nodes of your fleet, and it never moves ownership.** `h9k task assign <id>
[owner] --node <node>` narrows a task to one node of the owner's fleet: only that node's dispatcher
claims it, and the others stand down without a forced take. A node outside the fleet is refused (vouch
it first), and a bare `--node` clears the placement. A forced take or a grant that moves a placed task
rewrites the placement to the new holder in the same step.

**Scope decides how far an idea's or task's events travel.** `private` never leaves the node,
`fleet` reaches every node you run, and `team` reaches every member's fleet and cannot be narrowed
again. See [Replication scopes](#replication-scopes) for the rules.

**What a first session looks like.** On a fresh second machine you run `h9k install`, then `h9k
project add --name demo --repo-url <url>`. Because the ledger already names an owner, registration
stops at the invite. On the first machine, `h9k node invite` prints a secret. Back on the second, `h9k
project join demo --invite <secret>`, and within a minute or so, once the first machine's daemon
sweep has matched the proof, the new node is vouched into your fleet and `h9k project members demo`
on either machine shows it. Adding a colleague is the same with `h9k project invite demo` instead, and they become a member
with their own root.

Depth: `h9k decide list` carries the decisions behind idea 202383dc, the distributed-team chain, and
[The distributed team](cli.md#the-distributed-team-identity-fleet-and-holding) in cli.md is the
command reference.

## Replication scopes

Every idea and every task carries a **replication scope**: how far its own events travel across
the fleet of nodes an owner runs and the team of members a project has.

- **Private** — never leaves this node. The old `set-private on` flag, still available as an
  alias.
- **Fleet** — addressed to that owner's own root identity the same way an invite proof already is,
  so no OTHER project member's node ever applies it: it never appears on another member's board,
  feed, or status, and it is this scope's own delivery and application guarantee, not a
  confidentiality one. This is the resting scope for a fresh idea or a fresh draft: an owner can
  work something alone or within their own fleet before it is ready for the team. On a project
  whose ledger is a shared git repository, the envelope itself is still a signed, unencrypted blob
  on that shared repository's own `refs/hall9k/messages/<node>` ref (`GitLedgerMessageTransport`
  signs, it does not encrypt) — every project member CAN read a fleet item's own text straight off
  that ref with plain git plumbing, even though no build here ever applies or displays it to them
  (independent pre-PR review, cycle 3, conformance lens, medium).
- **Team** — reaches every project member's own fleet. One-way: once a scope reaches team, no
  command can narrow it back down, because another member may already hold a copy and there is no
  message that un-sends what they already have.

**Defaults.** A newly captured idea and a freshly added draft task both start at fleet scope.
Publishing a task (`h9k task publish`) always sets team scope, unconditionally — a published task
is the door a task reaches the team through with no separate command needed. An idea has no such
automatic door: it reaches the team only on the explicit word below. An item that existed before
this feature keeps the effective scope it already had — team if it was already shared (not
marked private), private if it was.

**Setting it directly.** `h9k idea scope <id> <private|fleet|team>` and `h9k task scope <id>
<private|fleet|team>` set the scope as a recorded event naming who and when, refused if the item
is already at that scope or already team and asked to go narrower.

**Sharing.** `h9k idea share <id>` and `h9k task share <id>` are sugar for setting team scope —
the one door onto team an idea has, and the door that lets a task's draft reach the team before it
is ready to publish (useful when a draft needs a teammate's eyes for approval). Both work on an
item in any state, a draft included.

A scope change re-sends the item's whole history at its new, wider scope, so a teammate who was
never addressed before receives it whole rather than only whatever happens from that point on.

Depth: [PLAN.md §16](../PLAN.md) (idea 202383dc's own replication scaffolding; idea 8c5993c5, Decisions Log #240).
