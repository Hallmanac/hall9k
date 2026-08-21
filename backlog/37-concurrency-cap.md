---
project: hall9k
type: feature
objective: The dispatch engine respects a per-node concurrency ceiling so a full queue cannot start more agents than the machine can hold
criteria:
- DaemonOptions gains MaxConcurrentRuns (default 3): the dispatch sweep claims a queued task only while the node's count of live runs is below the ceiling; everything else stays Queued - which already honestly means waiting - and is claimed as slots free up, oldest assignment first
- The count of live runs means agent sessions the daemon is supervising right now: build sessions, review cycles, fix runs, follow-ups, synthesis passes all occupy the slot of the run that owns them; a run releases its slot when its session tree is done (completed, failed, parked, or following-up with no live session), not at true closeout - a task waiting on a merge observation holds no memory
- h9k status shows the pressure honestly when the ceiling is the reason nothing dispatches: a queued row that is waiting for a slot says so in one line (per the backlog-28 reason discipline), so a quiet board reads as throttled, not stalled
- The daemon log states the ceiling at startup and logs each deferred claim once (task id, current live count), never per-sweep spam
- The ceiling is configuration, not code: DaemonOptions like the review caps, so the Windows tower and the Mac laptop can carry different numbers per their memory
- dotnet build and dotnet test pass
---
Origin incident (2026-08-21, twice in one day): the morning kernel panic showed
compressor exhaustion and 37 swapfiles with an MCP-heavy agent fleet resident; the
afternoon OOM killed three of four concurrently dispatched Opus-1M agent sessions
(tasks 17, 35, 27 died mid-run; 25 survived) the moment the platform hit its first
four-wide parallel push. The dispatch engine claims everything eligible with no
ceiling - the machine, not the platform, ends up enforcing one, by killing agents.

Design constraints:
- Queued-waiting-for-slot is NOT a new task state: the task is Queued, the reason
  line is display composition from the daemon's live-run count (backlog-28 shape).
- Composes with backlog 29 (slim agent profile): 29 shrinks each agent's footprint,
  this bounds how many exist - both are needed, neither substitutes for the other.
- Review/fix/synthesis sessions deliberately do not take their own slots: they
  belong to a run that already holds one, and counting them separately would let
  two builds plus their reviews exceed the ceiling the number was chosen to protect.
- Sequencing: touches DispatchEngine/DispatchLoop and DaemonOptions. 35 (in flight)
  touches ReviewEngine and DaemonOptions - the DaemonOptions overlap is one file;
  queue after 35 merges unless the queue drains first, in which case rebase pain is
  minor and acceptable given the do-soon priority (Brian, 2026-08-21).
