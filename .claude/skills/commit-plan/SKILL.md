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

AGENTS.md's checkpoint-commit workflow records a pre-reset tip (`git rev-parse HEAD`), then
resets to the branch's own fork point (`git merge-base origin/<base> HEAD` — ordinarily an
earlier, separate commit from the pre-reset tip; if the branch carries no checkpoint commits
of its own yet, the fork point and the pre-reset tip are the same commit and the reset is a
no-op, worth noticing rather than assuming away) before invoking this skill to
recompose the checkpoint commits into real history. That caller, or any other
reset-and-recompose caller, is not finished once the commits above are made. It is finished
only once **both** of these pass:

1. `git diff <pre-reset-tip> HEAD` — must print nothing.
2. `git status --porcelain` — must print nothing.

**Check 1 alone is not enough, and treating it as the whole verification is the exact defect
this section exists to close.** Check 1 does catch a composed commit that forgot to stage a
file the pre-reset tip already held committed — that file reappears in the diff as a
deletion, not silence. The actual blind spot is narrower: a file that was never
checkpoint-committed before the pre-reset tip was recorded (created afterward, or left
staged or unstaged and never committed at all). Such a file was never part of the pre-reset
tip's tree to begin with, so it cannot appear in a diff against that tree either way — a
mixed reset never touches the working tree, so the file just sits there through the reset
and the recompose alike (untracked if it is genuinely new, modified if it is an uncommitted
edit to a file the fork point already tracked — `git status --porcelain` shows the first as
`??` and the second as ` M`, not the same thing), with the diff silent about it the whole
time either way. Check 2 is the one that actually catches this: it inspects the working
tree itself, not the commit graph, so the file shows up there even when the diff is silent
about it.

**A non-empty check 1 with a clean check 2 is not automatically a pass, and it is not
automatically a failure either — read what the diff shows before concluding.** The normal
`Process` above (step 1: inspect `git status`) already picks up a file that was never
checkpointed before the pre-reset tip was recorded, right alongside everything else, and
step 3 commits it as part of the plan like any other file — there is nothing wrong with
that commit. But the pre-reset tip predates that content, so `git diff <pre-reset-tip> HEAD`
will show it as newly added forever, no matter how correctly it was composed; that tip
cannot be satisfied by any recompose from here, because it never claimed to cover content
it never held. If everything the diff shows is exactly that kind of legitimate addition —
material `git status` surfaced during step 1 and the plan folded in on its own commit, not a
deletion and not something you cannot account for from that inspection — record a new
pre-reset tip right here (`git rev-parse HEAD`) and treat the recompose as finished; there is
nothing further to prove against the old tip once a fresh one exists to measure from. If the
diff instead shows a deletion, or content you cannot trace back to step 1's own inspection,
treat it as a real failure the same as any other: something was dropped or mis-composed, and
it needs fixing and re-verifying, not a fresh tip papering over it.

If check 2 finds anything, decide which of two things it is before touching it:

- **It belongs in this branch, and was never checkpointed before the pre-reset tip was
  recorded.** Do not fold it into an already-composed commit — the pre-reset tip never held
  it, so doing so makes check 1 permanently non-empty over content that legitimately
  belongs. Instead, checkpoint-commit it now (an ordinary checkpoint commit), record a *new*
  pre-reset tip (`git rev-parse HEAD`), and redo the reset-and-recompose from there: this
  content only becomes eligible for the diff check once it has been through a pre-reset tip
  of its own.
- **It was never meant to land** (a scratch file, a plan-excluded leftover). Roll it back
  without touching anything else it did not create: `git checkout HEAD -- <path>` for a
  tracked file carrying unwanted modifications — not `git checkout -- <path>`, which restores
  the working tree from the index and leaves a *staged* unwanted change staged, exactly the
  case this section names as one check 2 catches — `git clean -f -- <path>` for an untracked
  file (always with an explicit pathspec — a bare `git clean` refuses to run under
  `clean.requireForce`, and `git clean -fd` with no pathspec deletes every other untracked
  file in the tree too, including ones still meant to be folded in).

Then re-run **both** checks again. A clean diff plus a dirty `git status` is still a failed
recompose; never conclude with either check failing.

Origin incident (2026-09-05, GitHub issue #218): five fix-lap sessions in one night recomposed
a tree, ran only the diff check, found it empty, and concluded with up to seven files sitting
uncommitted — those files were never checkpointed before the pre-reset tip was recorded, so
the diff check was structurally blind to them no matter how carefully it was read.
`VerificationRunner`'s dirty-worktree guard caught each one after the session had already
exited, and `h9k task retry` recovered them, but the session itself should never have
believed its work was committed.
