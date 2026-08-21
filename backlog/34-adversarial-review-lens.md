---
project: hall9k
type: feature
objective: Add a second, adversarial lens to the pre-PR review - a defect hunter alongside the existing conformance reviewer, so internal review stops depending on Copilot to catch the scanner classes
criteria:
- The ReviewEngine runs two independent review passes per cycle, each with fresh context - the existing conformance pass (does the work meet the objective, the acceptance criteria, and repo doctrine) and a new adversarial pass prompted to hunt defect classes regardless of the criteria - injection and trust boundaries, missing sanitization, concurrency and races, API misuse, resource and process lifetime handling
- The adversarial pass is a genuinely different attention budget, not a second roll of the conformance prompt - its instructions never mention the acceptance criteria, and it is told to assume the code is wrong somewhere and find where
- The two passes run independently and their findings merge before the verdict - MergeReady requires BOTH lenses clean; either lens finding real problems produces one NeedsFixes verdict with the merged finding list, and one fix session addresses all of it
- Each recorded review event says which lens produced each finding, so the review history teaches which lens earns its keep over time
- The VERDICT-line contract, the re-prompt-then-park rule, and the cycle budget apply per cycle, not per lens - two lenses do not double the parking math
- Review dispatch events record the model for both passes per decision #33
- dotnet build and dotnet test pass
---
Origin incident (2026-08-21, PR #21): four Copilot passes on the same branch each
surfaced different findings. Empirically checked - the final pass flagged code in
TaskAddCommand.cs that was byte-for-byte identical in the previous reviewed commit,
and an injection risk in WorkItemContext.cs that existed in all four reviewed
commits. Copilot was not finding new problems introduced by fixes; it was sampling
the same code repeatedly, and the accumulated samples caught real defects (a
prompt-injection boundary among them) that the internal conformance review missed
across multiple cycles. Brian: the conformance reviewer checks alignment with the
stated outcomes and acceptance criteria; the adversarial reviewer is the missing
piece - 100% wanted.

Design constraints:
- One review pass is one sample. The lesson from the incident is not that Copilot
  is better but that a single pass has one attention budget; the two lenses turn
  the sampling property into the mechanism instead of a weakness. The lens list is
  a seam - more lenses (or repeated adversarial samples) can arrive later without
  restructuring, but two is the shipped shape.
- Relationship to backlog 25 (all reviewers): 25 broadens whose findings get
  HANDLED on the PR after it opens; this task broadens what internal review
  GENERATES before the PR opens. They compose and neither replaces the other -
  Copilot stays valuable precisely because its sampling is outside our control.
- Cost is acknowledged, not hidden: two fresh-context passes roughly double review
  tokens per cycle. That is the price of not outsourcing defect hunting to a
  third-party reviewer's roll of the dice.
- The adversarial prompt should name the defect classes from the incident record
  (sanitization, trust boundaries, races, API misuse) as examples, not as a closed
  checklist - a checklist becomes the new blind spot.
