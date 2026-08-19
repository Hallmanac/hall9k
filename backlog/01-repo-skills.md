---
project: hall9k
type: chore
objective: Commit repo-resident Claude skills so every agent worktree carries the git workflow skills
criteria:
- A .claude/skills/ directory exists in the repo with at least commit-plan, resolve-copilot-reviews, and pr-summary skills
- Each skill is adapted for agent use (no personal paths, no user-specific assumptions; works from any worktree)
- A claude session started in a fresh worktree lists the skills as available
- AGENTS.md gains a short section naming the skills and their purpose
---
The owner's user-level skills (~/.claude) named git:commit-plan, git:resolve-copilot-reviews,
and git:pr-summary are the starting material — read them and adapt rather than invent.
Repo-resident skills version with the code and reach every node (macOS, the future Windows
node, any teammate), independent of whoever's home directory.

Do NOT adapt git:create-pr — the daemon opens PRs (PullRequestOpener); agents are
explicitly forbidden from opening PRs themselves.

Boundaries: skills only + the AGENTS.md section. No changes to the daemon, CLI, or domain.
