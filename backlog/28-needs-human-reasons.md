---
project: hall9k
type: feature
objective: Every NeedsHuman row says why and what to do, in one line, at the surface where the human is looking
criteria:
- h9k status shows a short reason beside every NeedsHuman row - one line, the cause and the lever, never a paragraph (e.g. "review parked: budget spent - h9k review resolve", "dependency f47658a9 failed - retried, clears at its closeout", "asked a question - h9k task answer")
- The reason is derived from the same recorded facts the composition already reads (park reasons, dependency-failure records, pending questions), never a re-guess; one composer owns the mapping from state to reason-line so status and task show cannot disagree
- h9k task show leads with the why: a NeedsHuman (or held) task prints an ATTENTION section at the top - reason and lever before the objective, criteria, and context mountain - so the answer to "why am I looking at this" is the first thing read
- Every reason names its lever explicitly by command, per the CLI teaching standard; a reason without a next action is not done
- The waiting-but-handled case reads differently from the act-now case: a hold that will self-clear (a retried blocker en route to closeout) says so, so the human can consciously ignore it (origin incident, 2026-08-21: two dependency holds read as red NeedsHuman for hours after their blocker was already retried and rebuilding, and the human had to ask an orchestrator session what, if anything, was actually needed)
- dotnet build and dotnet test pass
---
Brian's feedback (2026-08-21): status shows that something needs a human but never
why; task show has the why somewhere in a mountain of context text. The attention
surface's whole job is answering "what do you need from me" - a red row without a
reason delegates the investigation to the human it was supposed to spare.

Design constraints:
- Reuses backlog 27's recovered-blocker work naturally: once holds clear on
  recovery, the waiting-but-handled wording becomes rare instead of routine -
  build 27 first or together.
- The reason line is display composition, not new events: everything needed is
  already recorded (CloseoutParked.Reason, ReviewParked, dependency failure
  records, QuestionAsked). This task is about surfacing, not capturing.
- Keep the one-line discipline ruthlessly: the full text stays available in task
  show's attention section; status gets the compressed cause + lever only.
