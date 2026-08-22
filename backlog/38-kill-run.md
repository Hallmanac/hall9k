---
project: hall9k
type: feature
objective: A run can be killed without killing its task - stop the session, keep the work, requeue or hold by the human's choice
criteria:
- h9k run kill <task-or-run-id> --reason terminates the live agent session (the daemon kills the process tree it supervises) and the run records Failed with the reason - the task stays alive with the recovery levers (retry, resolve, abandon) all open
- The command refuses politely when the daemon is not running - a stopped daemon supervises nothing, and detached agents answer to no one until adoption (the next daemon start remains the lever there)
- task abandon remains the task-level walk-away; this command is the run-level stop - help text teaches the distinction
- dotnet build and dotnet test pass
---
Origin (2026-08-21, Brian's question while stopping the daemon for a reinstall):
"Do we have a way to kill Agent Sessions mid-run?" The honest answer was no -
abandon is terminal for the task, daemon stop deliberately leaves agents running
detached, and neither is "stop this session, keep the task." The OOM incidents
the same day would have wanted exactly this lever for shedding load.

DRAFT ONLY - Brian refines before publishing.
