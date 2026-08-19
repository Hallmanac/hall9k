---
project: hall9k
type: feature
objective: Give Failed tasks an explicit human-driven retry path so infrastructure failures do not strand completed work in a terminal state
criteria:
- h9k task retry <id> moves a Failed task back to Queued through a decider-guarded event (e.g. TaskRetried), preserving lease-generation fencing (the next claim increments as usual)
- Retry is Failed-only and human-only: Abandoned stays a dead end, and the closeout monitor never retries automatically (a failure that repeats without human eyes is the never-loop-on-judgment rule, PLAN.md log #11)
- A retried task whose branch and worktree survive reuses them through the existing follow-up checkout path; a retried task whose artifacts are gone starts clean from origin/main
- The retry reason is recorded on the event and visible in h9k task show
- Projections (TaskListItem, TaskDetails) and h9k status reflect the transition; the failure history remains visible in the stream (retry does not erase why it failed)
- dotnet build and dotnet test pass
---
Origin incident (2026-08-17): the first two automatic follow-up runs completed their
work and passed verification gates, then failed at the push step (the daemon's plain
push rejected their rebased branches - see backlog 08). Both tasks fell to Failed,
which is terminal with no exit: TaskDecider.Reopen requires Done, and nothing else
leaves Failed. The completed, gated work sat stranded in the worktrees and had to be
force-pushed and shepherded by hand. Failure of the machinery around the work should
not permanently condemn the task that contains the work.

Design constraints:
- Failed stays honest: retry appends a new event; it never rewrites or hides the
  failure. The stream reads added -> ... -> failed -> retried -> claimed.
- Distinct from TaskReopened (which is Done-only and drives PR follow-ups). Do not
  overload one event with both meanings; the decider guards differ.
- Consider whether TaskRetried should count against any budget: recommendation is no
  (it is human-initiated, like h9k pr resolve resetting the closeout budget).
