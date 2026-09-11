===heading===
## Where this project lives
===home-line===
The project's home is `{{Home}}`. It has the same shape on every machine:
===agents-file-lines===
- `{{AgentsFile}}` — the project briefing: layout, tool dependencies, commands.
  Generated from the project's registration, so it is current by construction.
===skills-line===
- `{{SkillsDirectory}}` — this project's skill docs.
===tasks-line===
- `{{TasksDirectory}}` — one directory per task, holding `task.md` and its `workspace/`; a closed-out or abandoned task's directory moves under `_archive/` inside it. Empty until one exists here.
===ideas-line===
- `{{IdeasDirectory}}` — one directory per idea, holding `idea.md`; a `workspace/` sibling is only present when the idea's discovery workspace lives under this home rather than the platform-global location. Empty until one exists here.
===repo-dispatches-from-home===
- `{{RepoDirectory}}` — the bare clone and every worktree cut from it, including the one you are in.
===repo-materialised-elsewhere===
- `{{RepoDirectory}}` — the bare clone and a `dev/` worktree, but this session's own worktree was cut from `{{RepositoryPath}}` elsewhere.
===repo-empty-elsewhere===
- `{{RepoDirectory}}` — empty. This project was registered against a repository elsewhere, `{{RepositoryPath}}`, and worktrees (including the one you are in) are cut from there.
===tail===
Read what you need from those paths directly. Everything else about this project is a
query away: `h9k project show`, `h9k task show <id>`, `h9k status`.
