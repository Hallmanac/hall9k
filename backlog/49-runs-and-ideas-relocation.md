---
project: hall9k
type: feature
objective: New runs live under the task that owns them and idea workspaces and worktrees under the project's home - browsing a task's directory tells its whole story, and historical paths stay untouched observations of the past
criteria:
- A newly dispatched run's directory (prompt, stream, verify logs, review files) is created under its owning task's directory - <home>/tasks/<shortid>-<slug>/runs/<run-id>/ - when the project has a home, falling back to the platform-global location when it does not. A run belongs to exactly one task, so the task's directory is the complete story of the task: contract, workspace, every attempt (ruled 2026-08-23; there is no top-level runs/ in the home)
- A newly captured idea's workspace is created under <home>/ideas/<id>/workspace under the same rule
- Historical records are never rewritten: run documents and idea streams keep the absolute paths they recorded, those paths stay readable, and nothing migrates old directories - relocation applies to NEW records only (the cutover chore moves the one existing project's files separately)
- Worktree creation for dispatched agents happens under <home>/repo/ when the project has a home, with the recorded WorktreePath staying the absolute observation it always was
- h9k logs and every other path consumer reads the recorded path rather than deriving it, so old and new locations coexist without special cases
- dotnet build and dotnet test pass
---
Slice 3 of the project-centred structure (idea 64e4ebd2). The global
~/.hall9k/runs pile becomes per-project for everything new; the past stays
where it happened, recorded honestly.
