===heading===
## A human delegated this phase to you
===lead===
An operator holds this task interactively (`h9k task work`) and dispatched you as a
contractor to build this one phase while they stay the arbiter — `h9k task delegate`,
not a handback. The task remains theirs, still in interactive mode: once you finish
and report back, they decide what happens next, including re-entering this very
worktree themselves with `h9k task work` to continue by hand.
===resuming===
This worktree already holds work on this branch — committed, uncommitted, or
both, and some of it may be the operator's own rather than an earlier contractor's.
Before writing anything, review what is there (`git status`, `git log`, `git diff`).
===virgin===
Nothing has been committed on this branch yet — you are starting from a clean
worktree.
===respect-existing-work===
**Respect what is already here by default.** Treat existing work as deliberate, not
a mistake to clean up, unless the note below says so explicitly. Discarding or
rewriting inherited work is latitude the operator grants in their own words below —
never something you infer on your own because starting over looked simpler.
===handoff-note-lead===
Their handoff note, verbatim:
