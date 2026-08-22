---
project: hall9k
type: feature
objective: The board answers four questions with three surfaces - lifecycle state, live phase, and attention - so a glance tells the truth about what is happening and what needs a human
criteria:
- The displayed lifecycle vocabulary becomes Draft, Published, Working, Delivered, Done, Failed, Archived. Delivered is the state between the work being pushed and the merge being observed - what today's premature Done falsely claims; Done renders ONLY at true closeout, agreeing at last with the dependency rule. Display-first (Brian, 2026-08-22): persisted states do not change in this task - Queued and Blocked keep their streams and render under Published as derived facts ("published - ready to dispatch", "published - waiting on 2 dependencies"), and the display layer is written so the ranking model (IDEA-ranking-and-grooming) can retire them later without another redesign
- Every Working or Delivered row carries a PHASE line derived from run records PLUS observed session liveness - "building", "gates", "review cycle 2 of 3 (adversarial pending)", "fix session running", "watching PR - waiting on your merge", "waiting for a slot", "waiting for budget" - and a phase never claims a session is doing something without observing the process alive (origin incident, 2026-08-22: the board said ClosingOut while a fix agent was actively editing the worktree, and the orchestrator nearly rewrote history under it)
- The two meanings of today's ClosingOut are split by the phase line: watching-and-dispatching-follow-ups reads differently from watching-with-nothing-left-but-the-human's-merge, so "is it my turn?" never needs a log dive (origin incident, 2026-08-22: PR 24)
- The ATTENTION surface absorbs backlog 28's contract as a first-class column: needs-you yes or no, the one-line cause and lever from recorded facts, and waiting-but-handled (self-clearing holds, budget waits) visually distinct as consciously ignorable
- Failed rows compose their cause from what is recorded - gate failure, machinery death, review-loop error - never a bare word; where the cause is not yet recorded distinctly (token exhaustion until backlog 40 lands) the display says what it observed, never guesses
- The run-level vocabulary renders as the phase line's material and never leaks into the state column; h9k task show leads with state + phase + attention before the context mountain
- Ranking readiness: the Published row's derived-facts line is built to also carry ranking facts (unranked / ranked / expedited, available / held) when the ranking model lands - the column exists, the facts arrive later, and no unranked-count reporting is built here
- dotnet build and dotnet test pass
---
The status-vocabulary sitting's outcome (Brian + orchestrator, 2026-08-22; agenda and
the seven catalogued confusions in backlog/SITTING-status-vocabulary.md). Root cause:
one field answering four questions - lifecycle, live activity, attention, cause. The
fix is three surfaces; the persisted model changes NOT AT ALL in this pass.

Settled in the sitting:
- Delivered is the word for pushed-but-not-closed-out (Brian approved the name).
- Display-first: no state-machine surgery now; Queued/Blocked retire when the ranking
  model lands (sequencing: this task, then project membership, then ranking/grooming).
- Phase is derived-only in this pass: composed from run records and live process
  observation, not new events. If a phase cannot be observed it says so rather than
  guessing (never-guess applies to liveness too).

Relationships (decided at review, Brian, 2026-08-22):
- Backlog 28 (needs-human reasons) is ABSORBED here: its criteria - the one-line
  cause and lever from recorded facts, the required reason at every decider, the
  waiting-but-handled distinction, one composer owning the mapping - are part of
  this task's attention surface. 28's file stays as the origin record, marked
  absorbed; it is never queued separately.
- Backlog 16's post-queue note is amended: the ClosingOut-to-FollowingUp rename is
  superseded (the phase line dissolves that bucket entirely); the RunSuperseded-to-
  FollowedUp persisted rename half stays parked in 16's note, unchanged.
- Backlog 33 (Archived rename) stays its own persisted-vocabulary task; this task
  displays whichever word the stream currently records.
- Backlog 36 (token lines) composes into task show beside the phase line; keep the
  seams compatible, build neither here.
