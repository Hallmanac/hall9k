---
project: hall9k
type: bugfix
objective: A NeedsFixes verdict with no findings is rejected by the review engine and re-prompted, never accepted - a verdict that names nothing actionable parks humans and stalls fix sessions on content that does not exist
criteria:
- The review engine validates a reviewer's output before recording it: a NeedsFixes verdict must carry at least one finding with a stated location (file, or file and line) and a defect description; a verdict that fails validation is re-prompted once with the requirement quoted, reusing the existing one-reprompt machinery from the verdict-parsing path
- A reviewer that fails the re-prompt has its pass recorded as errored (with the malformed output preserved verbatim for diagnosis), and the cycle proceeds per the existing errored-review handling rather than treating the empty verdict as findings
- MergeReady verdicts are untouched - finding nothing is a complete answer; only NeedsFixes claims the existence of something and must therefore name it
- The validation applies to both lenses identically
- dotnet build and dotnet test pass
---
Origin (2026-08-23, twice in one day, both Sonnet-model reviewers): task 40's
conformance lens issued NeedsFixes over "two minor log-message-only
inaccuracies" it never enumerated - the findings file held one summary
sentence and a verdict, the fix session had nothing to comply with, and the
lane burned to the compliance cap and parked. Hours later task 24's
adversarial lens issued a bare "VERDICT: needs-fixes" with the finding text
entirely absent; the fix session did exemplary diligence, refused to fabricate
a fix for an unnamed defect (the never-guess rule applied exactly right), and
disputed - parking a human to resolve a review that said nothing. Both parks
were resolved merge-ready on the strength of the OTHER lens plus the fix
session's verification, but the engine accepting content-free verdicts is the
defect: it converts a reviewer's malformed output into human interrupts.

Relationship: rides beside 53 in the machinery-hygiene family. Also a model-
evidence datapoint - both occurrences were Sonnet reviewers; no Opus lens has
produced one.
