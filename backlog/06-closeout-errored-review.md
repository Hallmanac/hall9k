---
project: hall9k
type: bugfix
objective: Make the closeout monitor distinguish an errored Copilot review from a clean one instead of treating both as review-complete
criteria:
- A Copilot review whose body signals failure (e.g. "Copilot encountered an error and was unable to review") is treated as review-pending, not review-clean; the run does not advance past ReviewPending on its account
- The monitor re-requests the Copilot review via the API when it observes an errored review, bounded by the same automatic-retry budget as other closeout actions
- A repeatedly erroring reviewer parks the run (CloseoutParked) with a reason naming the errored review, so the human sees it in the NeedsHuman bucket rather than the PR silently skipping review
- An errored review followed by a successful re-review proceeds through the normal thread-resolution flow
- dotnet build and dotnet test pass
---
Origin incident (2026-08-17): during a GitHub partial outage, Copilot posted a review
whose only content was "Copilot encountered an error and was unable to review this
pull request." The closeout design counts unresolved review threads, and an errored
review produces zero threads - indistinguishable from a clean pass. PR #6 nearly ate
its own dogfood here: the monitor it implements would have treated its own unreviewed
state as review-clean.

Design constraints:
- Depends on 04 (the closeout monitor) being merged.
- Detection should be conservative: match Copilot-authored reviews whose body
  indicates failure, not arbitrary review text. Document the matching rule.
- Re-request via the API (the website may be down when this matters - that was the
  origin incident's exact circumstance).
