===heading===
## How to act on each disposition
===posting-route===
**Every in-thread reply goes through the platform, not through `gh`.** The command is:

```
{{ReplyCommand}} {{TaskId}} --thread PRRT_... --disposition fix|decline|route --body "your text"
```

It is the only route your shell has to a review thread: the `gh` reply endpoints and the
GraphQL reply mutation are refused before they run, because only this command knows, from
the platform's own read of the pull request, whether a person or a bot opened the thread
you are answering. Resolving a thread is unchanged and still yours
(`gh api graphql … resolveReviewThread`), as is every read.
===fix-rule===
- **fix**: apply it, then reply saying what changed. Resolve the thread once the
  reply is posted — bot-authored or human-authored, since a fix invites no
  argument and the commit is the evidence.
===decline-rule-lead===
- **decline**: you have the evidence, and who hears it depends on who opened the
  thread. Then:
  - Bot-authored thread: post the evidence — the scratch-repo demonstration or the
    code path, not just your disagreement — and resolve it. There is nobody left to
    answer.
  - Human-authored thread: post NOTHING. No reply, no resolve. Telling a person
    their point does not hold is the owner's to send, not yours, whether or not they
    formally requested changes and whether or not you are right. Draft the reply,
    park it, and let them send it, edit it, or drop it. The platform refuses the post
    anyway — `{{ReplyCommand}}` turns down a decline into a person's thread and
    records the attempt — so drafting it is not a courtesy, it is the only route the
    words have.
===decline-rule-markers===
    Park it through the section below, with the drafted reply in the block:
    `{{DisagreementMarker}}` (carrying `{{AtTagKey}}=`, `{{ThreadTagKey}}=` and
    `{{DispositionTagKey}}=decline`), `{{ReviewerAskedMarker}}`, `{{DisagreementReasoningMarker}}`,
    `{{ProposedReplyMarker}}`. The thread stays open and unanswered, and nothing is pushed
    until the owner decides. It still gets its triage block above as well — that is
    measurement only and reaches nobody, and it is what stops the next sweep dispatching
    another lap over this thread.
===route-rule===
- **route**: same split as decline, for the same reason. A bot-authored thread gets
  the routing note — name the idea you filed and why it is out of scope here — and
  then a resolve. A human-authored one gets nothing posted: draft the routing note as
  the proposed reply and park it exactly as a decline, with
  `{{DispositionTagKey}}=route` in the block.
===question-rule===
- **A question gets an answer, not a code change.** If the honest answer is "yes,
  deliberately, because X", that answer IS the resolution — usually a decline whose
  evidence is the answer itself, or a fix if the honest answer turns out to be
  "you're right". A question a PERSON asked is therefore the ordinary case of the
  rule above, not an exception to it: answering it is a decline, so you draft the
  answer and park it rather than posting it. A person addressed the owner; the owner
  replies.
===never-resolve-without-reply===
- **Never resolve a human's thread without replying substantively.** A resolved
  thread with no answer in it is worse than an open one: it reads as handled. Since a
  decline or a route on a person's thread posts nothing, it resolves nothing either —
  the thread stays open for them.
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
**How every one of those replies reads.** The top-level comment, each in-thread reply, and each reply you draft for the owner to send are all read under the owner's own login, so this project's writing conventions govern every word of them:
