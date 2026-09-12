===heading===
# Follow-up task: replay this stacked branch onto its new base
===intro===
This task's work is a slice of a stack: it was built on top of another task's
branch, and its pull request targeted that branch. That parent branch has now
moved, and this pull request's base is `{{BaseBranch}}`. Your job is exactly one
mechanical operation: replay this branch's own commits onto the new base.
===no-new-intent===
**There is no new intent here and you must not add any.** Nothing in this
session's output is read by a reviewer — the platform deliberately runs no review
cycle over a replay, because git replaying commits that already passed review is
not new work. That is only true if you keep it true: do not refactor, do not
improve, do not fix anything you notice in passing. Note it in your final summary
instead — that is what the summary is for here.
===follow-up-reason===
Why this follow-up was dispatched: {{Reason}}
===original-objective-heading===
## Original objective (context, already implemented and reviewed)
===working-rules-heading===
## Working rules
===worktree-note===
- You are in an isolated git worktree checked out on the EXISTING pull-request
  branch `{{Branch}}`. Work only here.
===replay-shape===
- The replay, in this exact shape:
===fetch-both-commits===
  - `git fetch origin` first, so both commits below are present locally.
===record-what-replays===
  - Record what you are about to replay, so you can check nothing was lost:
    `git log --oneline {{UpstreamCommit}}..HEAD`. Those commits — and only those —
    are this task's own work.
===rebase-onto-command===
  - `git rebase --onto {{OntoCommit}} {{UpstreamCommit}} {{Branch}}`
===boundary-explanation===
    Both arguments are exact commits, not branch names, and neither is negotiable.
    `{{UpstreamCommit}}` is the boundary: everything at or before it is the parent's
    work, which `{{OntoCommit}}` already holds. A plain `git rebase` without it would
    replay the parent's commits a second time; a different boundary would drop this
    task's own commits. `{{OntoCommit}}` is the commit on `{{BaseBranch}}` this branch
    is meant to land on — do not substitute the branch name, which may have moved
    since; if the base has moved, the platform's own machinery brings the branch
    current afterwards, exactly as it does for any other run.
===resolve-conflicts===
  - Resolve any conflict by reading both sides' intent — keep both changes when
    both are still wanted, take the side that is still correct when one supersedes
    the other. A resolved conflict's content belongs inside the commit being
    replayed (`git add`, then `git rebase --continue`); never invent a "resolve
    rebase" commit, and never leave a conflict marker behind — grep the resolved
    files for the three marker sequences before continuing.
===check-replay===
  - Check the replay afterwards: `git log --oneline` must show this task's own
    commits and nothing of the parent's, and the count must match what you
    recorded above (a commit git drops as already-present upstream is the one
    legitimate exception — say which, and why, in your summary).
===fold-reason===
    — the commit this replay landed on, which is this branch's boundary afterwards.
    Do NOT name `origin/{{BaseBranch}}`: it is the parent's own branch, and a force-push
    moves it out from under the fold, which would rebase the parent's commits into
    this branch's authored history
===no-push===
  - Do NOT push (the platform pushes the replayed branch with
    `git push --force-with-lease` after re-verifying), and do NOT open a new pull
    request — the existing one updates in place, already retargeted.
===stop-if-blocked===
- **If the replay cannot be made to work, stop and say so plainly** rather than
  forcing something through. Leave the worktree clean (`git rebase --abort`), and
  name in your final summary exactly what blocked it. A replay nobody reviews must
  never ship a result you are unsure of.
===closing-summary===
- End with a short summary: which commits replayed, anything git dropped and why,
  what conflicted and how you resolved it, the verification results, and anything
  you noticed but deliberately did not touch.
