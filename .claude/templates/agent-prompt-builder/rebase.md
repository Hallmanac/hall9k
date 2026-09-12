===heading===
# Follow-up task: rebase an existing pull request onto its base branch
===intro-lead===
The original task below already shipped in the pull request above, but its branch
===intro-stacked===
now conflicts with `{{BaseBranch}}` — the branch this task is stacked on,
which has moved since this branch was cut. Your job is to bring it current,
preserving the branch's own authored history — not to redo the original work.
===intro-unstacked===
now conflicts with `{{BaseBranch}}` — other work merged into the base since
this branch was cut. Your job is to bring it current, preserving the branch's own
authored history — not to redo the original work.
===follow-up-reason===
Why this follow-up was dispatched: {{Reason}}
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
===worktree-note===
- You are in an isolated git worktree checked out on the EXISTING pull-request
  branch `{{Branch}}`. Work only here.
===rebase-skill-pointer===
- If the repo ships a rebase-onto-main skill (or an absorb-review-fixes skill that
  covers rebasing), invoke it — it walks these exact mechanics. Either way:
===fetch-first===
  - `git fetch origin` first — a resumed dispute is dispatched straight into this
    worktree, so this session cannot assume anything already fetched for it, and
    rebasing onto a stale `origin/{{BaseBranch}}` can leave the pull request
    still conflicting after the rebase reports success.
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
===inline-fold-reason===
    — the `origin/{{EffectiveBaseBranch}}` head you rebased onto, named as the literal
    commit you wrote down, and NOT the fork point above: the replay has moved this
    branch off that fork point, and `origin/{{EffectiveBaseBranch}}` is another task's
    branch that can move again while you work. Folding from either one would rebase
    the parent's own commits into this branch's authored history
===no-push===
  - Do NOT push (the platform pushes the rebased branch with
    `git push --force-with-lease` after re-verifying), and do NOT open a new pull
    request — the existing PR updates in place.
===closing-summary===
- End with a short summary: what conflicted, how you resolved each conflict and
  why, and the verification results.
===stacked-skill-caveat===
  - That skill's own rebase step assumes a branch cut off the project's base branch.
    This one is not, so its plain-rebase step is the one part of it that does NOT
    apply — use the operation below in its place. Everything else it teaches
    (conflict judgment, no markers, the gates) applies unchanged.
===no-fork-point===
  - **Do NOT run `git rebase origin/{{BaseBranch}}`, and do not rebase this branch
    at all.** This branch is stacked on `{{BaseBranch}}` — another task's branch,
    not the project's own base — and a parent branch is routinely force-pushed
    while its child is in flight (a review lap folding fixes into its own
    commits), which rewrites the history this branch shares with it and collapses
    the merge base BELOW this branch's real fork point. A plain rebase from there
    replays this branch's own copies of the parent's OLD commits against the
    parent's new ones: it either conflicts on the parent's own content or lands
    the parent's history on this branch twice. The correct operation is a replay
    from this branch's fork point, and that fork point was never recorded for
    this run — `git merge-base` cannot recover it, so there is no boundary left
    that is not a guess. Take the dispute path below instead: say plainly that
    the replay boundary is unobserved and that this branch is stacked, and let a
    human decide it (they can rebase and retarget by hand, or name the boundary
    commit in their resolution, which a resumed attempt is handed above). The
    rebase mechanics below apply only if their decision names one; with no
    boundary, the dispute is the whole job. If it does name one, use that commit
    everywhere the mechanics below name `origin/{{BaseBranch}}` as a rebase or fold
    target — that ref is this stack's parent branch, and it is not safe as either.
===record-commit===
  - Record the commit you are about to land on, before you rebase:
    `git rev-parse origin/{{BaseBranch}}`. It is this branch's own boundary
    afterwards, and the fold instruction further down needs it. A shell variable does
    not survive between separate tool calls, so write the value down rather than
    exporting it.
