# Scope

What works today, what is designed but unbuilt, and what the project deliberately will not do.

This page is written to be checkable. Anything under "works today" has run in anger, because
Hall9k has been building Hall9k since the pipeline first ran end to end. Anything under "designed
but unbuilt" names the file that holds the design, so you can read it rather than take this
page's word for it.

Last reconciled against the tree on 2026-08-28.

---

## Works today

### The dispatch pipeline

Draft a task, publish it through the readiness gate, assign it, and a daemon claims it under a
lease, cuts a worktree and a branch from the base branch, spawns a detached Claude Code session
with an assembled prompt, runs the project's verification gates, puts the diff through a two-lens
independent review loop, and opens a pull request. A fix cycle's own `dotnet test`-shaped gate
runs narrowed to the tests reachable from that cycle's touched commits when they can be mapped
with confidence, never on the run's first pass or the mandatory full pass immediately before the
pull request, so nothing merges on scoped green alone (PLAN.md Decisions Log #98). Then it
watches that pull request until the merge is observed, dispatching bounded follow-up runs for
failing checks, unresolved review threads, and a branch that has fallen behind and now conflicts
with its base along the way, and removing the worktree and the branch at true closeout. A branch
obstructed only by a conflict with its own base gets a mechanical fix tried first: a plain fetch +
rebase + force-push in the run's retained worktree, no agent session and no local gates, since
GitHub's own CI on the push is treated as the authoritative gate here — the follow-up run only
dispatches when that mechanical attempt genuinely fails (a real conflict, an unusable worktree, a
refused push, or a pull request retargeted to a base other than the project's own) rather than for
every conflicting branch (PLAN.md Decisions Log #131).

Two things about that loop have been hardened by incident rather than by design review: leases
survive a laptop lid closing without spawning duplicate agents, and daemon catch-up after a
restart cannot double-book a task, because a stale generation is fenced out of every task-level
write. A third: a fix session dispatched over substantially the same findings an earlier fix round
already tried escalates to the review role's model instead of the fix role's,
de-escalating automatically once a round moves on to something genuinely different — but only on
an install where the review and fix roles actually resolve to different models; a default install
that has never set `--model-review`/`--model-fix` resolves them identically, so a repeated round
there dispatches on the ordinary fix model exactly as it would have anyway (PLAN.md Decisions Log
#90).

Every gate's own wall-clock duration is recorded on the run's verification pass or failure and
shown on `h9k task show` beside the run it belongs to. The same command flags a gate whose newest
duration materially (1.5x) exceeds the project's own recent recorded average for that same gate —
compared only against other runs' passing samples at the same full/scoped classification, never a
fixed baseline — so a single run's step-change spike is caught the week it happens rather than
reconstructed later from file timestamps (origin incident 2026-09-01). The baseline is itself a
trailing average of the same series, so it climbs right along with a suite that grows a few
percent per run: that smooth multi-day drift, the origin incident's own shape, stays under this
threshold on every run of the climb and is not what this flag catches. Seeing that shape still
needs a human reading the Gates column's own raw numbers over time.

### Interactive claims

`h9k task work <id> [--direct-launch] [--acknowledge-unmet-dependencies]` lets an operator work a
Published, Queued, or Blocked task themselves instead of dispatching it headless. On a Published
task assigned to nobody, it assigns the task to the operator's own owner and claims it
interactively in one atomic event append, so the task is never observably Queued in between and the
dispatcher can never win the race to it. An unmet dependency — whether just discovered here on a
Published task, or already sitting Blocked from an ordinary `h9k task assign` or a
handed-back/retried claim — warns rather than refuses: the platform names every open blocker, and
`--acknowledge-unmet-dependencies` is the human's recorded override to claim it anyway. Not needed
twice: an acknowledgment this task already carries from an earlier claim on the same still-open
blockers is honored without asking again, and `h9k task show` names whether a claim's own
acknowledgment was fresh or carried forward. Either way, it claims the task and cuts the same
branch and worktree headless dispatch would, then — by default — prints the worktree path, the
branch, and a starting prompt assembled through the same code path a headless spawn's is (its
working rules swapped for an attached operator) for the operator to paste into a Claude Code
session started anywhere. The pasted session self-registers (`h9k task register-session`), which is
what lets the double-booking and liveness guards below recognise it; a session that never registers
degrades honestly to the same no-op every guard already had for a task nobody ever recorded a
session against. `--direct-launch` instead launches a plain interactive Claude Code session
attached to the operator's own terminal itself, the way this command always did (kept for one
release; refused on a machine where Claude Code resolves to a Windows script shim, since the prompt
cannot survive that shim's argv). The claim is held by the human rather than a process — no lease,
no heartbeat reclaim — so closing the terminal is a normal way to leave, and running
`h9k task work` again re-enters the same worktree and branch — by default with a fresh prompt, or,
under `--direct-launch`, resuming the most recently recorded session's own conversation, falling
back to a fresh session, announced rather than silent, only when the recorded one cannot be
resumed. It occupies zero concurrency slots (the run's `NodeId` is the sentinel `Guid.Empty`, which
the node's session-ceiling accounting never counts), so it starts even when the daemon's queue is
full. `h9k task verify` runs the project's gates on demand against the claim's worktree; `h9k task
deliver` pushes the branch and hands the run into the standard delivery pipeline — from there it is
indistinguishable from a headless run; `h9k task handback` releases the claim to a headless agent
partway through, resuming the same branch (`--first` marks it queue-first for the next free
dispatch slot, `--now` dispatches it immediately instead, ceiling-exempt, through
`h9k task start`'s own mechanism — refused together); `h9k task release` gives an untouched claim
back to the dispatch queue. See [PLAN.md Decisions Log #103, #122, #124, #126, #127](../PLAN.md).

### A deliberate human kick-off

`h9k task start <id>` dispatches a Published, Queued, or already-Blocked task on the spot, headless, instead of
waiting on the dispatch queue or working it interactively. It reuses `h9k task work`'s own claim
shape exactly — the same ceiling-exempt sentinel `NodeId`, so every lever built on it (`deliver`,
`verify`, `handback`, `release`, the stale-claim nudge, and re-entering with `h9k task work`
itself) accepts a start-it-mine claim on the identical terms an interactive one already gets — but
launches the agent headless and detached (`claude -p`) under the
`<task-shortid>-build` name rather than attached to the caller's terminal, and returns as soon as
the process is confirmed alive. Shares `h9k task work`'s own warn-then-acknowledge shape for an
unmet dependency, on a Published task and on an already-Blocked one alike (a task already sitting
Blocked from an ordinary `h9k task assign`, or from a handed-back/retried deliberate claim): the
platform names every open blocker, and `--acknowledge-unmet-dependencies` is the human's recorded
override to start it anyway. Not needed twice: an acknowledgment this task already carries from an
earlier claim on the same still-open blockers is honored without asking again, whichever of
`start` or `work` gave it. Refused otherwise on Draft, a pr-review task, a reopened task's
follow-up branch, and any task that already has a live claim; there is no re-entry path the way
`h9k task work` has one — a fresh claim on an already-Blocked task is exactly what the Blocked
entry is, not a re-entry. Giving the claim back (`handback`, `release`, `retry`, or `pr resolve`
reopening it) lands the task on Blocked rather than Queued when the acknowledged dependency is
still open, since only `h9k task assign` clears that snapshot — and the acknowledgment itself stays
on record for whichever command reclaims it next. See
[PLAN.md Decisions Log #125, #128](../PLAN.md).

### The board

`h9k status` composes three surfaces (lifecycle state, live phase, attention) from the underlying
records, and a phase never claims a session is doing something without observing the process.
Every needs-you cause is quoted from a record and carries the command that clears it. Browsing
lives under `h9k task list`, filterable by project and state, defaulting to live work with
archived tasks held back behind `--include-archived`, and bounded with a footer saying how many
rows were held back and how to see them, and under `h9k project list`, which counts every
project's tasks by attention bucket.

### The dependency graph

`--blocked-by` edges, enforced at assignment and re-evaluated every dispatch cycle. Cycles are
refused at publish and named hop by hop. A dependency counts as met only at true closeout. A
blocker that dies holds its dependents visibly rather than silently unblocking them, and a
blocker that recovers clears the hold on its own. The same edges route context: a run receives
what its immediate blockers handed down at their closeout, exactly one hop.

### Epics

`h9k epic add | list | show | link-jira | close` (PLAN.md Decisions Log #100). A first-class
named grouping of tasks — its own id, title, and Open/Closed state — with optional, no-ceremony
membership: a task joins or leaves at `h9k task add --epic` / `h9k task revise --epic`/
`--clear-epic`, must share the epic's project, and only an Open epic accepts new members. Closing
is always an explicit human act with a reason, never automatic, and there is no `reopen` yet. Jira
linking is identity-only, stored verbatim, and unlike a task's own `link-jira`, never read back
against Jira.

### Ideas

Capture with one command and one argument, a discovery workspace per idea, revision with full
history, assignment to a project after the fact, promotion into a draft task, and discard with
the reason kept. Provenance is recorded in both directions.

### External work items

`h9k task add --from-issue` adopts a GitHub issue; `--from-jira` adopts a Jira card. Both are
one-time snapshots that never re-check, and neither invents acceptance criteria. A project
declares a backlog policy (`h9k project set --backlog none|github-issues|jira`), and every task
published under one is tracked automatically: `github-issues` has the platform author the issue
itself, deterministically, since an issue's shape is uniform; `jira` dispatches the same
agent-mediated push a human can also run by hand with `h9k task push-to-jira`, which composes what
the card should look like in the project's own repository and submits the composed payload through
`h9k task write-jira` — hall9k's own sole executor of every Jira write (create, update, comment),
which validates it, refuses a transition or a close regardless of who composed it, executes it
against the Jira Cloud REST API with the same registered connection the read side uses, and reads
the item back to verify before recording anything. `h9k task
link-issue` / `h9k task link-jira` cover the adoption half — recording a pre-existing item, read
back through gh or the registered connection first — so an agent's claim, or the platform's own
creation call, is never taken as the recorded fact either way. An adopted task never gets a second
item created for it, and a retried or replayed Jira create narrows the window for a duplicate
rather than closing it outright: `write-jira` searches for a marker an earlier attempt's own card
would carry before creating anything new, but Jira's search index updates asynchronously, so a
retry inside that index-lag window can still find nothing even though the card genuinely exists.
`h9k task publish` refuses a draft with no linked item and no publication already pending under a
tracking policy until a human or orchestrator either links what a search of the tracker found,
attests none exists with `--no-existing-item`, or attests that the task should skip tracking
altogether with `--untracked` — the platform never searches the tracker itself. When a task
carrying an external reference merges, closeout comments the pull request on it (GitHub issue or
Jira card alike, the Jira comment going through the same `write-jira` surface) and never closes or
transitions it. A rejected credential (the registered API token revoked or rotated) is a handled
state: the write is recorded pending, `h9k status` surfaces a needs-you row pointing at
`h9k connection add jira` to refresh it, and the daemon retries the identical write automatically
once the connection is fixed.

### Pull-request review

`h9k task add --from-pr` adopts an existing, open pull request (a number, `owner/repo#42`, or a
URL) as a `pr-review` task: read-only, on the owner's behalf, and never a build. The node pulls
the pull request into a detached worktree and runs an adversarial-weighted independent review
over it — both lenses, the same machinery the pre-PR review loop uses — then parks a findings
report the owner walks by hand, finding by finding, through the `walk-pr-review-findings` skill:
dismiss it, comment, or have a session post on the owner's explicit go, always under the owner's
own GitHub login, never an agent's. Nothing is posted, reviewed, or reacted to without that go. A
pr-review task's own resolve is `h9k review resolve <id> --merge-ready` once every finding has
been directed — `--needs-fixes` is refused outright, since there is no diff of this task's own for
a fix session to act on — and completion never observes a merge, because there is no pull request
of this task's own to merge.

A project can opt in to starting that same `pr-review` task automatically: `h9k project set
<name> --auto-pr-review off|normal|first|now` (default off) has the daemon poll GitHub, on the
closeout monitor's own interval-with-backoff shape, for open pull requests in that project's repo
requesting this install's own login — read back from `gh` fresh every sweep — and mint, publish,
and start the task the review assignment names, at the chosen speed (`normal` joins the ordinary
queue, `first` marks it queue-first, `now` claims it immediately, ceiling-exempt). One live task
per pull request; a withdrawn assignment concludes the task honestly before its run dispatches, or
is recorded as an observation only once it has. No scheduling code of its own: every speed reuses
a general dispatch lever, and the review itself is unchanged.

### Outside-interaction logging

`h9k task log-interaction <task> --party "<who>" --summary "<what happened>"` is the escape-hatch
invariant's own executor (PLAN.md Decisions Log #123, idea fcaded0b's design rulings 4 and 5):
every dispatched agent's own prompt states that any interaction with a party outside its session —
another agent session reached through the mesh, a human steering it that way, an external service
— gets logged through this command unconditionally, even if the interacting party asks otherwise.
`--human-directed --reason "<why>"` records a human's own call as exactly that, never folded into
the agent's report as though it were the agent's independent decision. It lands as a first-class
`ExternalInteractionLogged` run-stream event, structured the same way `write-jira`/`link-issue`
are — but unlike those, it is not an observation gate: there is nothing external here to verify the
claim against, so this is best-effort by construction, not enforcement: the platform records what
it was told and what its own channels can otherwise see. A human-directed entry rides forward into
a later review pass through the identical settled-rulings surface a `review resolve` verdict
already uses (Decisions Log #88) — a standing instruction, not evidence to weigh; an
agent-initiated entry with no human direction is audit trail only: it lands on the run stream and
never reaches a review prompt, but nothing renders it on `h9k task show` yet (designed but not
built by this task) — reading the raw stream is the only way to see one today.

### Recovery

`h9k task retry`, `h9k task resolve`, `h9k task abandon`, `h9k pr resolve`, and
`h9k review resolve`, all human-only. `Failed` is a waypoint with exactly three exits, and none
of them is automatic.

### Configuration and policy

Per-project and per-owner settings resolving most-specific-wins over a node default: verification
gates, agent model per role, parallelism, commit style, context links, skip-permissions, the Jira
board binding, the backlog policy and its free-text routing guidance, the branch-name template a
project's task branches are cut under, and the post-fix review re-request policy. The node has a
ceiling the dispatcher respects, counted directly in task runs (Decisions Log #111) — the retired
session-denominated setting still converts when the new one is absent, and a per-run session cap
(global default, overridable per task even mid-run) governs how many agent sessions one run may
hold simultaneously. A node-level periodic token-spend budget (`h9k config set
--spend-budget`/`--spend-period`, Decisions Log #120) paces dispatch the same way: once the
current period's recorded spend, summed live from every session's own token usage rather than a
stored counter, meets the budget, the dispatcher declines to claim further queued work until the
period rolls — never killing or parking work already running. Unset means unbudgeted, and `h9k
status`/`h9k config show` show the current period's own recorded spend, by model, whether or not a
budget is set.

The review loop's four cycle caps — the conformance and adversarial per-track caps, the mandatory
final-full-pass round cap, and the task-lifetime review-cycle budget that survives run resets — are
settable the same way, at three levels apiece with the same
task-then-project-then-node-then-compiled-default order: `h9k config set` for the node default,
`h9k project set` for a per-project override, and `h9k task set-review-caps` for a per-task
override that can be changed even while the task's run is already in progress.

Which pre-PR review stages a run gets is itself a setting (`--review-stage-composition` at
`h9k config set`, `h9k project set`, and `h9k task add`/`revise`, Decisions Log #129): the full
pipeline (default), one lens only (`adversarial-only`/`conformance-only`), the mandatory final
full pass skipped (`skip-final-pass`), or no pre-PR review at all (`none`). Resolved once, at
each run's own dispatch, and frozen for that run's whole lifetime — unlike the cycle caps above,
a mid-run change reaches only the task's next run — and recorded on the run's own stream, so
`h9k task show` always answers which pipeline shape a given run actually ran under. A value that
removes a load-bearing guarantee (Decisions Log #92, or a lens's own attention budget) is refused
at set time unless acknowledged with `--accept-reduced-review`.

### Installation and release delivery

A tagged commit on `main` becomes a GitHub release carrying self-contained `h9k` and `h9kd`
binaries for macOS arm64, Windows x64, and Linux x64 (`.github/workflows/release.yml`), each
bundled with the canonical skill set and a `checksums.txt`. A bare machine with no repo checkout
and no .NET SDK bootstraps from that release directly (`scripts/install.sh` / `scripts/install.ps1`,
or the agent-followable [`docs/INSTALL.md`](INSTALL.md)): fetch via `gh`, verify the checksum, ask
consent, install, and finish with `h9k doctor`. `h9k install` publishes binaries to `~/.hall9k/bin`,
links `h9k` onto the PATH, and publishes the skill set to `~/.hall9k/skills`, either from a local
`dotnet publish` (`--repo`) or from an already-downloaded release payload (`--from-release`).
`h9k update` is the same `--from-release` path wired to `gh release download`, for a machine that
already has `h9k`. The daemon has a CLI-owned lifecycle with a strictly opt-in autostart (a
launchd LaunchAgent on macOS, a Task Scheduler logon task on Windows) that snapshots the
environment it will need and reports any tool it cannot resolve. Daemon start/stop/status run on
macOS, Windows, and Linux; autostart runs on macOS and Windows (see Windows support, below, and
Decisions Log #3 for why neither is a service).

`h9k uninstall` reverses the install without reversing the work: it stops a running daemon,
unregisters autostart, removes the PATH link, and deletes only what `h9k install` itself wrote
under `~/.hall9k` (bin/, the skill set, the Postgres compose file, the daemon's log and pid
files) — never a registered project's home, `config.json` (an operator, `h9k install` itself
when nothing was configured yet, or `h9k doctor`'s start-offer may have written it, and
uninstall keeps it regardless, since it is the reconnect path a later install needs), credentials,
or the global idea/run fallback directories, which are real work living as siblings of those
files, not the install. The
`hall9k-postgres` Docker container is only ever stopped — its data volume is never touched,
because the data lives in Docker rather than in the home this command trims, and a later
`h9k install` reconnects to it. `--purge-data` is the one path that destroys the container and its
volume too, and it names what is about to die and asks for confirmation first, refusing outright
in a non-interactive session without an explicit `--yes`.

See [PLAN.md Decisions Log #78, #83, #85](../PLAN.md).

### The project home

Every project owns a directory in one shape on every machine: a generated `AGENTS.md`, `repo/`
(bare clone, `dev/` worktree, task worktrees), `ideas/`, `tasks/`, `skills/` seeded from the
install's canonical set, and a generated `.claude/` adapter. `h9k project add` creates it and
`h9k project init` is the adopt-and-repair path; both are platform code end to end, with no agent
anywhere in the recipe, and both are idempotent. The dispatcher composes the home into every
agent briefing, so a dispatched session is told where the skills and the docs are rather than
hunting for them.

Every task owns a directory under `tasks/` (`<shortid>-<slug>/task.md` plus a `workspace/`), and
every idea with a project renders the same shape under `ideas/`; the daemon keeps them rewritten
by sweeping the store, not by watching individual events, and reconciles the directory on its own
startup (backlog 48). The render is one-way: a human edits the file and applies it back with
`h9k task revise <id> --file` (there is no `--file` form for ideas — paste the note back as the
argument), and the store, never the file, decides what happened. An idea with no project yet has
nowhere to render into, so it stays in its global discovery workspace until assigned.

A task's directory moves to `tasks/_archive/` the moment it is terminal — true closeout, or
abandoned — and moves back once it isn't any more and its current run is no longer live, so a
project's `tasks/` folder shows a human only the work still worth their attention, with every
finished task's history one click away rather than mixed in with it (backlog 51). The liveness
check is deliberate: moving a reopened task's directory back out from under a follow-up run that
is still writing into it would race that run, so the render sweep defers the move until the run
has left its actively-running states, and every daemon-side reader resolves a run's directory
dynamically (`RunPaths.ResolveCurrentDirectory`) rather than trusting a stale recorded path, so a
parked run still finds its own files once the sweep does move it back. `Done` alone does not
qualify: a task reads Done from the moment its pull request opens, and only true closeout — the
merge the closeout monitor actually observed — moves it to the archive, so a task with an open
pull request stays exactly where it was.

A new run's directory lands under its owning task's directory (`<home>/tasks/<shortid>-<slug>/
runs/<run-id>/`) once the project has a home, so browsing the task's directory tells its whole
story — contract, workspace, every attempt — with no top-level `runs/` inside a home (backlog
49). An idea's discovery workspace follows the same rule, decided once at capture and never
revisited: an idea captured with a project whose home already exists gets its workspace under
that home from the start, and everything else keeps it at the platform-global location
permanently, even once later assigned to a project (backlog 49). Both fall back to the
platform-global locations they always used — `~/.hall9k/runs/<run-id>/` and
`~/.hall9k/ideas/<idea-id>/workspace` — when there is no home to put them under, and a stream or
idea recorded before this shipped keeps reading from exactly where its files have always been.

The hall9k project itself completed this same move into its default home (backlog 52): the
project's home at `~/.hall9k/projects/hall9k` is canonical, and this repository is that home's
`repo/dev` worktree.

### The help tree

Every command carries a domain-language description and at least one worked example, enforced by
a test that walks the shipped tree. A command line that never reaches a command is answered with
that command's own help rather than a stack trace.

---

## Designed but unbuilt

Each of these has a written design. Reading the linked file is faster than asking.

### The mid-run question loop (Slice 2)

The design is settled: an agent that needs a decision calls `h9k ask` and **exits**, and
`h9k answer` resumes the session with the answer injected, so a run can park for hours without
holding a process open. The `QuestionAsked` and `AnswerProvided` events are already on the task
stream. **The `ask` and `answer` commands do not exist.** An agent that needs a decision today
makes the most reasonable call and records the assumption in its handoff, and nobody should tell
a human they can answer a running agent.

See [PLAN.md §13](../PLAN.md), Decisions Log #5.

### Windows support

Windows builds and tests in CI (with the Docker-dependent integration tier excluded, since
Windows runners cannot run Linux containers). `h9k install` and `h9k update` place release
binaries on Windows and put `h9k` on the PATH; `h9k daemon start` / `stop` / `status` and
`h9k daemon autostart enable` / `disable` all work there now too — `IProcessManager` gained its
Windows implementation (spawn via `cmd.exe`, kill-tree and reattach shared with macOS since
neither turned out to be OS-specific), and autostart is a Task Scheduler logon task, never a
service, so it runs as the signed-in user and credentials work exactly as they do on demand.

What is still outstanding, on purpose, is the real-machine walk: this task's own acceptance was
split so that the lifecycle code, its tests, and CI are checkable here, while the actual install
→ doctor → daemon start → autostart → one-task-to-a-PR walk on a physical Windows machine is
Brian's own acceptance step once a release exists to walk from — not something a dispatched run
can demonstrate for itself.

See `SLICE-1.md` S1-14, Decisions Log #3, #78, #85.

### Token visibility and exhaustion

Token usage is recorded per session on every run, and cost is observed rather than derived. What
is missing is the reporting: the pull request footer is written once at open rather than kept
current, and `h9k task show` has no tokens surface. Separately, when the subscription window runs
dry mid-flight, sessions die as generic errors instead of being recognised as a recoverable,
clock-bound condition that should hold and resume.

See [`backlog/36-token-visibility.md`](../backlog/36-token-visibility.md) and
[`backlog/40-token-exhaustion.md`](../backlog/40-token-exhaustion.md).

### Killing a run without killing its task

Stopping a session while keeping the work, then requeuing or holding on the human's word, is
designed. `Killed` is a reserved run state with no command behind it.

See [`backlog/38-kill-run.md`](../backlog/38-kill-run.md).

### Watching and peeking

Two separate absences, each designed:

- `h9k watch [--notify]`, the blocking watcher that owns desktop notification. Nothing pushes a
  notification today, and no session should promise to.
- A session peek: "what is the agent doing right now" is a filesystem dig rather than a command
  ([`backlog/21-session-peek.md`](../backlog/21-session-peek.md)).

`h9k doctor` — the database doctor check — is built (Decisions Log #73, #74; see
[operations.md](operations.md#postgres)). It diagnoses reachability, credentials, and schema; it
does not diagnose the separate daemon-liveness gap described in
[operations.md](operations.md#known-operational-gaps) (whether a running daemon is serving *this*
database), which stays open.

### The slim agent profile and auxiliary sessions

Dispatched sessions currently inherit the owner's full Claude Code configuration, including every
MCP server, none of which any platform session has ever used. The design narrows that to a slim
default with MCPs declared per task, keeping CLIs as the on-demand capability path. Alongside it,
a running agent requesting an auxiliary session with declared capabilities is designed and
unbuilt.

See [`backlog/29-slim-agent-profile.md`](../backlog/29-slim-agent-profile.md) and
[`backlog/30-auxiliary-sessions.md`](../backlog/30-auxiliary-sessions.md).

### The learnings loop

Recording run-earned lessons as event-sourced platform data and injecting each project's active
lessons into every dispatch. Today those lessons are written by hand into `AGENTS.md`.

See [`backlog/16-learnings-loop.md`](../backlog/16-learnings-loop.md).

### Sequencing the ready set automatically

The dispatcher is deliberately mechanical: it takes queued tasks in order and has no idea whether
two of them collide. Inferring that is currently a human judgment, documented in
[AGENTS.md](../AGENTS.md). A coordinator agent that reads the ready set, estimates file
footprints, and authors `--blocked-by` edges with a recorded why per edge is the eventual answer;
what it waits on is enough manually authored edges to know what a good one looks like.

See [`backlog/IDEA-coordinator-agent.md`](../backlog/IDEA-coordinator-agent.md).

### The formal funnel

`h9k idea add` and promotion exist. Triage as a batch gate, conversational discovery as a named
flow, the parking garage with resurfacing counts, and idea fan-out into several drafts are
designed and unbuilt.

See [PLAN.md §3 and §7](../PLAN.md),
[`backlog/31-idea-fanout.md`](../backlog/31-idea-fanout.md).

### Multi-node and peer-to-peer

Everything needed to keep the door open is paid for: owner as a first-class entity, globally
unique ids, no single-owner assumptions in streams or projections, and lease-based claiming that
works identically for one node or twenty. **Nothing beyond that is built.** No node discovery, no
gossip, no replication, no cross-user trust.

The peer-to-peer branch has a full design (identity as a two-tier key hierarchy, mDNS on the LAN,
hole punching, a relay on 443, QUIC throughout) in
[HALL9K-P2P-DESIGN.md](../HALL9K-P2P-DESIGN.md) and Decisions Log #38 to #58. Having a design does
not decide the branch; it removes "we do not know what it would look like" as a reason to avoid
it. The first step when multi-machine actually arrives is to evaluate several daemons against one
shared Postgres, which the lease model already makes safe.

---

## Deliberately not doing

These are decisions, not gaps. Proposing them is fine; assuming they were overlooked is not.

**The platform never merges your pull request.** That is the last human checkpoint and it stays
one. `Delivered` means the work is on a pull request and every automated thing that could be said
about it has been said; `Done` waits for you to merge it.

**No bot identity.** Agents act as the node owner's git and `gh` identity, and the work is
authored by the human. No `hall9k-agent[bot]`, no `Co-Authored-By` trailers, no
generated-with footers. The audit trail lives in Hall9k, not in commit cosmetics.

**Agents never open a pull request, and never start a review thread.** The daemon opens pull
requests, and there is deliberately no create-pr skill. Agents reply inside existing threads only.
That second rule is load-bearing rather than stylistic: since every comment is authored under the
human's login, a thread's first comment being a reviewer's is the only thing that distinguishes
feedback from an earlier agent's reply.

**The platform never composes a Jira card's content, and never transitions or closes one.** Issue
types, required fields, and routing rules are the organisation's configuration, so composing a
card's content is always an agent's or an operator's judgment — but hall9k is the sole *executor*
of every Jira write (create, update, comment): composition and execution are split, and the
executor refuses a transition or a close regardless of what was composed, because which status a
merge means is a team's workflow rather than a fact about software. The one write the platform
initiates entirely on its own is a comment on the card when the task's pull request merges.

**Never guess at an unobserved fact.** Audit fields, history, and identifiers record what was
actually observed, and the unobserved is represented as explicitly unknown. A quiet pull request
reads as "no external review activity observed" rather than "clean". A session whose process
cannot be seen reads as "liveness not observed here" rather than as either answer. An agent that
needs a decision parks with the question rather than picking an answer that looks plausible.

**Not a work tracker.** Jira and GitHub already do that. Once work is published, the external
item is the source of truth for its content, and the task carries the operational depth.

**No two-way content sync, and no bulk backlog mirroring.** The platform practises selective
adoption: a 400-card Jira project yields the handful of cards you are actually taking on. Sibling
and epic awareness works by pointing at the siblings in agent context, so the agent fetches them
live rather than importing staleness.

**Not an agent runtime.** Claude Code already is one. Hall9k spawns it and supervises it. Claude
Code subagents are specifically not used as workers, because they run inside a parent session,
flood its context, and serialize on it.

**No hosted SaaS, no multi-tenant cloud, no kanban UI.** Local-first is the identity, not a
stopgap. A web portal rendering the same event-stream projections is a far-future option, and
nothing in the design depends on it.

**No voice capture.** Wispr Flow covers it, and the standard capture flow is dictating into
`h9k idea add`.

See [PLAN.md §1, §14](../PLAN.md) for the non-goals as originally written, and
[AGENTS.md](../AGENTS.md) for the rules an agent has to follow because of them.
