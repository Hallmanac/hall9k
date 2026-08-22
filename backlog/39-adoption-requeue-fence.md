---
project: hall9k
type: bugfix
objective: Catch-up never double-books a task, and a stale generation can never write task-level state
criteria:
- Startup catch-up picks exactly one recovery per task - adopt the surviving run OR requeue the expired lease, never both; when a detached agent's run is adoptable, adoption wins and the lease is refreshed rather than expired
- Every task-level state transition driven by a run (review parked, run failed, pushed, closed out) is fenced on the run's generation being the task's current generation - a stale generation's write is rejected and logged, not applied
- The rejection log names both generations so the operator can see the stale lane die: "run at generation 2 attempted TaskFailed; task is at generation 3 - rejected"
- A stale lane's sessions stop at the first fence rejection rather than burning further cycles: the review engine checks the fence before dispatching each pass and each fix run
- dotnet build and dotnet test pass
---
Origin incident (2026-08-21 evening, twice observed): after a sleep-through-restart,
catch-up both adopted the detached runs AND requeued the same tasks' expired leases
(tasks 18 and 35). Both generations then ran full two-lens review cycles in parallel
in the same worktrees - roughly eight concurrent Opus sessions of duplicate work.
The stale generation-2 lane of task 18 then spent its fix budget, parked, and the
park/fail path WROTE TASK-LEVEL STATE: the board showed the task Failed while the
live generation-3 fix session was mid-flight, and the dependent's crying-wolf hold
re-armed off the lie. The dispatch-claim fence held all day for claims; the
run-driven task transitions turned out not to carry the same check.

DRAFT ONLY - Brian refines before publishing. Related: 37 (concurrency cap would
have bounded the parallel burn), 38 (kill-run would have let a human shed the
stale lane), 27 (the crying-wolf cascade this amplified).
