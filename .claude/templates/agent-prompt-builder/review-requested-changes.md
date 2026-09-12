===heading===
# Follow-up task: answer a reviewer's changes-requested review
===intro===
The original task below already shipped in the pull request above. A person has now
reviewed it and formally requested changes. Your job is to answer that review — not
to redo the original work.
===follow-up-reason===
Why this follow-up was dispatched: {{Reason}}
===original-objective-heading===
## Original objective (context, already implemented)
===project-links-heading===
## Project links (fetch yourself as needed)
===working-rules-heading===
## Working rules
===worktree-note===
- You are in an isolated git worktree checked out on the EXISTING pull-request
  branch `{{Branch}}`. Work only here.
===work-from-findings===
- Work from the findings above rather than rediscovering them: they were read off the
  pull request when this lap was dispatched, so spend your reading on the code they
  point at. Two things they can miss, both worth knowing rather than assuming away:
  a pull request carrying more than 100 review threads exceeds the provider's own
  page cap, and a reviewer may have submitted something new since. If a finding the
  review plainly refers to is not above, read the pull request for it and say so in
  your summary.
===resolve-skill===
- Use the resolve-review-threads skill for the mechanics of replying inside a thread
  and resolving it (the thread ids are already given above, as each finding's
  `{{ThreadTagKey}}=`). Its triage judgment does not apply here and it says so itself: a
  human's finding you disagree with is not settled in the thread, it is parked per the
  section above.
===closing-summary===
- End with a short summary: which findings you fixed, which you answered without a
  code change and why, and which one (if any) you parked as a disagreement.
===findings-heading===
## The review you are answering
===no-reviews===
No review findings were recorded with this follow-up. Say so in your summary and
read the pull request's own reviews yourself: `gh pr view --json reviews`.
===findings-intro===
Closeout already read the review, so it is quoted here rather than left for you to
find. Each finding opens with the same `{{FindingMarker}}` header a platform review pass uses,
with two tags deliberately missing: no `{{SeverityTagKey}}=` and no `{{ScopeTagKey}}=`, because the
reviewer graded neither and neither is yours to invent. Their standing is simpler —
a person requested changes, so every one of these is a point they want answered.

`{{ThreadTagKey}}=` names the review thread a reply would land inside. A finding with no
`{{ThreadTagKey}}=` is the review's own BODY, which GitHub makes unthreadable: there is nothing
to reply inside, so an answer to it can only be a top-level comment on the pull
request.
===time-not-reported===
time not reported by the provider
===no-location-finding===
{{FindingMarker}} (the review's own body — no file, no line, no thread)
===review-heading===
### Changes requested by @{{Reviewer}} ({{Submitted}})
===review-url===
Review: {{ReviewUrl}}
===no-findings-in-review===
Closeout read no body and no inline comments on this review. That is either a
reviewer who requested changes without stating what, or comments closeout could
not see — its thread read is capped at the pull request's first 100 threads.
Open the review above and read it yourself (`gh pr view --json reviews`, or
`gh api` for its comments) before concluding which. If the reviewer genuinely
stated nothing, say so in your summary rather than guessing at what they meant:
there is then nothing to fix and nothing to dispute.
===handling-heading===
## How to handle each finding
===handling-intro===
Read the finding and the code around it before deciding anything. Then:
===handling-fix===
- A finding you agree with gets the fix, then a reply inside its thread saying what
  changed, then the thread resolved — in that order. Never resolve before the reply
  is posted: a resolved thread with no answer in it reads as handled when it is not.
===handling-question===
- **A question gets an answer, not a code change.** If the honest answer is "yes,
  deliberately, because X", that reply IS the resolution. Inventing a change to look
  responsive is worse than saying nothing.
===handling-body-comment===
- A finding about the review's own BODY has no thread to reply inside. Answer it with
  a top-level comment on the pull request (`gh pr comment`) that names the review it
  answers and says what you did about each point. Never leave a review body
  unanswered.
===handling-never-open-thread===
- **Never open a new review thread.** Reply inside existing ones only. A thread's
  first comment is always a reviewer's, and that is the only way the next run can
  tell your comment from theirs.
===writing-conventions-lead-in===
**How every one of those replies reads.** The top-level comment and each in-thread reply are posted under the owner's own login, so this project's writing conventions govern every word of them:
===hides-comments===
What you cannot see: GitHub hides a review's comments while that review is still
PENDING (written but not submitted). So work the findings above, and never read
silence as "the reviewer had nothing more to say".
===disagreement-heading===
## When you disagree with a finding
===disagreement-intro===
The reviewer is a person. Telling them they are wrong is theirs to send, not yours:
**do not reply on the pull request, and do not resolve the thread.** Not a hedged
reply, not a "just noting" comment — nothing reaches the reviewer from you.
===fix-first===
Fix everything you honestly agree with first — those replies land immediately, and
they are the right thing to post. Then, for the finding you cannot accept, close your
summary with a block of exactly this shape:
===block-header===
    {{DisagreementMarker}} {{AtTagKey}}={{ExampleLocationPlaceholder}}; {{ThreadTagKey}}=THE-FINDINGS-OWN-THREAD; {{ReviewTagKey}}=THE-REVIEWS-URL
===reviewer-asked-line===
    {{ReviewerAskedMarker}} what they asked for, in your own words, fairly.
===reasoning-line===
    {{DisagreementReasoningMarker}} why you think otherwise — the pattern, constraint, or
    decision it rests on, and what you did instead.
===proposed-reply-marker-line===
    {{ProposedReplyMarker}}
===proposed-reply-body-line===
    The reply you would send, written to the reviewer, as you would send it.
===dispute-marker-line===
Then a final line reading exactly `{{DisputeMarker}}` (the last line of the summary,
above the HANDOFF block the section below asks for).
===fill-in-instructions===
Fill in every part of that header from the finding's own one above — the block is
dropped as an echoed example if you leave `{{AtTagKey}}={{ExampleLocationPlaceholder}}`
in it, which would park a run over a file this repository does not have. Drop `{{ThreadTagKey}}=`
entirely when the finding you dispute is the review's own body, which has no thread.
Write the proposed reply as prose addressed to the reviewer, not as a note to the
implementer — it is what they may send verbatim under their own name.
===park-platform===
The platform parks the run for the implementer with your three positions saved
beside it, and pushes nothing until they decide. They resolve it with
`h9k review resolve`, which offers them exactly three choices: post your reply as
written, post an edited one, or post nothing at all.
===park-once===
Park at most once: one block, for the finding that genuinely blocks this lap. This is
one honest attempt, not a negotiation, and it is not a way to escalate a finding you
simply do not feel like fixing.
===resolved-line===
When you handled everything, close the summary with `{{ResolvedMarker}}` instead.
