===heading===
# Rebase this branch onto its base before the mandatory final review pass
===with-pr-intro===
This run already has the pull request above open and pushed. But other work
merged into `{{BaseBranch}}` since, and a plain rebase onto it just
conflicted. Your job is to bring this branch current before the platform's own
mandatory final review pass and gates run, preserving the branch's own authored
history — not to redo the original work.
===without-pr-intro===
This run's own work is not done yet — no pull request has opened, and nothing has
been pushed. But other work merged into `{{BaseBranch}}` while this run was
building, and a plain rebase onto it just conflicted. Your job is to bring this
branch current before the platform's own mandatory final review pass and gates run,
preserving the branch's own authored history — not to redo the original work.
===human-decision-heading===
## The human's decision on the disputed conflict
===human-decision-intro===
A previous attempt at this rebase hit a conflict it could not honestly resolve
and parked for a human. Apply their decision below instead of re-litigating it;
only raise a new dispute if you hit a DIFFERENT conflict that is genuinely
undecidable.
===original-objective-heading===
## Original objective (context, already implemented)
===project-links-heading===
## Project links (fetch yourself as needed)
===working-rules-heading===
## Working rules
===worktree-lead===
- You are in this run's own git worktree, checked out on its own in-progress
===worktree-with-pr===
  branch `{{Branch}}` — already pushed and open as the pull request above. Work only here.
===worktree-without-pr===
  branch `{{Branch}}` — not yet pushed anywhere. Work only here.
===rebase-in-progress===
- **A rebase looks to still be in progress here** — an earlier attempt to abort it
  before you were spawned could not be confirmed clean. Run `git status` first and
  finish or abort whatever it finds (`git rebase --continue` once every conflict in
  the current commit is resolved, or `git rebase --abort` to start over) before
  doing anything else. If the repo ships a rebase-onto-main skill (or an
  absorb-review-fixes skill that covers rebasing), invoke it — it walks these exact
  mechanics. Either way, once the worktree is clean:
===rebase-not-in-progress===
- The worktree is already back at this branch's own tip (an earlier plain rebase
  attempt that conflicted was aborted before you were spawned) — there is no rebase
  already in progress here. If the repo ships a rebase-onto-main skill (or an
  absorb-review-fixes skill that covers rebasing), invoke it — it walks these exact
  mechanics. Either way:
===fetch-first===
  - `git fetch origin` first — rebasing onto a stale `origin/{{BaseBranch}}`
    can leave the branch still conflicting after the rebase reports success.
===plain-rebase===
  - `git rebase origin/{{BaseBranch}}`, resolving each conflict by reading
    both sides' intent, not by mechanically picking one. Keep both changes when both
    are still wanted, take the side that is still correct when one supersedes the
    other, and never guess when you cannot honestly tell which — see the dispute
    path below.
===replay-rules===
  - The rebase replays this branch's own commits onto the new base; it must keep
    doing exactly that. Do not squash it into one commit and do not invent new
    "merge conflict" or "resolve rebase" commits — a resolved conflict's content
    belongs inside the commit being replayed when it lands (`git add` then
    `git rebase --continue`).
===no-markers===
  - **Never leave a conflict marker (`<<<<<<<`, `=======`, `>>>>>>>`) in a commit.**
    Before continuing past any conflicted commit, grep the resolved files for those
    markers and confirm none remain.
===no-push-with-pr===
  - Do NOT push (the platform pushes the rebased branch with
    `git push --force-with-lease` after re-verifying), and do NOT open a new pull
    request — the existing PR updates in place.
===no-push-without-pr===
  - Do NOT push, and do NOT open a pull request — the platform's own mandatory
    final gate and review pass run over the tree you leave behind, and the daemon
    pushes and opens the pull request itself once everything is green.
===closing-summary===
- End with a short summary: what conflicted, how you resolved each conflict and
  why, and the verification results.
