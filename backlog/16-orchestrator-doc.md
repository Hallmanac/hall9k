---
project: hall9k
type: chore
objective: Teach any interactive Claude Code session to be the orchestrator window, and backfill usage examples across the whole CLI
criteria:
- AGENTS.md gains the orchestrator-window section (or a linked repo skill) covering the role ("window, not alarm" - PLAN.md section 2), the h9k command surface with realistic examples, the question/answer relay flow (h9k status surfaces NeedsHuman, the human answers, the run resumes), and the standing policy that ALL new work enters via h9k task add - the flip is law, an orchestrator session never implements platform features directly
- The orchestrator section names the judgment the orchestrator currently owns: sequencing queueable tasks by likely file-footprint collision (what runs in parallel, what waits, what runs alone), with IDEA-coordinator-agent.md cited as its eventual automation
- The section covers the recovery levers by name and when each applies: task retry (machinery failed, work must re-run), task resolve (objective met despite the failure), task abandon (walk away), pr resolve (PR feedback follow-up), review resolve (parked review verdict)
- Every existing CLI command and option has .WithDescription/.WithExample per the AGENTS.md CLI command standards; this task backfills any command still missing examples (the standard is already law; audit the full command tree)
- The ClosingOut display bucket is renamed FollowingUp everywhere it renders (status composition, project rollups, tests, TASK-MODEL and decision-log prose): the state means a follow-up run is actively working the open PR, and the old name read as shutting down rather than working (Brian, 2026-08-20). Display-only - the bucket is composed at render time and never persisted, so no stream or migration concerns
- Missing required arguments never produce stack traces: every command with a required argument prints a teaching error naming what was missing and an example invocation (origin incident, 2026-08-20: bare h9k task publish crashed with an unhandled exception and a raw stack trace instead of teaching)
- Verified by use: a fresh interactive Claude Code session, given only the repo docs, can add a task, read status, and explain the recovery levers - record how the verification was performed
- dotnet build and dotnet test pass
---
This is SLICE-1 task S1-13. The orchestrator role has been performed live through the
entire v0 build (every task queued, sequenced, reviewed, and recovered through one
interactive session) but is documented nowhere - a fresh session would have to
rediscover the whole practice. CLAUDE.md currently defers to AGENTS.md with a note
that the orchestrator documentation "arrives with S1-13"; this task delivers it.

Design constraints:
- Write from the practiced reality, not aspiration: the levers, the sequencing
  judgment, and the review-checkpoint rhythm (agents build, Copilot and the internal
  reviewer check, the human approves PRs) all already happened - document what
  proved out, with origin incidents where rules have them.
- Keep it in AGENTS.md or a repo skill so every agent runtime shares it (the
  CLAUDE.md-defers-to-AGENTS.md structure is deliberate; do not fork guidance).
- The WithExample audit is mechanical: walk Program.cs's command tree, list every
  command/branch, verify each against the standard, fix the gaps. Do not restyle
  existing good help text while passing through.

## Post-queue note (Brian + orchestrator session, 2026-08-21 - the queued agent has the frozen snapshot; reconcile at PR review)

AMENDED 2026-08-22: the ClosingOut-to-FollowingUp display rename in this task's
criteria is SUPERSEDED by backlog 41 (the status redesign dissolves that display
bucket into a phase line) - at PR review, drop that criterion rather than renaming
a thing 41 deletes. The RunSuperseded rename below stays parked as written.

The RunSuperseded status joins the FollowingUp vocabulary family: rename to
FollowedUp (event RunFollowedUp), meaning a completed run that is no longer the
run of record because a follow-up run was dispatched for its task. "Superseded"
was silent on cause and agency - it read as though something could have stopped
the run mid-flight (a human, another agent), when in fact only a finished run is
ever followed up. The event should also record the successor run id so run
history reads "followed up by run <id>" without hunting. Persisted vocabulary,
so this follows the backlog-33 pattern (new event, legacy events replay into
the new status).

Priority note: Brian rates this low relative to forward-moving work - land it
with this task if convenient at PR review, or let it slide to a later pass;
never dispatch dedicated work for it. Deliberately rejected as too big for now:
splitting run status into two facts (session outcome + a run-of-record pointer
on the task), which is arguably the cleaner model; the rename does not block
that remodel if it ever earns its way up the list.
