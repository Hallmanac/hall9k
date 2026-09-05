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
4. Verify each commit builds if there is any doubt about ordering: building the working tree after committing does not prove the commit itself builds, since the working tree still holds every later commit's uncommitted changes, staged or not — build the commit's own tree in isolation instead. `git worktree add <throwaway-path> <commit-sha>` and `dotnet build` there, then `git worktree remove <throwaway-path>`, isolates cleanly without touching the stash at all. A tagged stash works too: `git stash push -u -m "<unique-tag>"` the remaining uncommitted changes, build the tree that is left, then find the entry by tag (`git stash list --format='%gd %H %gs'` — `%gd` is the `stash@{n}` selector itself, not just the SHA and subject), restore with `git stash apply <sha>`, and afterwards drop it by re-finding its current `stash@{n}` via the same tag lookup and passing that to `git stash drop` — `drop`, unlike `apply`, only accepts the `stash@{n}` reflog form, not the commit SHA. Never use a bare `git stash` / `git stash pop` — the stash stack is shared across every worktree cut from this project's bare repository, so a concurrent build session's bare `pop` can apply a different session's stashed changes into this worktree, or this one's into another's.

## Verifying a recompose (when this skill is the recompose step)

AGENTS.md's checkpoint-commit workflow invokes this skill to recompose a tree that was just
`git reset --mixed` to a recorded pre-reset tip (the fork point), turning checkpoint commits
into real history. That caller, or any other reset-and-recompose caller, is not finished once
the commits above are made. It is finished only once **both** of these pass:

1. `git diff <pre-reset-tip> HEAD` — must print nothing.
2. `git status --porcelain` — must print nothing.

**Check 1 alone is not enough, and treating it as the whole verification is the exact defect
this section exists to close.** The diff compares committed trees only. A composed commit
that forgets to `git add` a file leaves that file sitting untracked in the working tree
rather than reverted — an untracked file never appears in a diff between two commits, so the
diff reads empty whether or not the file actually got committed. Check 2 is the one that
actually catches a forgotten file: it inspects the working tree itself, not the commit graph,
so an omitted file shows up there even when the diff is silent about it.

If check 2 finds anything, a file did not make it into any composed commit: stage it and fold
it into whichever commit should own it, or, if it is a file the plan deliberately excludes,
roll it back (`git checkout -- <path>` / `git clean`) rather than leaving it loose — then
re-run **both** checks again. A clean diff plus a dirty `git status` is still a failed
recompose; never conclude with either check failing.

Origin incident (2026-09-05, GitHub issue #218): five fix-lap sessions in one night recomposed
a tree, ran only the diff check, found it empty, and concluded with up to seven files sitting
uncommitted — the diff check was structurally blind to the omission, so it could not have
caught it no matter how carefully it was read. `VerificationRunner`'s dirty-worktree guard
caught each one after the session had already exited, and `h9k task retry` recovered them,
but the session itself should never have believed its work was committed.
