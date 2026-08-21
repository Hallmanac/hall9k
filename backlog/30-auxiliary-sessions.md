---
project: hall9k
type: feature
objective: A running agent can request an auxiliary session with declared capabilities - the platform spawns it, supervises it, and hands the result back
criteria:
- An agent invokes a platform command (e.g. h9k run spawn --prompt-file <mission> [--mcp <server>]...) to request a run-scoped auxiliary session; the DAEMON spawns it - agents never spawn agents, the platform is the only spawner
- The auxiliary session gets exactly the declared MCPs on top of the slim profile (backlog 29), a resolved and recorded model per role (decision #33's chain; a new Auxiliary role or the requester's role, decided in design), and its tokens recorded on the run like every session
- The requesting command BLOCKS until the auxiliary completes and returns the result: exit outcome, a summary, and the artifact paths it produced in the run's directory - synchronous for the requester, supervised for the platform
- Auxiliary sessions are bounded by a per-run budget (DaemonOptions, small default); exhaustion fails the request with a self-correcting message, never queues silently
- The spawn request and completion are run-stream milestones carrying the declared capability set - what ran with what access is an observed fact
- The distinction from task add is taught in the command help: discovered WORK becomes a draft task for a human to publish; a mid-run NEED becomes an auxiliary session
- dotnet build and dotnet test pass
---
Brian's direction (2026-08-21), completing the slim-profile design: the slim agent
works through CLIs (gh, twg, git - the observed record is that no platform session
has ever needed an MCP), and when a mission genuinely needs launch-time capability
(a browser for end-to-end testing), the agent asks the platform for an auxiliary
session with the MCPs declared, rather than being pre-loaded with everything or
spawning anything itself. Precedent: the ReviewEngine already spawns supervised
sessions inside a run; this generalizes that seam to agent-requested missions.

Design constraints:
- Depends on backlog 29 (the slim profile and per-task declarations).
- Recursion is capped hard: an auxiliary session cannot request auxiliaries in v1.
- The blocking command needs a timeout with an honest failure; a hung auxiliary
  must not hang the parent run silently (stall detection applies to both).
