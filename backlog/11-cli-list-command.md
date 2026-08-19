---
project: hall9k
type: feature
objective: Replace the flat status board with a project-first h9k list that drills into a project's tasks on request
criteria:
- h9k list (no arguments) lists projects, one row per project, with rollup columns (task counts by state bucket, e.g. needs-you / active / awaiting-review / done)
- Below the project list, a teaching prompt tells the user the drill-down: run h9k list <project> to see that project's tasks (the CLI-standards rule - help that teaches, not labels)
- h9k list <project> lists the project's tasks with Status as a column, plus id, objective, activity, and PR - the information the old flat board carried
- <project> accepts the project name or an unambiguous fragment, with a self-correcting stderr message on no match or ambiguity (matching TaskIdResolver's manner)
- The task list is bounded, newest first, with a --all flag (or explicit paging) and a footer saying how many were held back and how to see them - the design should anticipate task counts growing overwhelming
- h9k status remains but narrows to its real job, the attention pane: what needs you, what is stalled, what is running - a summary, not a browse surface (drop or slim the full table)
- Both commands carry WithExample invocations and teaching descriptions per AGENTS.md CLI standards
- dotnet build and dotnet test pass
---
Brian's direction (2026-08-18): list is the conventional browse verb across CLIs, and
the flat status table will not survive real task volume. Browse should start at the
project level and drill in: h9k list shows projects, h9k list <project> shows that
project's tasks with status as a column. Teach the drill-down at the bottom of the
project list. Paging can start simple (bounded list + counts + --all) but the shape
should leave room for real paging later.

Design constraints:
- StatusCommand's Compose logic (bucket refinement from run state) is reused for the
  per-task Status column - do not fork the composition rules.
- Keep the status command's attention-first ordering (NeedsHuman, stalled, active) in
  whatever remains of it; list <project> orders newest-first for browsing.
- Command wiring in Program.cs follows the existing branch/leaf conventions.
