---
name: commit-plan
description: Organize the working tree's changes into cohesive, buildable commits ordered for easy PR review. Use when a task's changes are ready to commit, especially when they span multiple concerns (domain + CLI + tests + docs).
---

# Commit Plan

Organize the current changes in this repository into cohesive, buildable commits. Each commit must:

1. Be logically cohesive based on functionality, not file location.
2. Build and pass on its own (`dotnet build` succeeds at every commit; no missing project references, no handler without the events it consumes).
3. Respect dependency order:
   - Events and aggregates before the handlers/deciders that use them
   - Domain changes before CLI/daemon changes that consume them
   - Infrastructure and configuration before the code that depends on it
   - Package pins in `Directory.Packages.props` in the same commit as the first code that needs them
4. Include new files AND their corresponding project/solution file updates together.
5. Keep unit tests in the same commit as the functionality they cover, unless explicitly asked to separate them.
6. Label pure cleanup, configuration, or non-functional commits as `chore:` — but if a cleanup sits inside a hunk already being changed for the feature, leave it in the feature commit rather than contorting the diff.
7. Be ordered so a reviewer reading the PR commit-by-commit follows the change naturally.

## Commit message format

- Title: brief summary, 50 chars or so (longer is fine for complex changes). If the branch or task names a slice task ID (e.g. `S1-10`), lead with it: `S1-10: <summary>`. Otherwise use conventional prefixes (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`) where they add clarity.
- Blank line, then a short summary paragraph.
- Bullet points (markdown dashes) explaining what changed and why, when the title alone isn't enough. Small commits can skip the body.
- Do not mention project-file updates or that files were "added to projects".
- **No attribution trailers of any kind** — no `Co-Authored-By`, no "Generated with" footers, no bot attribution. Commits are authored as the repo owner (PLAN.md §6.6; hard rule).

## Process

1. Inspect the full change set: `git status` and `git diff` (plus `git diff --stat` for shape).
2. Draft the plan: for each commit, list the files included and the commit message, in order.
3. State the plan briefly in your output, then execute it commit by commit — stage exactly the planned files (`git add <paths>`, using `git add -p` when a file's hunks split across commits) and commit. Do not wait for approval unless the session is interactive and the human asked to review the plan first.
4. Verify each commit builds if there is any doubt about ordering: build after each commit (e.g. `dotnet build`). Never use `git stash` to check — the stash stack is shared across every worktree cut from this project's bare repository, so a concurrent build session's `git stash pop` can apply a different session's stashed changes into this worktree, or this one's into another's.
