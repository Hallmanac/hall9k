===no-base-suite-heading===
- **The full verification suite the self-review phase above requires must be
  green before you finish:**
===no-base-reset-rule===
- **This worktree's own commit history could not be read before you were
  dispatched, so once the self-review phase above has run its course there is
  no boundary that is safe to reset to.** Do not run a mixed reset or otherwise
  recompose this branch's history: whatever is already on it — including any
  commits the operator made before this delegation — stays exactly as it is.
  Leave your own checkpoint commits as your history rather than squashing or
  rewriting them.
===no-base-final-clean-tree-rule===
- **The session is not done while `git status` shows anything uncommitted or
  untracked.** Check it last and commit whatever it still shows before your
  final message.
===recompose-heading===
- **Once all the work is done, the full verification suite is green, and the
  self-review phase above has run its course, recompose only your own
  checkpoints into real history — never anything that predates this delegation.**
===no-gates===
  This project configures no verification gates, so the suite is
  vacuously green — recompose once the work itself is done.
===gates-heading===
  The gates that must pass first:
===step0===
  0. With every last increment committed as a checkpoint — `git status` must show
     nothing uncommitted or untracked before this step. Record the pre-reset tip:
     `git rev-parse HEAD` — step 3 checks against it, so this is not optional
     bookkeeping.
===step1===
  1. Reset to the exact commit this branch held when you were dispatched —
     `{{DelegationBaseCommit}}` — never the branch's fork point against
     `origin/{{BaseBranch}}`. Everything at or before that commit is the
     operator's own history, made on their own live interactive claim before this
     delegation — not yours to rewrite, whatever it contains:
     `git reset --mixed {{DelegationBaseCommit}}`
     A mixed reset changes which commits exist and leaves the working tree exactly
     as it is, so the tree itself does not move.
===step2===
  2. Immediately invoke the commit-plan skill, if this repo ships one, to compose
     that tree into cohesive, buildable commits covering only your own new work —
     or compose them yourself the same way if it does not.
===step3===
  3. REQUIRED before you finish: verify tree identity — `git diff <old-tip> HEAD`
     (the tip recorded in step 0) must print nothing. A mixed reset changes only
     which commits exist, never the tree, so an empty diff should be automatic —
     but a file the commit-plan step forgot to stage lands as untracked rather
     than modified, which this diff catches and a plain `git status` glance can
     miss. Check `git status --porcelain` too, right here, and treat any untracked
     file it shows as the same failure.
===between-steps===
  Nothing happens between steps 1 and 2: no test run, no fix, no exploration.
  That gap is exactly what the reset is for: because the tree never moves, the
  commits composed in step 2 describe the identical tree that passed the suite
  before step 1, and anything done in between would break that guarantee. If
  something genuinely must change after the reset, commit everything as it stands
  first, then make the change and recompose again.
===final-clean-tree-rule===
- **The session is not done while `git status` shows anything uncommitted or
  untracked.** Check it last, after the recompose above, and commit whatever it
  still shows before your final message. A clean tree is the contract, not a
  nice-to-have.
