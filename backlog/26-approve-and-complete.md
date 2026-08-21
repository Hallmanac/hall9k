---
project: hall9k
type: feature
objective: Make human approval a recorded platform act that drives the merge itself - h9k task approve, and the closeout monitor completes the pull request
criteria:
- h9k task approve <task> [--reason] records the human's approval as an event on the task's stream (a human act through the CLI, per the observation-gate doctrine); approving requires the task to be Done - with its pull request open, or with no pull request at all for transcript-deliverable work
- With approval recorded, the closeout monitor gains a completion path: approval present + no unresolved review threads + CI green on the current tip -> the DAEMON merges the pull request itself (rebase merge, delete branch), deterministic platform code with no agent involved
- A stale PR blocks nothing: when the approved PR has fallen behind main (dirty, or a semantic collision like duplicated decision-log numbering), the monitor dispatches a rebase-and-regate follow-up first - rebase onto main, resolve per the repo conventions, gates - and merges on the resulting green, still automatic
- The merge is recorded from observation as today (PullRequestMerged when the monitor sees it), and the normal closeout continues unchanged: RunCompleted, worktree removal, branch cleanup, dependent unblocking with handoffs
- A task with NO pull request - research and spike work whose deliverable is its transcript - closes out through the same lever: approving it records the human attestation AND completes the run directly (RunCompleted, dependents unblock, the handoff travels), since there is no merge for the monitor to observe and the human's judgment is the only honest completion signal. Origin gap (2026-08-20): the lifecycle assumed every task ends in a merge, so a Done research task could never reach true closeout and would park its dependents as blocked-by-a-dead-blocker forever
- h9k task approve before the PR settles is not an error: the approval waits, and the monitor completes whenever the conditions are all true - approval is a standing fact, not a timing puzzle
- Revoking is explicit: h9k task approve --revoke withdraws a standing approval with a reason, refused once the merge was observed
- h9k status and task show display a standing approval, so an approved-but-unmerged task is visibly in flight rather than mysteriously idle
- dotnet build and dotnet test pass
---
Brian's direction (2026-08-20): approval today is a sentence typed to an orchestrator
session, and everything after it is orchestrator labor - the merge, the sync, and the
rebase repairs when main moved underneath. The same evening produced the exact
motivating case: PR #20 reported CLEAN while quietly carrying a duplicate decision-log
number that a blind merge would have landed, caught and repaired by hand. This lever
makes approval an event, the merge machinery, and the stale-branch repair a dispatched
agent - removing the last routine human-labor loop between "I approve" and "it is on
main."

Design constraints:
- The happy path is deterministic platform code, not an agent: merging an approved,
  green, settled PR is mechanical. Agents enter only for the judgment-shaped work
  (rebase conflict resolution per the repo's authored-history conventions).
- The rebase-and-regate follow-up rides the existing reopen pipeline and prompts, with
  the renumber-on-collision convention (decision-log entries) stated explicitly in its
  prompt - that repair has now been performed by hand at least six times and its
  mechanics are well rehearsed in the log.
- Approval is scoped to the PR tip the human saw... deliberately NOT: a standing
  approval survives the rebase-and-regate follow-up (the repair changes base, not
  substance), but any follow-up dispatched for NEW review findings clears it - new
  substance was added after the human looked. State the rule in the event and the
  docs.
- Interplay with backlog 25: a teammate's unresolved thread blocks the completion path
  exactly like Copilot's, so approve-then-thread-arrives waits, dispatches, and
  completes after resolution - approval does not shortcut review.
