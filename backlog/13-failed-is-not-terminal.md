---
project: hall9k
type: feature
objective: Make Failed a needs-human waypoint instead of a terminal state, with an explicit resolve-to-done exit
criteria:
- TaskState.Failed is no longer terminal; the only terminal states are Done and Abandoned (TaskState.IsTerminal, decider guards, and TASK-MODEL.md all updated)
- h9k task resolve <id> --reason <why> moves a Failed task to Done via a decider-guarded event (e.g. TaskResolved), recording the human's attestation that the objective was met despite the run failure; an optional --pr <url> records where the work landed
- Resolve is Failed-only and human-only; the reason is required (an attestation without a why is a guess, AGENTS.md never-guess rule)
- The three exits from Failed are retry (backlog 09, re-run), resolve (objective already met), and abandon (walk away); h9k task show on a Failed task teaches all three
- The stream stays honest: added -> claimed -> failed -> resolved -> done reads in order; resolve never rewrites or hides the failure
- Projections and h9k status reflect the transitions; a resolved task shows Done with its PR
- dotnet build and dotnet test pass
---
Brian's principle (2026-08-18): a failed state means there is an unsolved problem,
and an unsolved problem is not an ending. Terminal states should say how the story
ended; "ended in failure" is only true when a human walks away, which is what
Abandoned already means. Origin incident: tasks a282deb0 and 13241bd8 sit Failed
although their work merged as PRs #7 and #8 - the failure was in the machinery
(the push step), the objective was met, and the final state is simply wrong.

Design constraints:
- Builds on backlog 09 (TaskRetried and its guards land there; this task widens the
  model around it). If 09's implementation assumed Failed is terminal anywhere,
  correct it here.
- TaskDecider.Fail's "already terminal" guard must still prevent failing a Done or
  Abandoned task, and double-Fail on an already-Failed task stays rejected.
- After this lands, resolve the two origin-incident tasks by hand as the first real
  use, with reasons pointing at their merged PRs.
