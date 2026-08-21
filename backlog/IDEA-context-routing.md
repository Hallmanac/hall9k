# IDEA: Context routing via dependency edges

**Status:** Draft — funnel entry, not a decision
**Origin:** Beads comparison session, 2026-08-20 (agent-to-agent messaging)
**Builds on:** Decision #34 — `BlockedBy` edges, `TaskDependencyQuery`, `RunCompleted` at true closeout
**Would become:** Decisions Log #36

## The gap

Beads lets agents post threaded messages to each other on an issue. The use case is
handoff: agent A finishes the schema, leaves a note about a gotcha, agent B picks up
the dependent task and reads it.

Notice what that substitutes for. Beads has no supervisor, so agents must coordinate
directly. It is peer-to-peer messaging because there is no central authority to route
through.

Hall9k has that authority, and after #34 it also has the graph. So the equivalent here
is not messaging — it is the daemon deciding what context a task starts with. Which is
a stronger position, because it does not depend on an agent choosing to leave a note.

## The insight

`BlockedBy` edges do double duty.

An edge says "this could not start until that finished," and that is a statement about
*relatedness*, not only about timing. If B was blocked by A, then A almost certainly
touched the same area, or produced the thing B builds on. The schema before the
projection. The interface before the implementation.

So the graph built for scheduling also answers the context question: when a task is
claimed, follow its blocking edges backwards to find the work that matters to it.
Nobody had to declare that separately, and nobody has to maintain a second structure.

This matters more after #34 than it would have before, because #34 made a dependency
mean something stronger. A blocker is met only at **true closeout** — `Done` plus
`RunCompleted` from the closeout monitor. So by the time an edge resolves, the upstream
work has a merged PR, a completed review, and a settled outcome. There is something
real to hand down.

## The shape

### Handoff summary at closeout

When a run reaches true closeout, its final act is a handoff summary: what it actually
did, what a downstream task needs to know, what it deliberately did not do.

This belongs at closeout rather than in a separate summarizer session. It is cheaper —
no extra agent — and the agent that just did the work is best placed to say what
mattered. `CloseoutEngine` is already the place that appends `RunCompleted` and calls
`TaskDependencyResolver`, so the summary lands on the same path that unblocks the
dependents who will read it.

Probably its own event rather than a field on `RunCompleted`: a run can complete
without producing a useful summary (a parked run resolved by hand, a pre-#36
historical stream), and `RunCompleted` should not have to lie about that.

### Depth-one assembly

When `DispatchEngine` claims a task, it assembles context from:

- the idea- / epic-level context that already exists, plus
- the handoff summaries of its **immediate** `BlockedBy` blockers only.

Not the transitive ancestry. A chain A → B → C passes B's summary to C, not A's and
B's. Otherwise a long chain accumulates until most of the context is irrelevant to the
task actually running — expensive, and worse than having less.

Note `TaskDependencyQuery` already loads the transitive closure, but that is for cycle
detection at publish. Context assembly deliberately reads only the first hop.

If C genuinely needs something from A, that is a **missing edge**, not a context
problem. Which makes the graph self-correcting: an agent repeatedly needing something
two hops back is evidence of an undeclared dependency — and is itself a learning worth
recording (see IDEA-learning-capture).

### Fan-in and synthesis

Fan-in is normal and healthy. Eight tasks running in parallel converging on an
integration task is good decomposition, not a smell — the parallelism is the point, and
after #34 the absence of an edge is the daemon's explicit licence to run them at once.

But eight summaries is heavy. So above a configurable threshold (start around three),
the daemon dispatches a synthesis pass that condenses the blockers' summaries into one
context document before the dependent task starts. Below the threshold, pass them
through raw.

Configuration, not a constant. The right number is only visible once real fan-in
patterns appear.

### Dead blockers

#34 established that a blocker reaching `Failed` or `Abandoned` parks its dependents
with the reason rather than unblocking or stranding them. That path never reaches
context assembly, so there is nothing extra to do — but a task resumed after its dead
blocker is retried and merged must pick up the summary from the *successful* run, not
the failed one. Worth an explicit test.

## Why not agent-to-agent messaging

Because it is a worse version of the same thing here. A note depends on agent A
correctly predicting what agent B will need, before B exists. The daemon decides after
the fact, with knowledge A could not have had: which tasks actually depended on it, what
the reviewer flagged, what merged and what did not.

Messaging is the right answer when there is no supervisor. There is one.

## Draft acceptance criteria

- [ ] A run at true closeout appends a handoff summary event carrying what the run did, what a dependent needs to know, and what it deliberately left undone
- [ ] The summary is authored by the run's own agent as its final act, not by a separate session
- [ ] A run that closes out without a summary is valid; the absence is visible rather than silently empty
- [ ] When `DispatchEngine` claims a task, the launched run's context includes the handoff summaries of its immediate `BlockedBy` blockers and no deeper ancestry
- [ ] Blocker count above a configurable threshold (default 3) dispatches a synthesis pass whose output replaces the raw summaries in the claimed task's context
- [ ] A task whose blocker died and was later retried to a successful merge receives the successful run's summary, never the failed run's
- [ ] A blocker with no summary falls back to its objective and acceptance criteria
- [ ] `h9k task show` displays the context a task would receive if claimed now
- [ ] Historical streams without summaries replay and dispatch unchanged
- [ ] PLAN.md gains Decisions Log #36; TASK-MODEL.md records the new event and the depth-one rule
- [ ] `dotnet build` and `dotnet test` pass

## Open questions

- Do review findings from the blocker's reviewer session travel downstream too, or only
  the agent's own summary? Findings are about the blocker's code, which the dependent is
  probably about to build on.
- Does a `conflicts-with` edge (if it is ever added) route context, or only serialize
  dispatch? Probably only serializes — a conflict is about files, not knowledge.
- Should the summary be a first-class artifact under `~/.hall9k/runs/<run-id>/` like the
  review findings, so it is inspectable outside the ledger?
