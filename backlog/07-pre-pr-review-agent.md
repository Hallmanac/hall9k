---
project: hall9k
type: feature
objective: Dispatch an independent review agent over every run's diff before its pull request opens, looping fix-and-re-review until clean or parked
criteria:
- After verification gates pass and before PullRequestOpener runs, the daemon dispatches a review agent - a separate headless Claude session with fresh context (never the session that wrote the code) - over the run's diff against main
- The review agent's prompt requires verified findings only (read the surrounding code, confirm the defect, discard what cannot be confirmed), each with file:line, a defect statement, and a concrete failure scenario, plus an explicit merge-ready / needs-fixes verdict
- A needs-fixes verdict dispatches a fix run in the same worktree with the findings as its prompt; after the fix run passes verification gates, a fresh review agent reviews again (the loop is: review -> fix -> gates -> review)
- The loop is bounded by the same automatic-retry-budget pattern as closeout (config in DaemonOptions); exhausting it parks the run for a human with the unresolved findings attached
- A finding the fix run judges to be not-a-defect or human-territory (design disagreement, scope change) parks the run NeedsHuman with both positions recorded rather than looping
- A merge-ready verdict lets PullRequestOpener proceed; the review outcome is recorded on the run stream as a milestone event and the findings/verdict land in the run's artifact directory on disk
- Review state is visible in h9k status (e.g. UnderReview between Verifying and AwaitingReview)
- Tokens spent by review and fix sessions are recorded on the run like any other session
- dotnet build and dotnet test pass
---
Today the pipeline is: agent writes code -> build/test gates -> push -> PR -> Copilot.
Nothing between the gates and the PR reads the code. Copilot is the only reviewer,
it is shallow, and (origin incident, 2026-08-17, GitHub outage) it can error out
entirely - see backlog 06. The platform should bring its own reviewer: an
independent agent with fresh context reviewing the diff BEFORE the PR opens, with a
bounded fix-and-re-review loop, so PRs arrive pre-reviewed and Copilot becomes a
second opinion instead of the only one.

Design constraints:
- Fresh context is the point: the reviewer must be a new session that has not seen
  the implementation reasoning, or it will rubber-stamp. Same reason human teams
  do not self-review.
- Reuses existing machinery deliberately: the fix run is the follow-up-run pattern
  (worktree reuse, verification gates re-run), the bound is the retry-budget
  pattern, parking is the CloseoutParked/NeedsHuman-bucket pattern from 04.
  Do not invent parallel mechanisms.
- Review happens pre-PR, so review traffic never hits GitHub - it works during
  GitHub outages and on non-GitHub origins (which 04's closeout cannot watch).
- Streams carry milestones only: the verdict event belongs on the run stream;
  the full findings text is an artifact on disk, not event payload.
- Depends on 04 (parking pattern, retry-budget pattern) being merged.
