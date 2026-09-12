===heading===
## How to act on each disposition
===fix-rule===
- **fix**: apply it, then reply saying what changed. Resolve the thread once the
  reply is posted — bot-authored or human-authored, since a fix invites no
  argument.
===decline-rule-lead===
- **decline**: reply with the evidence — the scratch-repo demonstration or the
  code path, not just your disagreement. Then:
  - Bot-authored thread: resolve it. The evidence is what a bot needed; there is
    nobody left to answer.
  - Human-authored thread: leave it open. The evidence is posted, but closing a
    person's thread for them is not yours to do — they read it and resolve it
    themselves.
  - Human-authored thread whose reviewer's own `CHANGES_REQUESTED` verdict on this
    pull request still stands: post NOTHING — no reply, no resolve. Telling a
    person they are wrong is the implementer's to send, not an agent's, so a
    disagreement with a standing review is never posted by you: draft the reply,
    park it, and let them send it, edit it, or drop it (Decisions Log #152).
    Read the verdicts before you reply to any human thread — `gh pr view --json
    reviews` — and take each reviewer's latest `CHANGES_REQUESTED`, `APPROVED` or
    `DISMISSED` as the one that stands, ignoring their `COMMENTED` ones: GitHub
    wraps a plain thread reply in a COMMENTED review, so reading a reviewer's
    newest review of ANY type hides a changes-requested verdict that is still
    blocking the merge. It stands until that reviewer changes it themselves, so an
    earlier lap having already pushed fixes does not lift it. Park it through the
    section below, with the drafted reply in the block:
===decline-rule-markers===
    `{{DisagreementMarker}}` (carrying `{{AtTagKey}}=` and `{{ThreadTagKey}}=`), `{{ReviewerAskedMarker}}`,
    `{{DisagreementReasoningMarker}}`, `{{ProposedReplyMarker}}`. The thread stays open and unanswered,
    and nothing is pushed until a human decides. It still gets its triage block
    above (`{{DispositionTagKey}}=decline`) — that is measurement only and reaches nobody,
    and it is what stops the next sweep dispatching another lap over this thread.
===route-rule===
- **route**: reply naming the idea you filed and why it is out of scope here, then
  apply the same bot-resolves / human-stays-open rule decline uses.
===question-rule===
- **A question gets an answer, not a code change.** If the honest answer is "yes,
  deliberately, because X", that reply IS the resolution — usually a decline whose
  evidence is the answer itself, or a fix if the honest answer turns out to be
  "you're right".
===never-resolve-without-reply===
- **Never resolve a human's thread without replying substantively.** A resolved
  thread with no answer in it is worse than an open one: it reads as handled.
===one-attempt===
- One honest attempt per thread per follow-up; never re-litigate a point a
  previous run already answered.
===body-comment===
A review can also carry a BODY alongside its inline comments, and GitHub makes a
body unthreadable — there is nothing to reply inside. Answer it with a top-level
comment on the pull request (`gh pr comment`) that names the review it answers and
says what you did about each point. Never leave a review body unanswered, and
never leave a comment the reviewer has to connect back to their review themselves.
===writing-conventions-lead-in===
**How every one of those replies reads.** The top-level comment and each in-thread reply are posted under the owner's own login, so this project's writing conventions govern every word of them:
