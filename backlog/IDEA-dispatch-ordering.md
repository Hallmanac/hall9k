# IDEA: Ordering within the ready set

**Status:** Draft — funnel entry, and possibly a decision to *not* build
**Origin:** Beads comparison session, 2026-08-20
**Builds on:** Decision #34, which deliberately left ordering as FIFO by `AddedAt`
**Would become:** Decisions Log #37

## Where this sits

Decision #34 settled what the ready set *is*: `Queued`, assigned to this node's owner,
every `BlockedBy` dependency at true closeout. It explicitly did not change how that set
is ordered — FIFO by `AddedAt`, as before, on the stated principle that "dependencies and
assignment shape the ready set, not the ordering."

This is the note for when FIFO stops being enough. It may never be, and that is a
legitimate outcome. The point is to have the reasoning written down rather than
rediscovered in six months.

## Priority is not order

The distinction that took longest to see, and the one that matters most:

- **Edges express necessity.** This cannot start until that finishes. Where no edge
  exists, the work is genuinely parallel — and after #34 that absence is the daemon's
  explicit licence to dispatch several agents at once.
- **Priority expresses importance.** Which of these unrelated things do I want first.

Within a single idea's breakdown, priority is close to meaningless, because the edges
already encode the order. Priority only says something *across* ideas, comparing
unrelated work.

Which suggests priority belongs on the idea, at triage, with tasks inheriting it — not
set per task at creation. And #34 already put the natural home in place: the readiness
contract is enforced at `publish`, so priority is a property of a published task, not a
field you fill in at `add`.

## Priority is mutable, so it is an event

Priority is continually refined; it cannot be a property set once. So it is its own fact
— `IdeaPriorityChanged`, or `TaskPriorityChanged` if inheritance is not the model.

Note this is one of the few edits #34 would otherwise forbid. Revision is Draft-only, and
returning a `Queued` task to Draft to re-rank it means `unassign → draft → revise →
publish → assign` — four explicit acts to change one number, and it drops the task out of
the ready set on the way. So a priority change has to be its own event permitted in
`Published`, `Queued`, and `Blocked`, or the feature is unusable.

Side effect worth having: a history of your own re-ranking. Genuinely informative when you
look back and notice something sat low while you kept bumping other work past it for three
weeks.

## The candidate ordering

If FIFO is ever replaced, the ranking is:

1. **Priority** — the only subjective input. Keep it a small explicit scale.
2. **Dependent count** — how many tasks are blocked behind this one. Objective, computed
   from the `BlockedBy` edges #34 already built. A task with six dependents is worth more
   than an isolated task at the same priority, because clearing it releases a subtree.
3. **Age** — `AddedAt`, as the tiebreaker. Objective, and what happens today.

The intent is that dependent count does the heavy lifting automatically, so priority stays
coarse. Hand-tuning a number on every task is a discipline nobody sustains.

## The argument against doing any of this

Decision #34 made **assignment an explicit human act**. So the human already is the
prioritizer: you assign what you want run, and the ready set is by construction the work
you chose, in the order you chose it. Automatic ranking is solving a problem the
assignment gate mostly removed.

Dependent count still has an argument, because it is the one signal a human is likely to
misjudge — subtree size is not visible at a glance, especially in a graph an agent
authored during discovery. But that is an argument for surfacing it at the moment of
*assignment*, not at dispatch.

**Recommended first move: display it, do not sort by it.** Show dependent count in
`h9k status` and `h9k task show`, so the question it answers — "which of these should I
assign next?" — is answered where the decision is actually made. Leave dispatch FIFO.

If, after living with that, you find yourself assigning in an order the daemon then
ignores, that is the evidence that ranking is worth building. Until then it is machinery
in search of a problem, which is exactly what #34's restraint was avoiding.

## One at a time, not a drained list

Separately settled, and independent of ranking: the daemon should ask for the single best
next candidate each time it has capacity, rather than pulling a batch and draining it.

A batch is a snapshot that goes stale — by item four, a task has closed and unblocked
something better, or a peer node has claimed one. One-at-a-time keeps every decision on
current state, keeps the claim adjacent to the decision (which matters for the P2P race
window), and costs nothing at this scale.

The claim query after #34 is already effectively this shape. The note is to keep it that
way, and to resist batching if throughput ever looks tempting — a handful of agents does
not need it.

## Readiness as a counter (optimization, not a change)

Beads computes readiness by walking the graph on every `bd ready` call, checking only
*direct* blockers — transitivity comes free, because a blocked blocker is still open and
excludes its dependent anyway.

The projection equivalent is a counter: each task holds a count of unmet blockers, a
closeout decrements it on each dependent, and readiness is "counter == 0." Classic
topological sort, no walking.

`TaskDependencyResolver` currently re-evaluates instead, on two doors — closeout-driven
and the dispatch loop's sweep. That is correct, simpler, and it is also the only path that
catches a blocker that *died* rather than finished, which a decrement-on-close counter
would miss entirely. So the counter is not a straight upgrade; it would need the sweep kept
alongside it. Worth doing only if the sweep ever shows up in profiling.

The transitive closure load stays either way, for cycle detection at publish.

## Draft acceptance criteria

Scoped to the recommended first move only.

- [ ] `TaskDetails` and `TaskListItem` carry a dependent count — how many tasks name this one in `BlockedBy`
- [ ] `h9k task show` displays the dependent count and lists the tasks waiting on this one
- [ ] `h9k status` shows dependent count on `Published` rows, where the assignment decision is made
- [ ] Dispatch ordering is unchanged: FIFO by `AddedAt` among `Queued` tasks
- [ ] PLAN.md gains Decisions Log #37 recording the priority-vs-order distinction and the deliberate choice not to rank
- [ ] `dotnet build` and `dotnet test` pass

## Open questions

- Scale for priority, if it is ever added. Three levels? Five? Jira's, since Jira is the
  source of truth for content?
- Does priority live on the idea or the task?
- Is a "blocked longer than N" health flag worth having, now that `Blocked` is a real
  state with a visible section in `h9k status`? Cheaper than ranking and catches the
  failure ranking was meant to prevent.
