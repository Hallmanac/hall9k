---
project: hall9k
type: feature
objective: Complete the noun-first CLI shape - projects become browsable and inspectable, task list learns to filter, and status narrows to the attention pane
criteria:
- h9k project list shows one row per project with rollup columns (task counts by attention bucket, e.g. needs-you / active / awaiting-review / done) and a footer teaching the drill-downs (h9k project show <name>, h9k task list --project <name>)
- h9k project show <name> presents the project's registration and settings in one pane - repository path, base branch, connection binding, skip-permissions, verify gates, parallelism, commit style - plus the task rollup and the few most recent tasks with their states
- <name> accepts the project name or an unambiguous fragment, resolved in TaskIdResolver's manner: no match and ambiguity each get a self-correcting stderr message naming the candidates
- h9k task list gains --project <name> and --state <state> filters; output is bounded newest-first with a --all flag and a footer saying how many rows were held back and how to see them (task volume will grow; the shape must anticipate it)
- h9k status narrows to its real job, the attention pane: what needs you, what is stalled, what is running - a glanceable summary, not a browse surface (the full flat table goes)
- No new top-level browse command: the structure is noun-first (h9k project ..., h9k task ...); convenience shortcuts like a bare h9k list are deliberately out of scope, icing for later if ever
- Every new or changed command and option carries teaching descriptions and WithExample per AGENTS.md CLI standards
- dotnet build and dotnet test pass
---
Brian's direction (2026-08-20, superseding the 08-18 shape): the CLI's primary
structure is noun-first - the top-level things you manage (project, task, and later
owner, connection, node) each own their verbs, exactly as the existing tree already
hints (h9k project add/set, h9k task add/list/show). Browse and inspect capabilities
belong under their nouns: h9k project list, h9k project show, h9k task list
--project. Shortcut commands (a bare list, the status pane) are icing, never the
architecture. Project is the noun most underserved today (add and set exist; there
is no way to see what projects are registered or inspect one), so this task starts
there.

Design constraints:
- StatusCommand's Compose logic (bucket refinement from run state) is reused for
  every per-task Status column and every rollup count - do not fork the composition
  rules; one truth about what state a task is in.
- Keep attention-first ordering (NeedsHuman, stalled, active) in the narrowed
  status; task list orders newest-first for browsing.
- project show's settings pane reads from ProjectDetails - if a setting the
  decider accepts is missing from the projection, fix the projection rather than
  querying the stream ad hoc.
- Owner, connection, and node management surfaces (h9k owner show, h9k connection
  list, ...) are the same noun-first principle applied to the remaining aggregates -
  deliberately a follow-on task, not scope here; note it in the PR description.
- Command wiring in Program.cs follows the existing branch/leaf conventions.
- After backlog 05 lands, list/rollups will need Draft/Published/Blocked awareness;
  do not pre-build for states that do not exist yet.
