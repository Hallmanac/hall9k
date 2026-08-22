---
project: hall9k
type: feature
objective: The closeout budget measures lack of progress on the same obstruction, never the count of events, so a live pull request in a busy repo is not parked for being alive while runaway loops still hit a hard wall
criteria:
- Automatic closeout laps count against the cap only when consecutive laps fail to clear the same obstruction (the same failing check name, the same unresolved findings); a lap that clears its obstruction resets the counter to zero
- A human-initiated event observed on the pull request - a review re-request, a newly opened human thread, a human comment - always grants its own lap regardless of the counter, because human engagement is the proof the loop is not running away (origin incident, 2026-08-22: Brian re-requested a Copilot review on PR 26 while the task's flat budget of 2 was already spent on unrelated obstructions - review threads, then a CI flake - and the deliberate request would have been charged against a guard built for unattended loops)
- An absolute lifetime ceiling of automatic laps per pull request remains as the true runaway backstop (default 6), separate from the progress counter; hitting it parks with the full lap history named, and only a human lever (pr resolve) grants more
- What counts as "the same obstruction" is recorded mechanically, never judged: check obstructions key on the check name, review obstructions on the set of unresolved thread ids present at dispatch - two CI failures of different checks are different obstructions
- The park message names the obstruction that repeated and the laps it survived, so the human knows what the machine already tried before spending their own attention
- h9k pr resolve keeps its budget-reset behaviour and the reset is recorded as a human grant on the run stream
- dotnet build and dotnet test pass
---
Decided in conversation (Brian + orchestrator, 2026-08-22). The flat
MaxAutomaticCloseoutRuns = 2 conflates distinct causes under one counter: task 18's
budget went one lap to review threads and one to an ubuntu CI flake, and the fresh
human-requested Copilot review then had no budget left despite nothing ever looping.
The cap's real job is stopping repetition without progress, so that is what it
should count.

The concession that survived the AGAINST: a progress-based budget can legitimately
grind through many expensive laps on a busy pull request, so the hard outer bound
stays - the platform keeps an absolute ceiling per PR the way token budgets taught
it to keep hard outer bounds everywhere.

Relationship: backlog 44 (branch freshness) adds conflict obstructions to closeout;
they key on "conflicting with base" and follow the same counting rule. Neither task
blocks the other, but whichever lands second wires conflicts into this budget.
