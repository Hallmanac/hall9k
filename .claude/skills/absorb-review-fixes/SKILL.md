---
name: absorb-review-fixes
description: Fold review-feedback fixes into the branch commits that own them (fixup + autosquash + tree-identity check) so the PR branch stays authored history. Use on a follow-up when fixes are ready to commit and the project uses the narrative commit style.
---

# Absorb Review Fixes

Land review-feedback fixes as authored history: each fix disappears into the commit that
owns it, so the PR branch reads as a natural progression of the whole change with no
"address review feedback" commits (AGENTS.md Git rules; PLAN.md Decisions Log #26).

Precondition: the working tree holds the fixes (staged or unstaged) on an existing PR
branch, and the project uses the narrative commit style. If the project uses the append
style, do not use this skill; commit normally on top.

## Mapping rule (mechanical, no judgment calls)

1. List the branch's own commits: `git log --oneline origin/<base>..HEAD`.
2. A fix belongs to the **most recent branch commit that touches the same file**
   (`git log -1 --format=%h origin/<base>..HEAD -- <file>`).
3. A fix spanning files owned by **different** commits splits into one fixup per owning
   commit (`git add` per-file, or `git add -p` when one file's hunks split across owners).
4. Genuinely new scope — a new file no branch commit owns — may be a **new,
   properly-titled commit** describing what it adds. Never "review fixes", never
   "address feedback".

## Process

1. For each fix, stage exactly the files (or hunks) mapped to one owning commit and run:

   ```bash
   git commit --fixup=<owning-commit>
   ```

2. With every fix committed, record the pre-rebase tip and fold the fixups in:

   ```bash
   old_tip=$(git rev-parse HEAD)
   GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash origin/<base>
   ```

3. **Verify tree identity** — required before anything else happens:

   ```bash
   git diff "$old_tip" HEAD   # must print NOTHING
   ```

   An empty diff proves the rebase reordered history without changing content, so test
   runs and verification gates that passed against `$old_tip` honestly describe the new
   tip. A non-empty diff means a fixup landed wrong (usually a mis-mapped owner);
   reconcile until the diff is empty — never push a tree that differs from the tested one.

4. Pushing the rewritten branch requires `--force-with-lease` (never plain `--force`; a
   failed lease means the branch moved on origin — stop and re-inspect, don't retry
   blind). **In a Hall9k follow-up run, do NOT push**: the daemon pushes with
   `--force-with-lease` after re-verifying. Push yourself only when working by hand:

   ```bash
   git push --force-with-lease
   ```

Origin incident (2026-08-17): PR #6's pre-merge review round first landed as a separate
"harden closeout" commit and was rebuilt into the owning commits by hand; the same day,
the first two automatic follow-up runs rebased correctly but were failed by the daemon's
then-plain push. This skill plus the daemon's force-aware follow-up push are the fix.