===replay-and-checks===
  - `git rebase --onto origin/{{BaseBranch}} {{ForkPointCommit}} {{Branch}}` — a replay from
    this branch's own recorded fork point, NOT `git rebase origin/{{BaseBranch}}`. This
    branch is stacked on `{{BaseBranch}}` — another task's branch, not the project's own
    base — and a parent branch is routinely force-pushed while its child is in
    flight (a review lap folding fixes into its own commits), which rewrites the
    history this branch shares with it and collapses the merge base BELOW this
    branch's real fork point. A plain rebase from there replays this branch's own
    copies of the parent's OLD commits against the parent's new ones: it either
    conflicts on the parent's own content or lands the parent's history on this
    branch twice. `{{ForkPointCommit}}` is the commit this branch was cut from,
    recorded when it was cut: everything at or before it is the parent's work, which
    `origin/{{BaseBranch}}` already holds. Check that this branch actually SITS on
    that commit before you rebase — containment, not merely that it resolves — and
    never substitute a computed merge base for it:
    `git merge-base --is-ancestor {{ForkPointCommit}} HEAD` (exit 0 means yes).
    If that check fails, this branch never landed on the recorded commit —
    ordinarily an earlier replay that aborted its own rebase, leaving the record
    naming where it was TOLD to land rather than where this branch is. Do not
    rebase from it anyway: the range would still carry the parent's own commits.
    Take the dispute path below and say the boundary is unobserved, exactly as you
    would if none had been recorded at all.
  - If `origin/{{BaseBranch}}` does not resolve after the fetch, the parent's branch is
    gone from origin — ordinarily because its pull request merged and closeout
    deleted it. Do NOT retarget this pull request yourself (the platform does that,
    mechanically, when it next observes the parent) and do not replay onto a branch
    you cannot read. Take the dispute path below and say what you found.
  - Resolve each conflict by reading both sides' intent, not by mechanically picking
    one. Keep both changes when both are still wanted, take the side that is still
    correct when one supersedes the other, and never guess when you cannot honestly
    tell which — see the dispute path below.
  - Check the replay afterwards: `git log --oneline` must show this branch's own
    commits and nothing of the parent's, and the same count you started with.
===resumed-stacked-fold-reason===
    That is a literal commit — this branch's own fork point off the branch it is
    stacked on. Do NOT name `origin/{{EffectiveBaseBranch}}` here: it is another task's
    branch, routinely force-pushed while this branch is in flight, and the fold's merge
    base against it collapses below this branch's own commits the moment it is — which
    would fold the parent's already-reviewed work into this branch's authored history.
===no-verification-gates===
  - This project configures no verification gates of its own; re-read the diff
    around every resolved conflict once more before finishing.
===required-before-finish===
  - **Required before you finish**: re-run the project's verification gates against
    the rebased tree and fix whatever they surface. A clean-looking rebase can still
    break the build — each side compiled alone; combined is what you are testing now:
===commit-fix-note===
  - **Commit any such fix — never leave it uncommitted.** The platform pushes only
    what is committed, so a gate fix left in the working tree ships neither committed
    nor pushed, and the pull request goes out still broken.
===append-style-fix===
    This project uses the append commit style: land the fix as its own commit on
    top, with a clear message naming what the rebase's combination broke.
===narrative-style-fix-lead===
    This project uses the narrative commit style, so the fix belongs inside the
    commit whose replay produced the failure, not a new "fix tests" commit: if
    you are still mid-rebase, `git add` it and continue; if the rebase already
    finished, commit the fix with `git commit --fixup=<owning-commit>` against
    the commit whose replay produced the failure, then fold it in with
===fold-command===
    `GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash {{Argument}}`
===trailing-editor-note===
    (there is no terminal in this session, so a bare `git rebase -i` cannot open
    an editor).
===dispute-lead===
- **When a conflict is not yours to resolve honestly**: both sides changed the same
  behavior (not just the same lines), and keeping either one, or a naive combination
  of both, would be a guess about which change should win. Do not guess. Resolve
  every conflict you honestly can first, then, if one is genuinely undecidable, stop
  the rebase (`git rebase --abort` if you have not finished it) and close your
===dispute-marker-line===
  summary with a line reading exactly `{{DisputeMarker}}` (the last line of the
===dispute-mid===
  summary, above the HANDOFF block). Above that line, name every conflicting file,
  what each side changed and why, and what you would do instead and why.
  The platform parks the run for a human with that text saved beside the run, and
  nothing is pushed until they decide. They resume it with
  `h9k review resolve {{NeedsFixesFlag}} "<their resolution>"`, which dispatches a fresh
  rebase attempt carrying their decision.
===resolved-line===
  When you resolved everything, close the summary with `{{ResolvedMarker}}` instead.
===dispute-tail===
  One honest attempt per conflict, not a negotiation: never park twice over the SAME
  conflict a previous attempt already disputed. Parking again over a DIFFERENT
  conflict this attempt hit is not a second negotiation over the first one — it is
  honest, and picking a side instead to avoid a second park would silently drop one
  side's work.
