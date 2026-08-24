---
project: hall9k
type: bugfix
objective: RunAggregate.Apply(ReviewParkResolved) keys its pre-gate/post-gate branch on the same signal ReviewResolveCommand already had to move off of, so a resumed pre-gate dispute that disputes again is settled straight to the pull request instead of re-entering at the gates
criteria:
- RunAggregate.Apply(ReviewParkResolved) (src/Hall9k.Domain/Features/Run/RunAggregate.cs:391) stops branching on ParkedFromState == RunState.Verifying and branches on the same signal ReviewResolveCommand's own merge-ready refusal already uses (ReviewCycle == 0, backlog 44's rebase-dispute work) - ParkedFromState goes stale the moment a resumed dispute's fix session moves State to UnderReview before parking again, while ReviewCycle is untouched by the whole dispute-and-resolve round trip
- ReviewResolveCommand's own outcome message (src/Hall9k.Cli/Commands/ReviewResolveCommand.cs:159) moves off ParkedFromState onto the same corrected signal, so the printed outcome always matches what Apply(ReviewParkResolved) actually does
- RunAggregateTests.cs:484 and the ReviewEngineTests.cs cluster documenting ParkedFromState's role (roughly lines 1107-1736) are re-read against the corrected discriminator and updated rather than left describing the old, incorrect branch
- The failure this closes: a review-thread pre-gate dispute parks, a human resolves --needs-fixes, the fix session disputes again and parks a second time (now from UnderReview instead of Verifying), the human resolves --merge-ready, and the run goes straight to Settling/MergeReady - PullRequestOpener pushes a run whose verification gates never ran once
- dotnet build and dotnet test pass
---
Origin (2026-08-24, independent pre-PR review of task 44/backlog 44's own
branch-freshness work, conformance lens cycle 1): RunAggregate.cs:391-399
already carries a long comment admitting the mismatch - ParkedFromState is
what the branch checks, but a resumed pre-gate dispute's fix session moves
State to UnderReview before it parks a second time, so ParkedFromState reads
UnderReview instead of Verifying on exactly the case that matters, and the
else-if branch below takes the human straight to a push with no gates run.
The comment called this "a pre-existing defect tracked separately, not this
command's to fix" - it was not tracked anywhere, which is what filed this
card. ReviewResolveCommand.cs already independently arrived at the correct
discriminator (ReviewCycle == 0) for its own merge-ready refusal on a
disputed rebase; backlog 64 is applying that same, already-proven signal to
the aggregate's branch and the CLI's outcome message so both agree.

This was deliberately NOT fixed inline during the branch-freshness follow-up
that found it: RunAggregate.Apply(ReviewParkResolved) and its ParkedFromState
property are read from a cluster of integration and unit tests reasoning
explicitly about the Verifying/UnderReview distinction (RunAggregateTests.cs,
ReviewEngineTests.cs), and changing the discriminator needs those re-read and
possibly rewritten alongside the aggregate change, not bolted onto an
unrelated PR's fix-up pass.
