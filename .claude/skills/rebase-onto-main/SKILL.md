---
name: rebase-onto-main
description: Rebase an existing pull-request branch onto its moved base branch, resolving conflicts with judgment while preserving authored history and never leaving a conflict marker. Use on a follow-up dispatched because the PR now conflicts with its base.
---

# Rebase onto main

Bring a pull-request branch current with its base branch after other work merged ahead of
it (AGENTS.md Git rules; PLAN.md Decisions Log #26 and backlog 44). This is the inverse of
`absorb-review-fixes`: that skill folds *new* fixes into existing commits, this one replays
*existing* commits onto a *new* base. Both share the same doctrine: authored history stays
intact, tree identity is verified before anything is pushed, and the agent never pushes.

Precondition: the working tree is an existing PR branch that GitHub reports as
`CONFLICTING` against its base (`gh pr view --json mergeStateStatus,mergeable`, or the
follow-up's own dispatch reason). If the branch is merely *behind* but still mergeable,
this skill is not needed; GitHub merges it fine as-is.

## Process

1. **Confirm the base and the conflict.** `git fetch origin`, then
   `git log --oneline origin/<base>..HEAD` to see this branch's own commits and
   `git log --oneline HEAD..origin/<base>` to see what the base gained.

2. **Rebase, replaying this branch's own commits:**

   ```bash
   git rebase origin/<base>
   ```

   This is a plain rebase, not fixup/autosquash: the branch's commits already exist and are
   being replayed onto a new base, so their authorship and the shape of the branch's history
   are preserved by the rebase itself. There is nothing to squash and no new commit to
   create for "the rebase" as an operation.

3. **Resolve each conflict with judgment, not mechanically.** Read what each side actually
   changed and why before touching a hunk:

   - **Keep both** when the two changes are independent and both still make sense together
     (the common shape: a shared file both branches added to in different places).
   - **Pick a side** when one change genuinely supersedes or invalidates the other (a
     rename, a signature change, a rule the other side's addition now violates), and say
     which side won and why in your summary.
   - **Park, do not guess**, when both sides changed the *same behavior*, not just the same
     lines, and picking either would silently drop real work. See "When a conflict is not
     yours to resolve" below.

   Land a resolved conflict inside the commit being replayed (`git add <files>` then
   `git rebase --continue`), never as a separate "resolve conflict" commit. The mapping
   rule `absorb-review-fixes` uses (a change belongs to the commit that owns it) applies
   here too, and here the owner is unambiguous: it is the commit currently being replayed.

4. **Never leave a conflict marker in a commit.** Before every `git rebase --continue`, grep
   the files you just resolved for `<<<<<<<`, `=======`, `>>>>>>>`. Origin incident
   (2026-08-21): a conflict-marker string landed inside a committed file during a hand
   rebase and had to be caught and fixed as its own follow-up. The platform's discipline
   since is that a marker in a commit is never acceptable, checked before it can happen
   rather than caught after.

5. **Verify tree behavior, not just tree identity.** Unlike a fixup rebase, this one
   deliberately changes content (that is the point), so there is no pre-rebase tip to diff
   against. What there is to verify: **run the project's own verification gates against the
   rebased tree before finishing** (the build, the test suite, whatever this project
   configures). Origin incident (2026-08-22): this very feature's own first retry died on 7
   test failures that were main-reconciliation fallout from combining two branches' changes,
   not flakiness; each side had compiled and passed alone, and only the combination broke.
   A rebase that resolves every textual conflict cleanly can still be behaviorally wrong;
   only running the gates catches that.

6. **Do not push.** In a Hall9k follow-up run, the daemon pushes the rebased branch with
   `git push --force-with-lease` after re-verifying (Decisions Log #26), the same push path
   every other follow-up rewrite uses. Push yourself only when working by hand:

   ```bash
   git push --force-with-lease
   ```

## When a conflict is not yours to resolve honestly

Some conflicts are not a merge mechanic; they are two people's work disagreeing about what
the code should do. If both sides changed the same behavior (not just the same lines) and
you cannot honestly tell which should win: resolve every conflict you can first, then stop.

Abort the rebase if you have not finished it (`git rebase --abort`), so the branch is left in
its last-known-good state rather than half-rebased, then close your summary with a line
reading exactly

```
RESOLUTION: disputed
```

Above it, name **every conflicting file**, what each side changed and why (read both
commits' own history, not just the diff), and what you would do instead and why. When this
skill is running inside a Hall9k follow-up run, that marker parks the run with your text
saved beside it and nothing is pushed until a human decides (`h9k review resolve`). Park at
most once: this is one honest attempt, not a negotiation.

When every conflict is resolved and the gates pass, close your summary with

```
RESOLUTION: fixed
```

instead, plus a short account of what conflicted and how each was resolved.

## Origin incidents

- **2026-08-22, PR #26 (the incident this skill exists for).** A pull request sat
  `AwaitingReview` with every review thread resolved and nothing left but approval, and was
  silently unmergeable, because three other PRs had merged into main after its branch was
  cut. Nothing observed the conflicting state, nothing surfaced it, and a human ended up
  doing the rebase by hand: five keep-both conflict stops across a shared seam. That toil,
  and the fact that it recurs on every project with more than one lane merging, is why the
  platform now detects and dispatches this automatically (`CloseoutEngine`,
  `PullRequestConflictObserved`) instead of leaving it for a human to notice.
- **2026-08-22, the same feature's own build.** The first retry of implementing this very
  capability failed 7 tests that looked like flakiness and were not; they were the fallout
  of combining two branches' changes, surfaced only by actually running the suite after the
  rebase. Step 5 above exists because of exactly this.
- **2026-08-21, marker-commit incident.** A hand-resolved conflict landed with a stray
  conflict-marker string still inside a committed file. Step 4 above is the check that
  catches this before a commit exists, not after.
