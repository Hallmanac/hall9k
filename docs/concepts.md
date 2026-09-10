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
in Postgres. [ORCHESTRATOR-WINDOW.md](../ORCHESTRATOR-WINDOW.md) documents that role in full — a
dispatched headless session never loads it, only an interactive one, which is the role an
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
  is nothing for a reviewer to have an opinion about.
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

Every headless session runs these gates in the foreground and never with a background tool
(`run_in_background`, `Monitor`, `ScheduleWakeup`) still pending when its turn ends — the session's
process is killed the instant it finishes, so a backgrounded gate is left waiting on a
notification that never arrives, and the daemon tears down a completed session's own process tree
before the next gate or session touches the same worktree (PLAN.md §16 #167).

A session also never generates host load to reproduce or prove a flaky or timing-dependent test:
no parallel copies of a suite or test, no stress or spin loops, no deliberate memory pressure, no
CPU pinning. The host also runs the daemon, Postgres, and other sessions, so loading it to chase
one test starves all of them. A flake is reproduced deterministically instead — a fake, controlled
scheduling, or an injected delay — with the gate then run once, in the foreground, the same as any
other gate; a flake that will not reproduce deterministically is left best-effort and said so
plainly in the handoff, rather than proven at the host's expense (PLAN.md §16
#PLACEHOLDER-18b7a833).

Each gate is also validated once, at `h9k project set --verify` time, against a clean checkout of
the project's own base branch — a gate that cannot pass there refuses the whole `project set`
outright (`--accept-broken-gate` records it anyway, with a loud warning). A run that later fails a
gate which also fails on clean base says so in the recorded failure reason, rather than reporting
that same bare failure a second time — the distinction that tells "this gate was never going to
pass" apart from a real regression in the run's own branch.

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
(`LifetimeReviewCycleBudget`, default 25) that counts every review cycle the task has ever spent,
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
  lap. A decline or a route still gets an in-thread reply, but only a bot-authored thread may then
  be resolved by the agent; a human-authored one stays open for the human to close, so a lap can
  legitimately push nothing at all. The decline rate is recorded per thread on the run stream.
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
