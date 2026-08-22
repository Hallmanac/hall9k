---
project: hall9k
type: feature
objective: A pull request that has gone stale against main gets brought current by a dispatched agent, so an approvable PR is never silently blocked on a rebase nobody was told about
criteria:
- The closeout monitor observes the pull request's mergeable state on the inspections it already makes; when GitHub reports the branch as conflicting with its base, that observation is recorded on the run stream as its own event (never inferred, never re-guessed from staleness)
- An observed conflict dispatches a follow-up run onto the task's existing PR branch, the same dispatch shape as review-feedback follow-ups, spending the same closeout budget (MaxAutomaticCloseoutRuns) so a branch that keeps re-conflicting parks for a human instead of looping
- The rebase itself is agent work guided by a repo skill (rebase-onto-main or an extension of absorb-review-fixes): rebase onto origin/main, resolve conflicts with judgment, preserve authored history, and never leave a conflict marker in a commit; the skill records its origin incidents
- After the rebase the run re-runs the verification gates before finishing, because a clean-looking resolution that breaks the build is the failure mode (origin incident, 2026-08-22: this task's own first retry died on 7 test failures that were main-reconciliation fallout, not flakiness)
- The daemon pushes the rebased branch with force-with-lease exactly as it pushes other follow-up rewrites (Decisions Log #26); the agent never pushes
- A human lever exists to ask for it directly - h9k pr rebase <task> or an option on pr resolve - for the case where the human sees the conflict before the monitor's next inspection
- A conflict the agent cannot resolve honestly (both sides changed the same behaviour, not just the same lines) is a park with the conflicting files named, never a guessed merge
- dotnet build and dotnet test pass
---
Origin (2026-08-22): PR #26 (the Jira task) sat AwaitingReview with every review
thread resolved and nothing left but Brian's approval - and was silently unmergeable,
because PRs #23/#24/#25 had merged into main after its branch was cut. Nothing
observed the CONFLICTING state, nothing surfaced it, and the orchestrator ended up
doing the rebase by hand: five keep-both conflict stops across the project-settings
seam and the closeout tests. That is exactly the toil the platform exists to own,
and it will recur constantly once projects have more than one lane merging - every
merge makes every other open PR staler.

Where the logic lives, decided at filing: split like resolve-review-threads.

- The PLATFORM detects and dispatches. The closeout monitor already calls GitHub
  for threads and checks; mergeable state rides the same response. Detection is an
  observation event, dispatch is mechanical, budget is the existing closeout cap.
- A SKILL carries the how. Conflict resolution takes judgment (keep-both vs pick-a-
  side vs park) and repo doctrine (authored history, tree identity, no markers ever
  committed - the 2026-08-21 marker-commit incident applies). Skills are where that
  discipline lives and travels with the repo; the skill-layer platform-tier question
  (IDEA-skill-layer.md, Tension 8) applies to this one too, since the machinery
  depends on it.

Scope note: conflicting is the first slice. Merely-behind-but-mergeable is a softer
question (GitHub merges it fine; freshness only buys pre-merge CI truth) and can
ride the same machinery later behind a project setting if wanted. Do not build
auto-update-on-behind here.

Relationship: task 43 (repo materialisation) gives fresh machines the worktree base
this dispatch lands in; nothing here blocks on it for the single-node case.
