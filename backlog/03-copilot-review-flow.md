---
project: hall9k
type: feature
objective: Add follow-up-run mechanics so an agent can resume work on a completed task's existing PR branch
criteria:
- A CLI entry point exists (proposed - h9k pr resolve <task>) that dispatches a follow-up run for a task whose PR has unresolved review comments
- The domain model supports a follow-up run on a completed task without violating the state machine (design and document the event choices, e.g. a TaskReopened event or a follow-up claim - explain the decision in the PR)
- The follow-up run gets a worktree checked out on the EXISTING PR branch (not a fresh branch off main)
- The agent prompt instructs use of the resolve-copilot-reviews skill and includes the PR URL
- After the agent finishes, verification gates re-run and the branch is pushed (the PR updates in place; no new PR)
- dotnet build and dotnet test pass
---
This is the FOUNDATION of the PR closeout phase (see 04-pr-closeout-monitor.md): the
mechanics of dispatching an agent onto a completed task's existing branch. This task
delivers the on-demand trigger (h9k pr resolve); task 04 builds the automatic monitor
on top of exactly this machinery. Automatic polling is out of scope here.

Design constraints:
- Depends on the repo skills task (resolve-copilot-reviews must exist in the repo).
- The task's terminal state question is a real domain decision: TaskDecider currently
  rejects work on Done tasks. Extend the model deliberately (event + decider + projection),
  document the reasoning per TASK-MODEL.md conventions, and update TASK-MODEL.md.
- Worktree manager currently only creates branches off the base; it needs a
  checkout-existing-branch path for follow-up runs.
- The pipeline reuse matters: follow-up runs should flow through the same supervisor,
  verification, and push machinery, not a parallel code path.
