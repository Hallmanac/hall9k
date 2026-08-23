---
project: hall9k
type: feature
objective: Every project owns a home directory in a consistent shape - repo, ideas, tasks, runs, skills, and a generated AGENTS.md - created by platform code at project add, so a project looks identical on every machine and a session started inside it bootstraps itself
criteria:
- A project records a home directory, defaulting to the platform's projects location (~/.hall9k/projects/<name>) and overridable at add time or via project set; h9k project show prints it
- h9k project add (and an init/adopt path for a registered project without a home) creates the shape with platform code end to end, no agent: AGENTS.md, repo/, ideas/, tasks/, runs/, skills/, and a .claude/ adapter directory - the recipe ruling from the installation sitting applies ("this is a recipe, not an agent")
- repo/ is materialised per the bare-clone recipe absorbed from backlog 43: bare clone from the project's remote, fetch refspec corrected to map remotes into refs/remotes/origin/, a dev/ worktree on the primary branch, worktree creation working against it with no further setup, and the whole step idempotent - rerunning reports and exits rather than re-cloning
- The generated AGENTS.md is a RENDER of project facts, never hand-maintained: it names the layout, points at the repo's own AGENTS.md as the deep layer, and lists the tool dependencies derived from the project's bindings (h9k always, gh for GitHub, the Atlassian CLI when a Jira board is bound); it is rewritten when those facts change
- skills/ is seeded from the install's canonical skill set with .claude/skills/ generated as symlinks into it, so h9k install updating the platform updates every project's platform skills; project-specific skills sit beside the seeded ones and survive re-seeding
- A second machine reaches a working project directory from nothing but h9k and the project's remote, using only documented commands
- backlog/43-repo-materialisation.md's contract is absorbed here and its draft task is abandoned with a reason naming this task
- dotnet build and dotnet test pass
---
Slice 1 of the project-centred structure (idea 64e4ebd2; the full discovery
record with every ruling is DISCOVERY.md in that idea's workspace). The project
is the hub: this task builds the hub's physical shape and the recipe that
creates it. Ruled at discovery: hall9k's own home moves to the DEFAULT location
(clean start, not adopt-in-place) via the separate cutover chore; location is a
setting, shape is the contract.
