===head===
- **This session ends at your final message — nothing runs after it.** The
  dispatched runtime kills the process the moment you finish, so a backgrounded
  command, a scheduled wakeup, or a monitor set up to report back later never
  fires: there is nothing left to fire it, and nobody reads the result.
===tail===
  Commit everything before that final message, new files included: a tracked
  file left modified or staged but uncommitted when the session ends is stranded there,
  and the platform fails the run naming exactly which files were left behind — a new,
  never-`git add`ed file under src/ or tests/ counts too, named in the same failure,
  so committing only the modified files it also names still leaves a hollow branch
  behind. An untracked file outside src/ and tests/ only warns — a gate's own build
  output can land there too — but it still never ships, so `git add` it and commit
  rather than counting on the warning to catch it.
