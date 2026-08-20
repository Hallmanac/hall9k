# Idea: coordinator agent (inferred task sequencing)

Captured 2026-08-19 from Brian's question: can Hall9k kick off a Claude Code
coordinator that orchestrates the available tasks - deciding what can run in
parallel and what must run consecutively?

## The gap

The dispatch loop is a mechanical coordinator: FIFO, capacity-bounded, zero
awareness of whether two tasks collide. Backlog 05's BlockedBy graph will
ENFORCE sequencing, but only as declared by whoever authors the tasks - the
graph is enforced, never inferred. The judgment layer between them ("these two
both rewrite the TaskDecider, serialize them; those two are disjoint, run them
together") currently lives in a human-driven orchestrator session, exercised
constantly during the v0 build (08+09 in parallel, 13 held behind 09, 05
flagged run-alone). That judgment is real work and it is nobody's component.

## The shape

A coordinator agent - a headless session the daemon dispatches over the ready
set - that:

- Reads the queued/published tasks (objectives, criteria, agent context) and
  estimates each task's likely file/subsystem footprint against the repo.
- Authors BlockedBy edges where footprints collide, using 05's machinery as its
  output format: the coordinator's product is edges on the graph, nothing else.
  The dumb dispatcher then executes the sequenced graph exactly as it would a
  human-authored one.
- Flags run-alone tasks (wide rewrites like 05 itself) explicitly.
- Records its reasoning per edge (the one-line why, per working agreements) so
  a human can audit or delete an edge it got wrong.

## Constraints that make it honest

- The coordinator only ADDS edges, never removes human-authored ones; a
  human's declared dependency always outranks an inferred one.
- Inferred edges are conservative: a collision guess costs latency (serialized
  tasks), a miss costs a rebase conflict - both survivable, so prefer latency
  only when confidence is real; do not serialize the whole queue by reflex.
- Depends hard on backlog 05 (the graph must exist before anything can author
  edges on it). Natural sequencing: 05, then dogfood manual edges for a while,
  then this.
- Relates to S1-13: the orchestrator-window documentation should name this
  judgment as the human's job today, and this idea as its eventual automation.

## Adjacent judgment: choosing the model per task

Captured 2026-08-20 alongside backlog 19 (Decisions Log #33), which made the
model a deliberate, per-role, recorded platform decision but deliberately
stopped short of choosing one automatically. Auto-selection is the same species
of judgment as sequencing: "this task only needs Sonnet with high thinking" is
an inference about the work, not a mechanical rule, so it belongs to the
coordinator rather than to the dispatch loop.

Why it waits: #33 records the resolved model on every dispatch and #30 records
tokens per run, so a few weeks of real usage produce the first honest evidence
of whether a smaller tier holds the quality bar. The bar is concrete: the PGID
catch (#31) and the seven-cycle daemon review are the kind of finding a cheaper
reviewer would have to reproduce. Judging without that data would be guessing,
and #11's never-loop-on-judgment spirit says the platform waits.

The shape when it arrives is the same as edge authoring: the coordinator's
product is a task-level model override on the ready set, with a recorded
one-line why per choice, feeding the existing resolution chain rather than
bypassing it. A human-stated override always outranks an inferred one, exactly
as a human-authored edge outranks an inferred edge.
