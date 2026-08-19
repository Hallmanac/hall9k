---
project: hall9k
type: bugfix
objective: Stop system sleep from masquerading as node death, so a lid-close never spawns duplicate agents
criteria:
- Before expiring a lease held by THIS node, the sweep asks the operating system whether the run's process is alive (the supervisor knows the pid); a live local process means a live lease, whatever the heartbeat timestamp says - never declare a local process dead by timestamp when the OS can be asked
- The daemon detects wake-from-sleep (wall-clock jump far beyond the tick interval) and refreshes this node's heartbeats BEFORE the next sweep runs, so the wake-time race between sweeper and heartbeat service cannot requeue tasks whose agents are about to resume
- A claim is refused when the task's previous run's process is still alive on this node (single-flight per task per node), closing the duplicate-agent hole from the other side
- Remote-node semantics are unchanged: a genuinely silent remote lease still expires on the timeout (sleep detection only applies to leases this node holds)
- An integration or unit test pins the wake scenario: stale heartbeat + live process = no requeue
- A requeued task whose pull request is already merged closes out instead of redispatching: before spawning an agent for a task carrying a PR URL, the dispatch path consults the PR's state (or hands the task to the closeout monitor) so a dead run's requeue can never rebuild merged work (origin incident, 2026-08-18: after PR #11 merged, the storm-killed generation 5's lease expiry requeued the task and generation 6 spawned a fresh agent to rebuild the feature that was already on main; killed by hand)
- dotnet build and dotnet test pass
---
Origin incident (2026-08-18): a laptop lid-close during two active runs turned into a
generation storm. Each sleep stopped heartbeats; each wake ran the sweep before the
heartbeat service's first tick; the sweep saw 50-minute-stale leases, requeued both
tasks, and spawned fresh agents - while the previous agents resumed alongside them.
Task 13 reached five simultaneous "Running" generations, four agents sharing one
retained worktree, before a human killed the daemon and every agent by hand. Roughly
three tasks' worth of tokens were burned building the same two features.

Design constraints:
- The pid check must verify identity, not just existence (pid reuse): match on the
  recorded process start time or command line, the same discipline the process
  manager's adoption path already uses.
- Wake detection needs no platform APIs: comparing the expected next-tick wall time
  against actual wall time is enough, and works for any suspend cause (sleep,
  debugger pause, VM freeze).
- This narrows but does not replace backlog 14's parked-lease criterion: 14 keeps
  parked runs' leases alive by design; this task keeps RUNNING runs' leases honest
  across suspensions. Both matter.
