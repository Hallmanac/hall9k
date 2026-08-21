---
project: hall9k
type: refactor
objective: A task set aside deliberately is Archived, not Abandoned - one vocabulary for walking away, shared with ideas
criteria:
- TaskState.Abandoned becomes Archived across the domain, projections, composition, and every display surface; h9k task archive is the command, and h9k task abandon is retired with a teaching pointer (or kept briefly as an alias that says the new name - decided at design, never two silent synonyms)
- Going forward the stream event is TaskArchived; historical TaskAbandoned events remain exactly as written and replay to the Archived state - history is never rewritten, the vocabulary just stops being used
- Projection documents carrying the old state string are rebuilt by the daemon at startup in the backfill pattern decision #37 established (keyed, idempotent, self-terminating), so pre-rename tasks read Archived everywhere without touching their streams
- Docs sweep: TASK-MODEL, PLAN prose, AGENTS.md, and help text stop saying abandoned; the decisions-log entry records the rationale
- The vocabulary is now symmetric and stated somewhere teaching: ideas end Concluded or Archived, tasks end Done or Archived - archived always means "dealt with by deciding not to pursue," never "forgotten"
- dotnet build and dotnet test pass
---
Brian's direction (2026-08-21): abandoned reads like the task was forgotten and
never dealt with - but the reality is the opposite, a human explicitly decided it
was not worth doing and closed it with a reason. Archived says exactly that, and
the idea lifecycle already chose it (backlog 31's two terminal states), so this
rename completes the symmetry rather than inventing a second word.

Design constraints:
- This is the platform's first persisted-vocabulary rename - set the precedent
  carefully: new event forward, legacy replays unchanged, backfill for documents,
  and the decisions log records the pattern for the next rename.
- The Failed-exit teaching (retry / resolve / archive) and every reason-carrying
  surface pick up the new word in the same pass.
