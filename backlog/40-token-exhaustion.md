---
project: hall9k
type: feature
objective: The platform recognizes token-budget exhaustion as its own failure shape - runs hold and resume when the window resets instead of failing as generic errors
criteria:
- The run supervisor recognizes the rate-limit/usage-exhausted error shape in a session's result and records it as what it is (a distinct outcome, e.g. BudgetExhausted with the observed message) - never the generic "reported an error result"
- Classification is conservative per the never-guess rule: BudgetExhausted is recorded only when the session's result carries the recognizable usage-limit message shape; an ambiguous or generic API error stays the generic failure it is (origin: the 22:31 diagnosis needed the full pattern - simultaneous cross-run deaths, no wall-clock jump, daemon unaffected - and a single vague error proves nothing; trading "wrong cause: suspend" for "wrong cause: budget" would be the same defect with a new label)
- An exhausted run parks rather than fails: the work is intact, the cause is external and recoverable by clock, so the task holds with a reason naming the window ("token budget exhausted - resumes when the subscription window resets") instead of demanding a human retry
- The daemon retries held-for-budget runs automatically on a patient cadence (the window resets on a known-ish clock; probing hourly is enough), and a successful resume clears the hold without any human act
- When several sessions die of exhaustion in one instant, the board reads as one condition, not N unrelated failures - the reason line is shared and says how many runs are waiting
- h9k status distinguishes waiting-for-budget from every other hold per the backlog-28 reason discipline; it is a waiting-but-handled state a human can consciously ignore
- dotnet build and dotnet test pass
---
Origin incident (2026-08-21 22:31): the subscription usage window ran dry mid-flight
and every live session across three independent runs errored in the same instant -
a fix session, and both review passes of another run. The platform failed all three
honestly but generically, the board showed three unrelated Failed rows, and the
human's morning retries recorded a wrong cause (suspected machine suspend) because
the generic error shape carried no evidence. The signature was diagnosable - no
wall-clock jump, simultaneous cross-run errors, daemon unaffected - and the fix is
to teach the supervisor that signature so the record names the real cause and the
recovery needs no human at all.

DRAFT ONLY - Brian refines before publishing. Related: 36 (token visibility),
28 (reason discipline), 39 (honest failure attribution generally).
