---
project: hall9k
type: feature
objective: Make PullRequestOpened the start of a closeout phase the platform drives to true completion
criteria:
- A daemon background service polls each awaiting-review task's PR on an interval (config in DaemonOptions)
- PR merged -> RunCompleted appended, the task reaches a genuinely terminal closeout, and h9k status stops showing AwaitingReview for it
- PullRequestOpener no longer removes the worktree at PR-open: the worktree is retained until the PR completes (merged/closed) or another node takes a lease on the task; removal happens here, at closeout completion (origin incident: worktrees deleted at PR-open had to be recreated by hand to resolve Copilot reviews on PRs 4 and 5 — the worktree IS the follow-up workspace)
- The recreate-from-branch path (follow-up worktree checkout from task 03) remains for the other-node and purged-artifact cases
- Closeout completion also deletes the task branch everywhere it lingers: the remote branch (if the merge did not already delete it), the local branch in the shared repo, and stale remote-tracking refs (git fetch --prune). Note: PRs land via rebase merge, so the local branch tip is never an ancestor of main — use git branch -D, justified by the merged-PR signal the monitor already has (origin incident: five merged task branches accumulated locally because nothing owned this step)
- Failing PR checks -> a follow-up run dispatches automatically on the PR branch with a fix-the-CI prompt
- New unresolved Copilot review threads -> a follow-up run dispatches automatically using the resolve-copilot-reviews skill
- Repeated closeout failures do not loop forever - a bounded retry count parks the task NeedsHuman (or Failed) with the reason
- The closeout state is visible in h9k status (e.g. ClosingOut / ChecksFailing / ReviewPending refinements or equivalent)
- dotnet build and dotnet test pass
---
Today a PR opening ends Hall9k's involvement, but it is actually the BEGINNING of closeout:
Copilot reviews land, CI runs, and eventually a human merges. The human should not be the
poller — the platform watches and dispatches.

Design constraints:
- Builds directly on the follow-up-run mechanics from 03 (hard dependency; do not
  reimplement dispatch-on-existing-branch here).
- Polling via gh (pr view --json state,mergedAt,mergeStateStatus + checks + review
  threads GraphQL). Poll gently — minutes, not seconds; this is a doorbell-less domain.
- Merge detection finally gives RunCompleted its meaning (TASK-MODEL.md reserved it for
  exactly this). Update TASK-MODEL.md with whatever events the design adds
  (e.g. PullRequestMerged, PullRequestChecksFailed, ReviewFeedbackReceived).
- The bounded-retry rule matters: an agent that cannot fix CI twice must stop burning
  tokens and surface to the human (PLAN.md log #11 spirit: never kill/loop on judgment).
- Multi-node future: the poller runs on the node holding... nothing (the task is Done and
  lease-free). Decide and document who polls — simplest honest answer: every node polls
  tasks whose runs it executed (RunDetails.NodeId).
