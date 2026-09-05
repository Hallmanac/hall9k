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

3. **Verify tree identity — both of these, not just the first — required before anything
   else happens:**

   ```bash
   git diff "$old_tip" HEAD        # must print NOTHING
   git status --porcelain          # must print NOTHING
   ```

   An empty diff proves the rebase reordered history without changing content, so test
   runs and verification gates that passed against `$old_tip` honestly describe the new
   tip. A non-empty diff means a fixup landed wrong (usually a mis-mapped owner);
   reconcile until the diff is empty — never push a tree that differs from the tested one.

   **The diff alone is blind to a fixup that never got staged.** A fix left uncommitted or
   only partially staged during step 1 does not show up as a difference between `$old_tip`
   and `HEAD` — it was never in either tree to begin with — so the diff can read empty while
   a review fix is sitting loose in the working tree, unabsorbed and about to be lost the
   moment the branch is pushed. `git status --porcelain` is what actually catches this: it
   inspects the working tree, not the commit graph.

   If it shows a leftover that was never meant to land, discard it and re-run both checks.
   If it shows a genuine fix that missed step 1, do not just fold it into its owning commit
   and re-diff against the original `$old_tip` — that tip never held this fix either, so the
   diff would stay non-empty forever over content that legitimately belongs. Instead: stage
   it and commit the fixup, and record the new baseline **immediately after that commit and
   before the autosquash rebase** — recording it after the rebase instead makes the diff
   compare `HEAD` against itself, which is empty no matter what the rebase did to the tree
   and proves nothing:

   ```bash
   git commit --fixup=<owning-commit>
   new_baseline=$(git rev-parse HEAD)
   GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash origin/<base>
   ```

   Treat the result as an untested tree and re-run the project's verification gates against
   it: the working tree held this fix already (the precondition above), but only as an
   uncommitted or partially-staged change, so nothing has yet proven the tree green *with
   this fix committed* until the gates run again. Once they pass, re-run both checks against
   `$new_baseline`, not `$old_tip`:

   ```bash
   git diff "$new_baseline" HEAD   # must print NOTHING
   git status --porcelain          # must print NOTHING
   ```

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
