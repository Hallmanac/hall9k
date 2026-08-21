---
project: hall9k
type: bugfix
objective: A recovered blocker refreshes its dependents - retrying or resolving a failed dependency clears the dead-blocker hold instead of leaving the board crying wolf
criteria:
- When a Failed blocker is retried (TaskRetried) or resolved (TaskResolved), its dependents' recorded dependency-failure is cleared by a recovery event on each dependent (e.g. TaskDependencyRecovered naming the blocker), returning them to plain Blocked with the ordinary waiting-on display
- The same clearing applies when the dependency set is revised to drop the dead blocker (the already-taught remedy keeps working)
- The resolver never re-records a failure for a blocker that is currently Queued, Claimed, or otherwise capable of reaching closeout again; dead-blocker holds are for blockers that are dead NOW
- h9k status stops showing NeedsHuman for a dependent whose blockers are all either healthy or complete; the recovery is visible within one dispatch-loop cycle of the blocker's retry
- History stays honest: the original DependencyFailed record and the recovery both remain on the stream - the hold happened, and so did the recovery
- dotnet build and dotnet test pass
---
Origin incident (2026-08-21): the platform's first overnight crash-recovery ran
through the first real dependency graph. The machine went down with the chain's
head running; on daemon start the orphan was failed honestly and both dependents
were held-for-human with the dead-blocker reason - all correct. Then h9k task
retry put the blocker back to work, and the holds did not clear: both dependents
kept reading NeedsHuman for what would have been the whole rebuild, hours of a
board saying "act now" about a situation already handled. The unblock-at-closeout
path ignores the stale record, so this is display honesty rather than a
correctness bug - but a status surface that overstates urgency trains its reader
to ignore it.

Design constraints:
- The recovery event is appended by the same resolver pass that records failures
  (one owner for dependency state transitions), driven by observing the blocker's
  actual state - never by assuming the retry will succeed.
- A blocker that is retried and fails AGAIN re-records the failure; the pattern
  is hold, recover, hold, each observed, not a one-shot flag.
