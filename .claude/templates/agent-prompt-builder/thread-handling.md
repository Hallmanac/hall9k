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
you are answering. Every read is unchanged.

**A fix reply is held, not posted.** `--disposition fix` vets your words against the
writing conventions and records them on the run; it posts nothing. You never push, the
platform does, after its gates, and a reply that says Fixed must not reach the pull
request before the fix does. So the platform posts a fix reply, and resolves its thread,
only after its push has moved the pull request's head, and it appends the push range on
its own line (the short shas before and after, and GitHub's compare link). Do not write
that line, and do not resolve a fix's thread yourself: the platform does both, and a
lap that pushes nothing posts nothing. A decline or a route is unaffected and posts at
once into a bot's thread, which you then resolve (`gh api graphql … resolveReviewThread`).
===fix-rule===
- **fix**: apply it, commit it, then record the reply saying what changed. The platform
  posts it and resolves the thread after it pushes, bot-authored or human-authored,
  since a fix invites no argument and the commit is the evidence. Do not resolve the
  thread yourself.
  - **A finding already satisfied on the head this lap started from is not a fix.** If
    the pull request as GitHub shows it already carries the change (not a local commit
    this lap made), there is no push to wait for and nothing to claim as fixed. Treat it
    as a decline whose evidence names the commit that satisfies it: post that on a
    bot's thread, and on a person's draft it and park it exactly as any other decline.
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
body unthreadable, so there is nothing to reply inside. Answer it with one top-level
comment, and submit every such answer through the platform whatever its disposition,
never through `gh pr comment` or `gh issue comment` (both are refused before they run):

```
{{ReplyCommand}} {{TaskId}} --review REVIEW_URL --disposition fix|decline|route --body "your text"
```

`REVIEW_URL` is the review's own address (`<pull request url>#pullrequestreview-<id>`).
The comment that is posted names that review, so write only what you did about each
point. The command reads the review's author from GitHub, not from you. A fix is held
like a thread's: the platform posts it as that top-level comment after its push has
moved the pull request's head, with the push range on its own line, and a lap that
pushes nothing posts nothing. When the review is a person's and your answer is a decline or a route, nothing
is posted: the command records the refusal, and you park the answer exactly as for a
person's thread, with `{{DisagreementMarker}}` carrying `{{ReviewTagKey}}=REVIEW_URL`
and `{{DispositionTagKey}}=decline` or `route`. Submit that decline through the command
anyway, because the recorded refusal is what lets the owner send the reply you drafted.
A bot's review body posts at once on a decline or a route, and holds a fix. Never leave a review body unanswered, and
never leave a comment the reviewer has to connect back to their review themselves.
===writing-conventions-lead-in===
**How every one of those replies reads.** The top-level comment, each in-thread reply, and each reply you draft for the owner to send are all read under the owner's own login, so this project's writing conventions govern every word of them:
