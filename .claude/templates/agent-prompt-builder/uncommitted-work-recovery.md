===intro===
A previous session working this task ended with finished work sitting uncommitted
in this worktree:
===background-wait-notice===
That prior session's own last message said it was still waiting on a background
build or test run to finish — it never will: the process that would have delivered
that result was killed the moment that session's turn ended, so there is nothing to
wait for here. Do not re-run or wait on anything; commit exactly what is already on
disk, below.
===objective-orientation===
The task's objective, for orientation: {{Objective}}
===only-job===
Your only job is turning every file listed above into well-formed commits, then
stopping. If this repo ships a commit-plan skill, invoke it now — that is exactly the
judgment call it exists for: organize what is genuinely finished work into cohesive,
buildable commits. Skip that skill's own build-verification step (the throwaway
`git worktree add` plus `dotnet build` per commit) — this session must not run the
build or test suite, below. If this repo ships no such skill, use the same judgment by
hand: `git add` and `git commit` what belongs, in as many commits as the change
actually needs.
===no-revert===
None of the files listed above may be reverted (`git checkout -- <file>`) or restored.
Every one of them is finished work someone is counting on, even a file that looks like
scratch or leftover debugging — commit it rather than guessing it does not belong. A
listed path that no longer exists in the worktree was deleted, on purpose, as part of
that finished work — commit the deletion itself (`git add -A` or `git rm` to stage its
removal), never bring the file back. The platform verifies afterward that each listed
file's outcome actually reached a commit — the deletion staged and committed for a path
that is gone, the content staged and committed for one that still exists — not merely
that it stopped appearing in `git status`, so discarding a real change (restoring a
deleted path, or reverting a modified one) does not pass this check either — it only
loses the work while the run still fails.
===leave-others-alone===
`git status` may still show other files once you are done — a build or test byproduct
an earlier session left behind, unrelated to the list above.
Leave anything not listed above alone: it is not this recovery's concern, and deleting
a file you do not recognize risks losing work of its own.
===no-findings-no-gates===
Do not read or act on any review findings. Do not fix bugs, add tests, or change any
file's content beyond what committing requires. Do not run the build or test suite.
Do not open a pull request — the platform does that once this run reaches its gates on
its own. Stop once every file listed above is committed.
===working-rules-heading===
## Working rules
===session-ends-note===
- **This session ends at your final message — nothing runs after it.** The
  dispatched runtime kills the process the moment you finish, so a backgrounded
  command, a scheduled wakeup, or a monitor set up to report back later never
  fires: there is nothing left to fire it, and nobody reads the result. This session
  is not asked to run this project's gates at all — see above — but the same rule
  covers anything else you run:
