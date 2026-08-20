---
project: hall9k
type: chore
objective: Replace the work-in-progress README with real documentation that lets a newcomer understand, install, and run Hall9k without the author in the room
criteria:
- The README answers, in order, what Hall9k is (local-first agentic task orchestration - the one-paragraph pitch), what it looks like to use (a short real session: add a task, watch the pipeline, review the PR), how to install (h9k install, daemon start, Postgres via docker compose, prerequisites named), and where deeper docs live
- A docs/ folder carries the layer below the README: concepts (tasks, runs, the lifecycle, leases, the review loop, closeout), the CLI reference pointing at the --help tree as the source of truth rather than duplicating it, and operations (daemon lifecycle, recovery levers, what NeedsHuman means and what to do about it)
- Existing deep documents are linked, not rewritten: PLAN.md stays the vision and decisions log, TASK-MODEL.md stays the domain reference, AGENTS.md stays the contributor/agent guide - the new docs are the on-ramp that points into them
- Honest scope statements: what works today, what is designed but unbuilt (with backlog pointers), and what the project deliberately does not do
- The work-in-progress warning comes down only as part of this task landing - not before
- dotnet build and dotnet test pass (docs-only, but the gate stays the gate)
---
Captured 2026-08-20: the repo had no README at all until a placeholder went up
the same day, saying only "not ready, pay no attention." That placeholder is
doing its job while interfaces churn; this task replaces it when the project is
ready to explain itself.

Design constraints:
- Sequencing: after backlog 05 lands and the CLI surface from 11/16 settles -
  documentation written before then would describe interfaces mid-churn.
  Natural slot: alongside or just after 16 (the orchestrator doc), which covers
  the agent-facing half of the same need.
- Written from the practiced reality like 16: real command output, real
  workflow, no aspirational features presented as current.
- House writing conventions apply throughout (AGENTS.md, and the no-em-dash
  rule for authored prose).
