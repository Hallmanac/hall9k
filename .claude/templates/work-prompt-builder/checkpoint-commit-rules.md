===commit-as-you-go===
- **Commit as you go, one logical unit at a time.** Each commit here is
  crash protection, not authored history: a checkpoint so that an abnormal
  ending (context exhaustion, an early exit) strands at most the increment
  since the last checkpoint instead of the whole session. Message them
  plainly; none of them are what ships.
===recompose-heading===
- **Once all the work is done, the full verification suite is green, and the
  self-review phase above has run its course, recompose the checkpoints into
  real history in one continuous step.**
===no-gates===
  This project configures no verification gates, so the suite is
  vacuously green — recompose once the work itself is done.
===gates-heading===
  The gates that must pass first:
===step0===
  0. With every last increment committed as a checkpoint — `git status` must show
     nothing uncommitted or untracked before this step, or step 3 below will fail
     against a tip that never held it: a new, never-`git add`ed file under src/ or
     tests/ fails the final contract outright, and even one outside those trees that
     only warns there would still recompose into the new history while `old-tip`
     predates it, so the diff comes back non-empty for something that was added,
     not omitted. Record the pre-reset tip: `git rev-parse HEAD` — step 3 checks
     against it, so this is not optional bookkeeping.
===step1-stacked===
  1. Reset to the branch's own fork point — the commit this branch was cut from,
     recorded when it was cut: `{{StackedForkPointCommit}}`. This branch is stacked on
     `{{BaseBranch}}` rather than based on the project's own base branch, so the fork
     point is named here as a literal commit and must NOT be computed as a merge
     base against `origin/{{BaseBranch}}`: a parent branch is routinely
     force-pushed while its child builds (a review lap folding fixes into its own
     commits), which rewrites the history this branch shares with it and collapses
     that merge base BELOW this branch's real fork point. A reset there would
     dissolve the parent's commits along with this session's own, and the
     commit-plan step would recompose the parent's already-reviewed work as this
     branch's own authored history — which step 3 cannot catch, because a mixed
     reset never moves the tree. Verify the commit resolves and stop if it does
     not — never inline the substitution directly into the reset, since
     `git reset --mixed $(...)` on an empty substitution silently becomes a bare
     `git reset --mixed` — which resets to HEAD, changes nothing, and exits 0 as
     though the recompose had happened, with step 3's diff unable to catch it
     (the diff would compare HEAD against itself and read clean):
     `FORK_POINT=$(git rev-parse --verify "{{StackedForkPointCommit}}^{commit}")`
     `test -n "$FORK_POINT" || { echo "the recorded fork point does not resolve — stop here, do not reset" >&2; exit 1; }`
     `git reset --mixed "$FORK_POINT"`
     A mixed reset changes which commits exist and
     leaves the working tree exactly as it is, so the tree itself does not move.
===step1-unstacked===
  1. Reset to the branch's own fork point, not the tip of `origin/{{BaseBranch}}`
     itself: that ref lives in the shared repository and can move during this
     session (another worktree's fetch, a closeout branch cleanup), and resetting
     straight to its tip would recompose commits that revert whatever merged into
     the base after this branch was cut. The fork point does not move. Capture it
     into a variable and stop if it does not resolve — never inline the
     substitution directly into the reset: an unresolved `origin/{{BaseBranch}}`
     makes `git merge-base` print nothing and exit nonzero, and
     `git reset --mixed $(...)` on an empty substitution silently becomes a bare
     `git reset --mixed` — which resets to HEAD, changes nothing, and exits 0 as
     though the recompose had happened, with step 3's diff unable to catch it
     (the diff would compare HEAD against itself and read clean):
     `FORK_POINT=$(git merge-base origin/{{BaseBranch}} HEAD)`
     `test -n "$FORK_POINT" || { echo "no fork point resolved — stop here, do not reset" >&2; exit 1; }`
     `git reset --mixed "$FORK_POINT"`
     A mixed reset changes which commits exist and
     leaves the working tree exactly as it is, so the tree itself does not move.
===step2===
  2. Immediately invoke the commit-plan skill, if this repo ships one, to compose
     that tree into cohesive, buildable commits — the real, reviewable history for
     this PR — or compose them yourself the same way if it does not.
===step3===
  3. REQUIRED before you finish: verify tree identity — `git diff <old-tip> HEAD`
     (the tip recorded in step 0) must print nothing, exactly the same check the
     narrative commit style requires after a rebase. A mixed reset changes only
     which commits exist, never the tree, so an empty diff should be automatic —
     but a file the commit-plan step forgot to stage lands as untracked rather
     than modified, which this diff catches and a plain `git status` glance can
     miss. A non-empty diff cuts two ways: something `old-tip` had that the
     recompose is missing means the commit-plan step forgot to stage it — add it
     and recompose again before finishing. Something the recompose has that
     `old-tip` never held means step 0's clean-tree check was skipped; there is no
     local fix for that here, redo the recompose from a tip recorded once that
     content was itself committed as a checkpoint, not folded in at this step.
     Check `git status --porcelain` too, right here, and treat any untracked file
     it shows as the same failure: the platform's own gate fails outright on one
     under src/ or tests/, and only warns on one elsewhere (a build byproduct can
     legitimately be one there), so this file forgotten by the recompose is the
     check that actually stops it before it ships.
===between-steps===
  Nothing happens between steps 1 and 2: no test run, no fix, no exploration.
  That gap is exactly what the reset is for: because the tree never moves,
  the commits composed in step 2 describe the identical tree that passed the
  suite before step 1, and anything done in between would break that
  guarantee. If something genuinely must change after the reset, commit
  everything as it stands first, then make the change and recompose again.
===final-clean-tree-rule===
- **The session is not done while `git status` shows anything uncommitted or
  untracked.** Check it last, after the recompose above, and commit whatever
  it still shows before your final message. A clean tree is the contract, not
  a nice-to-have.
