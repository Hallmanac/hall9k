# Scope

What works today, what is designed but unbuilt, and what the project deliberately will not do.

This page is written to be checkable. Anything under "works today" has run in anger, because
Hall9k has been building Hall9k since the pipeline first ran end to end. Anything under "designed
but unbuilt" names the file that holds the design, so you can read it rather than take this
page's word for it.

Last reconciled against the tree on 2026-08-23.

---

## Works today

### The dispatch pipeline

Draft a task, publish it through the readiness gate, assign it, and a daemon claims it under a
lease, cuts a worktree and a branch from the base branch, spawns a detached Claude Code session
with an assembled prompt, runs the project's verification gates, puts the diff through a two-lens
independent review loop, and opens a pull request. Then it watches that pull request until the
merge is observed, dispatching bounded follow-up runs for failing checks and unresolved review
threads along the way, and removing the worktree and the branch at true closeout.

Two things about that loop have been hardened by incident rather than by design review: leases
survive a laptop lid closing without spawning duplicate agents, and daemon catch-up after a
restart cannot double-book a task, because a stale generation is fenced out of every task-level
write.

### The board

`h9k status` composes three surfaces (lifecycle state, live phase, attention) from the underlying
records, and a phase never claims a session is doing something without observing the process.
Every needs-you cause is quoted from a record and carries the command that clears it. Browsing
lives under `h9k task list`, filterable by project and state and bounded with a footer saying how
many rows were held back and how to see them, and under `h9k project list`, which counts every
project's tasks by attention bucket.

### The dependency graph

`--blocked-by` edges, enforced at assignment and re-evaluated every dispatch cycle. Cycles are
refused at publish and named hop by hop. A dependency counts as met only at true closeout. A
blocker that dies holds its dependents visibly rather than silently unblocking them, and a
blocker that recovers clears the hold on its own. The same edges route context: a run receives
what its immediate blockers handed down at their closeout, exactly one hop.

### Ideas

Capture with one command and one argument, a discovery workspace per idea, revision with full
history, assignment to a project after the fact, promotion into a draft task, and discard with
the reason kept. Provenance is recorded in both directions.

### External work items

`h9k task add --from-issue` adopts a GitHub issue; `--from-jira` adopts a Jira card. Both are
one-time snapshots that never re-check, and neither invents acceptance criteria. Jira is
connected as a read credential plus an agent-mediated pen: `h9k task push-to-jira` dispatches a
session that writes the card in the project's own repository, and `h9k task link-jira` reads the
key back through the connection before recording anything. When a task carrying a Jira reference
merges, closeout comments the pull request on the card.

### Recovery

`h9k task retry`, `h9k task resolve`, `h9k task abandon`, `h9k pr resolve`, and
`h9k review resolve`, all human-only. `Failed` is a waypoint with exactly three exits, and none
of them is automatic.

### Configuration and policy

Per-project and per-owner settings resolving most-specific-wins over a node default: verification
gates, agent model per role, parallelism, commit style, context links, skip-permissions, the Jira
board binding, and the post-fix review re-request policy. The node has a session ceiling the
dispatcher respects, counted in agent sessions rather than runs.

### Installation

`h9k install` publishes release binaries to `~/.hall9k/bin` and links `h9k` onto the PATH, on
macOS. The daemon has a CLI-owned lifecycle with a strictly opt-in launchd autostart that
snapshots the environment it will need and reports any tool it cannot resolve.

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
Windows runners cannot run Linux containers). `h9k install` refuses to run on Windows, and the
process manager needs spawn, kill-tree, and reattach parity. The daemon form is decided: a Task
Scheduler logon task, so it runs as the user and credentials work.

See `SLICE-1.md` S1-14, Decisions Log #3.

### Release delivery

Today's install is clone-and-build. A tagged commit becoming installable binaries on a bare
machine (release CI, per-OS self-contained artifacts, a one-line bootstrap) is designed and not
built, and it is the tier the distribution ladder calls "interim, likely permanent".

See [`backlog/42-release-delivery.md`](../backlog/42-release-delivery.md),
[PLAN.md §13](../PLAN.md).

### Repository materialisation

A node that has never cloned a project cannot work it: the repository has to already be on disk.
On-demand materialisation (bare clone, corrected refspec, worktree-ready) is designed.

See [`backlog/43-repo-materialisation.md`](../backlog/43-repo-materialisation.md).

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

**The platform never authors a Jira card, and never transitions one.** Issue types, required
fields, and routing rules are the organisation's configuration, and which status a merge means is
a team's workflow rather than a fact about software. The platform makes exactly one write of its
own: a comment on the card when the task's pull request merges.

**Never guess at an unobserved fact.** Audit fields, history, and identifiers record what was
actually observed, and the unobserved is represented as explicitly unknown. A quiet pull request
reads as "no finding recorded" rather than "clean". A session whose process cannot be seen reads
as "liveness not observed here" rather than as either answer. An agent that needs a decision
parks with the question rather than picking an answer that looks plausible.

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
