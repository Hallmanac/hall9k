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
