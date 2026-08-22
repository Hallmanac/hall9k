# IDEA: Ranking, grooming, and multi-node dispatch

**Status:** Draft — funnel entry
**Origin:** Design session, 2026-08-22 (voice)
**Builds on:** Decision #34 (development and dispatch are separate lifecycles),
`IDEA-dispatch-ordering.md`, `IDEA-coordinator-agent.md`, `IDEA-task-assignment.md`
**Would become:** next entries in the Decisions Log (#61+)

## What this is

The question that opened the session was whether the human gate between `publish` and
dispatch could be automated. The answer that came out is *no, and that is fine* — but the
gate can move. Today the human decides "this should run now," one task at a time, at the
moment of assignment. Instead, the human decides "this is the order the project runs in,"
periodically, over the whole set — and dispatch becomes mechanical against that ordering.

That is the rung this is climbing: not removing the human, but moving them from
per-task scheduling to periodic prioritisation, which is a decision they are far better
placed to make and which does not have to be made ten times a day.

## Ranking is a fact on the graph, never a dispatch action

The discipline `IDEA-coordinator-agent.md` set holds here without modification: **the
product is facts on the graph, and nothing else.** A ranking session emits ordering
events. It does not start work, does not pick nodes, does not touch the dispatcher.

This is what makes it survivable under P2P. Ordering events replicate like any other
event, so every node holds the same ordering, and a node that has been offline replays to
the same place. No node is *the* scheduler. There is no scheduler at all.

The inverse — a coordinator that decided "this node takes this task" — would need to know
who is online and what they are busy with, which is precisely the centralised coordinator
the project pivoted away from.

## The ordering is expressed as edges

Three shapes were considered:

- **A priority value per task.** Cheap, but two tasks tie, and the number says nothing
  about why.
- **A ranked position.** An explicit total order, but every insert reshuffles everything
  after it — noisy in an event log.
- **Edges.** This before that. Chosen, because the model already speaks this language and
  the dispatcher already walks edges.

**Caveat that must not be lost:** a `BlockedBy` edge means *cannot start*. A ranking edge
means *should go first*. Preference is not necessity, and collapsing the two would be a
real modelling error — a wrong footprint guess would strand a task rather than merely
serialise it. Ranking edges are a distinct, softer type.

## Ranking states

A task's ranking is one of three values, not two:

- **Unranked** — published, contract satisfied, not yet groomed. A legitimate, healthy
  state. Not a candidate for dispatch.
- **Ranked** — has a position in the ordering. A candidate.
- **Expedited** — sorts to the front. Same ordering, extreme position.

Expedite is the *exception path*, and should feel like one: deliberate, named, and
carrying a reason. Following the Phoenix Project argument Brian raised — unplanned work is
only dangerous when it is invisible — **expediting leaves a mark**. The system should be
able to answer "how many things did I expedite last month." That reporting is the point of
the flag as much as the ordering effect is.

Structurally this is cheap: expedite is a ranking event with a flag and a reason. Nothing
new.

## Availability is orthogonal to ranking

A separate flag, and genuinely a different thing from `BlockedBy`.

`BlockedBy` edges are *internal* — task B waits on task A, and true closeout resolves them
automatically. What this covers is *external*: waiting on a vendor, an upstream release, a
decision nobody has made. Nothing inside the graph will ever clear it, so a human says
"now."

So a task can be top-ranked and unavailable. It keeps its position; it simply is not a
candidate. Lower-ranked available work rises naturally, with no re-ranking required.

## Grooming is a gate, not a cadence

The session is human-plus-agent — Claude Code, in conversation. The agent's job is to
surface evidence so the ranking is decided on facts rather than vibes:

- **Dependent count**, already computable from the `BlockedBy` edges (#34), and already
  recommended for display in `IDEA-dispatch-ordering.md`. A task with six things behind it
  is objectively worth going first.
- **Footprint**, which is the genuinely agentic part — see below.

Scope: **the whole project, at task level, published tasks only.** Whole-set rather than
per-idea, because the comparison priority exists to answer is *across* unrelated ideas —
within one idea the edges mostly already say the order. This is backlog grooming in the
ordinary sprint sense.

**Drafts are visible but not rankable.** They appear in the session as read-only context,
so the human can leave room for work known to be coming, but nothing gets an ordering fact
until it is published. Drafts stay genuinely inert.

And the gate, not the calendar, is the mechanism: **a published task cannot be scheduled
until it has been ranked.** Grooming is the batch case; publish-then-wait-for-Friday is the
normal path. A task sitting published and unranked is not stuck.

## Footprint estimation

The naive version — asking an agent to guess touched files from the task text — is weak,
because task descriptions talk about behaviour, not files.

The stronger version: it is a Claude Code session, so let it *search*. Read the acceptance
criteria, grep for the types and namespaces named, report the files it expects to touch.
That is a research task, not a prediction task. Collision is then set intersection over
reported paths.

Cheaper signal worth using alongside it: **git history** — which files actually changed for
past tasks in the same area.

Footprints are guesses and will sometimes be wrong. That is the argument for the soft edge:
being wrong should cost a little serialisation, not a stuck task.

## What dispatch becomes

Every node walks the same ranked ordering, top down, and takes the first task that
survives:

- skip **unranked**
- skip **unavailable**
- skip **claimed**
- skip **assigned to someone else**

Claim it. Hash arbitration settles simultaneous claims; the loser retries and lands on the
next survivor.

No allocator, no negotiation, no node holding any knowledge of another node's state. The
spreading is emergent: node A takes the top item, node B walks past it because it is now
claimed.

### The claim guard changes

Today: *claimable if assigned to me.* Under #34 that ownership requirement was the
structural safety for multi-owner projects.

Proposed: **claimable if ranked, available, and either unassigned or assigned to me** —
where the node's owner is a member of the project.

This deliberately loosens #34, and the justification is that **project membership is
already the trust boundary**. If someone is in the project, they can claim work in it.
Assignment becomes a *reservation* — "this one is mine, hands off" — rather than the
precondition for anything running at all. It also fixes the single-owner-multi-machine
case, where all three of Brian's nodes share one owner and any of them could do the work.

**The claim event still records who took it.** Attribution survives; only the reservation
is optional. The accountability invariant is untouched.

### When everything ready is unranked

The node sits idle. Deliberately, and quietly — idle is a legitimate state.

But `h9k status` should *show* it ("six published, unranked"), so a quiet queue is never
mistaken for a wedged daemon. Same principle as dependent count in
`IDEA-dispatch-ordering.md`: surface the fact where the decision gets made, do not act on
it.

## First slice: project membership

**This is a hard prerequisite, not a peer feature.** The claim guard above checks that the
node's owner is a member of the project, and today a project has exactly one owner — there
is nothing to check against. The whole spreading story needs a member list.

`HALL9K-P2P-DESIGN.md` already assumes one: the project invite token exists to add an
owner's public key to a project's member list. Designed, not built.

Scope it plainly:

- a project holds a **set** of owner IDs
- **membership is checked at claim**
- the **invite flow** adds one
- **members are equal** — no roles, no permissions

### Closeout authority is the task owner's (ruled 2026-08-22, Brian)

A standing constraint on this slice, recorded before any second member exists so it is
never discovered by incident. Members are equal *for claiming work*; they are not equal
for finishing it. Three moments, three different treatments:

- **Approval, by anyone, is eligibility and nothing more.** The closeout monitor should
  observe approvals (it reads none today) and surface "approved, eligible for completion"
  on the attention surface for the task's owner. An approval never advances state, never
  merges, never closes anything out.
- **Completion is the owner's act, expressed by the owner merging.** The pull request
  sits eligible until the human who owns the task decides to complete it or do something
  else. The platform's expected path stays exactly what it is today: merge is a human
  act, and the monitor observes it.
- **A merge by a non-owner** (GitHub cannot prevent a member with write access from
  merging) is still a fact and is still recorded — never-guess cuts both ways — but the
  closeout effects (Done, the Jira comment, finished-business standing) park as attention
  for the owner to confirm rather than firing automatically. *Open sub-question, deferred
  to when this is built:* whether the dependency chain releases on the merge fact (the
  code the dependents need is in main) or also waits for the owner's confirmation.

### On roles

Explored and deliberately deferred. Two distinct axes came out of it, worth recording so
they are not conflated later:

- **Capability roles** — who may invite, publish, archive. Access control. There is no
  threat model here in which a project member is untrusted, so YAGNI applies cleanly.
- **Workflow roles** — who reviews, who is the domain expert for a subsystem, who gets
  pulled in when something parks for a human. That is *routing*, not permission, and is the
  more interesting one if roles ever land. Decision #33's per-role vocabulary is the shape
  to lean on.

Adding capability roles later is **additive and cheap**: membership goes from "is this
owner in the set" to "is this owner in the set, and does their entry permit this" — a field
on an existing membership record. Event sourcing helps rather than hurts, because historical
events are already attributed and do not need backfilled permissions; you gate future
commands only, and existing members migrate to a default role.

**The one thing that would make it painful** is implicit membership — inferred from having
claimed work, say — because there would be no record to attach a role to. So the cheap
insurance is making membership an explicit event now, which this slice does anyway.

## Draft acceptance criteria

- A project holds an explicit set of member owner IDs, populated by the invite flow;
  membership is its own event.
- `h9k` refuses a claim when the claiming node's owner is not a project member.
- Ranking is its own event type, permitted in `Published`, `Queued`, and `Blocked` — never
  requiring a return to `Draft` (per `IDEA-dispatch-ordering.md`).
- Ranking edges are a distinct type from `BlockedBy` and never gate readiness, only order.
- A task carries a ranking state of unranked / ranked / expedited; expedite records a
  reason and is reportable over a time window.
- A task carries an availability flag, set and cleared by a human, orthogonal to ranking.
- The dispatcher walks the ordering and skips unranked, unavailable, claimed, and
  other-owner-assigned tasks; it claims the first survivor.
- Assignment is optional at publish; an unassigned ranked task is claimable by any member's
  node.
- The claim event records the claiming owner regardless of prior assignment.
- `h9k status` reports the count of published-but-unranked tasks and states plainly when
  idleness is caused by them.
- A grooming session command opens the whole-project ranking conversation with published
  tasks rankable and draft tasks shown read-only, surfacing dependent count and footprint.
- `dotnet build` and `dotnet test` pass.

## Open questions

- Does footprint estimation run inside the grooming session, or as a prior pass whose
  results the session reads? (A prior pass is cheaper and cacheable; a live pass is fresher.)
- How are ranking edges pruned as tasks close out? An ordering over a set that keeps
  shrinking will accumulate edges to completed work.
- Does expedite bypass the ranking gate entirely, or does it write a ranking event that
  happens to sort first? (Latter is cleaner and keeps one code path.)
