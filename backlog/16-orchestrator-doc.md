---
project: hall9k
type: chore
objective: Teach any interactive Claude Code session to be the orchestrator window, and backfill usage examples across the whole CLI
criteria:
- AGENTS.md gains the orchestrator-window section (or a linked repo skill) covering the role ("window, not alarm" - PLAN.md section 2), the h9k command surface with realistic examples, the question/answer relay flow (h9k status surfaces NeedsHuman, the human answers, the run resumes), and the standing policy that ALL new work enters via h9k task add - the flip is law, an orchestrator session never implements platform features directly
- The orchestrator section names the judgment the orchestrator currently owns: sequencing queueable tasks by likely file-footprint collision (what runs in parallel, what waits, what runs alone), with IDEA-coordinator-agent.md cited as its eventual automation
- The section covers the recovery levers by name and when each applies: task retry (machinery failed, work must re-run), task resolve (objective met despite the failure), task abandon (walk away), pr resolve (PR feedback follow-up), review resolve (parked review verdict)
- Every existing CLI command and option has .WithDescription/.WithExample per the AGENTS.md CLI command standards; this task backfills any command still missing examples (the standard is already law; audit the full command tree)
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
