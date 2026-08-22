# Sitting agenda: the status vocabulary redesign

**Purpose:** one design conversation producing one coherent task. The statuses grew a
feature at a time and were never designed as a single surface; the owner who helped
build them finds them confusing (Brian, 2026-08-22), which is the whole finding.

## The confusions, each with the incident that proved it

1. **Done before it is done.** Task 17 displayed `Done` while its PR was open, follow-up
   generations were still running, and the dependency rule itself says a blocker counts
   only at true closeout. The display contradicts the platform's own strictest rule.
2. **ClosingOut means two opposite things.** Watching-the-PR-and-dispatching-follow-ups
   (busy) and watching-the-PR-with-nothing-left-but-the-human's-merge (waiting on you)
   render identically. Brian had to ask which one PR 24 was in (2026-08-22).
3. **The label lags the truth.** Task 35 displayed ClosingOut while a fix session was
   actively editing its worktree; the orchestrator nearly rewrote history under a live
   agent because the board said the lane was quiet (2026-08-22).
4. **Failed conflates four different events**: a real defect, a machinery death, a
   machine suspend, and token exhaustion - different causes, different levers, different
   urgency, one word. The 2026-08-21 22:31 token exhaustion showed as three unrelated
   Failed rows and got mis-attributed on retry (backlog 40's origin).
5. **Claimed hides everything.** Build, gates, two review lenses, fix sessions, verdict
   parsing - hours of distinct machinery under one word, with run-level states
   (UnderReview) leaking into the task pane inconsistently.
6. **Run vocabulary vs task vocabulary was never drawn as one picture.** RunSuperseded/
   FollowedUp (16's note), run Completed vs task Done, review verdicts (MergeReady
   clean-vs-settled, 35) - three families that grew separately.
7. **A red row without a reason** delegates the investigation to the human it was meant
   to spare (backlog 28's whole case, plus the crying-wolf era).

## Pieces already in flight that this must reconcile, not duplicate

- Backlog 28 (reason + lever on every NeedsHuman row) - the display half of this design.
- Backlog 16's post-queue notes (ClosingOut renamed FollowingUp; RunSuperseded renamed FollowedUp).
- Backlog 33 (Abandoned renamed Archived), 36 (token lines), 40 (BudgetExhausted outcome).
- Decision #34's two arcs: human acts (Draft, Published, assignment) vs machine
  observations (Queued/Blocked, Claimed, terminal states) - the split that names should
  keep legible.

## The shape to test in the sitting (a proposal, not a decision)

One field is carrying four questions. Separate them:
- **State** (persisted, few, honest): where the task is in its lifecycle.
- **Phase** (live, derived from the run and observed session liveness - never guessed):
  what the machinery is doing right now (building, gates, reviewing cycle 2 of 2,
  fixing, watching PR, waiting for a slot, waiting for budget).
- **Attention** (composed): needs-you or not - and when yes, the one-line reason and
  the lever (28's contract), including waiting-but-handled as consciously ignorable.

## Questions the sitting must answer

1. Does the three-part split (state / phase / attention) hold, or is it over-built?
2. Which words survive? (Done vs a name that admits the closeout gap; the Failed family
   split; whether Claimed becomes the phase line's job.)
3. Is phase derived-only (display composition, cheap) or recorded (events, replayable)?
   The liveness half (is the session actually alive?) can only be observed, not replayed.
4. One rename migration or several? (16/33's patterns exist; do the renames ride those
   tasks or consolidate here?)
5. What does h9k status's layout become once rows carry state + phase + reason?
