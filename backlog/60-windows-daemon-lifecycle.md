---
project: hall9k
type: feature
objective: Hall9k runs on Windows as a standalone node - the daemon lifecycle that today politely refuses there works, so a Windows machine can install from a release, start its daemon, and take a task end to end with no Mac in the loop
criteria:
- IProcessManager gains its Windows implementation with spawn, kill-tree, and reattach parity - the same detachment semantics the macOS path has, proven by parity tests that run green on Windows CI
- h9k daemon start, stop, and status work on Windows: detached from the terminal, single-instance guarded, logging to the same ~/.hall9k/h9kd.log location, status honest about pid and uptime
- h9k daemon autostart enable registers a Task Scheduler logon task per Decisions Log #3 (never a Windows service - that constraint stands); autostart disable fully unregisters it; stop still means stopped when autostart is on
- The daemon reads its operating settings from the durable config file (backlog 59), because a logon task has no shell environment - the two tasks meet exactly here
- The full install story is proven on the real Windows machine from nothing but the README: bootstrap script, checksum verification, h9k install, doctor's database diagnosis with Docker Desktop, daemon start - and any README or INSTALL.md gap found while following them is fixed as part of this task
- One real task dispatched on the Windows node runs end to end: worktree cut, agent session, gates, review, PR opened - the S1-14 bar as written
- This node is standalone by scope: its own database, no shared room, no peer-to-peer - nothing in this task may depend on another machine existing
- dotnet build and dotnet test pass on both CI legs
---
S1-14 promoted to the backlog at the 2026-08-24 sitting: delivery to Windows
landed with backlog 42 (win-x64 release binaries, install.ps1, h9k install
--from-release, h9k update all handle it), so the remaining gap is exactly
the lifecycle - DaemonAutostart.ForCurrentPlatform() and the daemon commands
still return a deferred not-yet on Windows (Decisions Log #78 recorded the
honest state). Decision #3 already ruled the mechanism: Task Scheduler logon
task with kill-tree semantics, no service mode. Brian's target machine is
real and waiting; the acceptance bar is the platform's own: a task taken to a
PR on that machine. Depends on backlog 59 for settings the logon task cannot
get from a shell.
