---
project: hall9k
type: feature
objective: Give the daemon a CLI-owned lifecycle - started and stopped on demand, with autostart as a strictly opt-in extra - and make install refresh the binaries
criteria:
- Installation puts h9k and h9kd release binaries in ~/.hall9k/bin with h9k on PATH, and registers NO background service, no login item, no autostart of any kind (today's hand-made symlink becomes the managed path)
- h9k daemon start launches h9kd detached from the terminal (survives shell exit and logout-safe on macOS conventions), logging to ~/.hall9k/h9kd.log; it refuses politely if one is already running (single-instance guard, decision to the log)
- On start, the daemon reports what it caught up on while down (tasks adopted, leases swept, merges the closeout sweep observed) so "on demand" visibly costs latency, never correctness
- h9k daemon stop shuts it down gracefully (finish in-flight event appends, leave agents running for adoption on next start - agents are detached by design); h9k daemon status reports running/not, pid, uptime, and the last few log lines
- h9k daemon autostart enable registers start-at-login (launchd LaunchAgent on macOS; Windows startup task deferred to S1-14 with a clear not-yet message); autostart disable fully unregisters it; neither is ever implied by install or start
- When autostart is enabled, stop still means stopped: stopping goes through the service manager (launchctl unload or equivalent) so the agent does not resurrect a daemon the human just killed
- h9k status states plainly when the daemon is not running ("daemon not running - tasks queue but do not dispatch; h9k daemon start") so a quiet queue is never a mystery
- Re-running the install after a merge republishes the binaries and, if the daemon is running, offers the restart (idempotent; this is the answer to installed-binary staleness)
- SLICE-1.md marks S1-12 complete with a pointer to the commands
- dotnet build and dotnet test pass
---
**Note (2026-08-23, backlog 42):** a later design session's IDEA doc claimed the Windows
autostart deferral below was stale and asked for it to be corrected here. Checked against
the code while building backlog 42 (release delivery): it is not stale — `DaemonAutostart`
still returns a deferred, not-yet implementation on Windows, and `SLICE-1.md`'s S1-14 still
carries the real Windows daemon lifecycle (including autostart) as unbuilt, dispatched-later
work. This file's criteria below are left as written. What backlog 42 actually added for
Windows is delivery only — release binaries exist for `win-x64`, and `h9k install`/`h9k
update` place them there — never daemon start/stop/autostart, which still name S1-14 and
refuse. See PLAN.md Decisions Log #78 for the full account.

This is SLICE-1 task S1-12, redesigned 2026-08-19 after colleague feedback on the
first demo: nobody evaluating a local-first tool wants it to install a permanently
resident background process as a side effect. The daemon is started on demand from
the CLI, runs until the CLI stops it, and start-at-login is an explicit opt-in.
The architecture already absorbed the trade-off: adopt -> sweep -> claim on startup
plus the closeout sweep mean a stopped daemon costs latency, never correctness -
the same lesson decision #29 encoded for sleep.

Earlier origin incidents still apply: the hand-started daemon died three times in
one day with its parent shell (detach properly), and the hand-made h9k symlink went
stale the moment main advanced (idempotent republish).

Design constraints:
- Detachment must not depend on a parent shell or this-session lifetimes: double-fork
  or launchd-submit-without-RunAtLoad or an equivalent honest mechanism, documented.
- The single-instance guard matters doubly now that starting is a routine human act;
  refuse with a teaching message naming the running pid.
- Crash-restart (KeepAlive) applies only under autostart enable, and even then must
  respect an explicit stop.
