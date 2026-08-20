---
project: hall9k
type: feature
objective: Make "what is the agent doing right now" a command instead of a filesystem dig, for every session role
criteria:
- h9k logs <task> --live (or an equivalent flag settled at design time) tails the CURRENTLY ACTIVE session's stream for the task's current run, whichever role it is - build, review, fix, or Copilot follow-up - rendered like h9k logs renders transcripts, following as new events arrive
- With no session active, the command says so plainly and shows which session ran last and how it ended, instead of an empty tail
- h9k task show names the active session when one exists: role, session id, pid, started-at, and last-stream-write time - the heartbeat surfaced as data (a stream untouched for many minutes is visibly suspect long before the one-hour stall flag)
- The command resolves which stream file is current from the run's recorded session milestones, never by guessing at newest-file-wins (a resumed or re-prompted session must resolve correctly)
- Teaching descriptions and WithExample per the CLI standards
- dotnet build and dotnet test pass
---
Origin incident (2026-08-20): a fix session ran 44 minutes and the only way to
answer "is it stalled or working" was ls -lt on the run directory and tailing
the newest stream file by hand - which established it was actively testing
table widths one second before the check. The stream file is the agent's
heartbeat; this task turns that recipe into a first-class command.

Design constraints:
- Read-only observation: peeking never touches the session or the run's state.
- The review loop's session files (review-N, review-fix-N) are the ones a human
  most wants to watch and the ones h9k logs cannot reach today.
- Renders through the existing StreamRenderer; no second rendering path.
