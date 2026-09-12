---
name: walk-pr-review-findings
description: Walk a pr-review task's findings report (or a mention follow-up's addendum) with the owner, finding by finding, and post exactly what they direct — a batched GitHub review, a plain comment, or a reply to a tagged comment — only on their explicit go. Use when a pr-review task (h9k task add --type pr-review --from-pr, or one auto-pr-review minted from a review request or a GitHub mention) is parked NeedsHuman with a report or addendum.
---

# Walk pr-review findings

A pr-review task reviews someone else's pull request on the owner's behalf, entirely read-only:
the dispatched run never writes to the pull request or the remote in any form, regardless of what
it found. Its sole output is a findings report, parked `NeedsHuman`. This skill is what happens
next — the interactive orchestrator session walking that report with the owner and posting
**only** what they direct, under their own `gh` login.

This is the intended act, not the agents-never-start-threads violation it would be on the
platform's own pull requests: that invariant (AGENTS.md, Git rules) exists to distinguish a
reviewer's comment from an agent's reply when every comment on a *platform-opened* PR is authored
under the human's login. On someone else's pull request the owner genuinely **is** the reviewer,
so posting a review under their login as their review is exactly what this skill exists to do.

## Before you start

1. **Find the report.** `h9k task show <id>` on the parked task prints the run's park reason,
   which names the file directly: `review-1-findings.md` under the run's directory for the
   original review, or `mention-followup-addendum.md` under a later run's own directory when this
   park is a mention follow-up's addendum (idea 2f079bcd — a GitHub comment tagged the install's
   own login after the original review, and this run answers it against the review already done).
   Read every file the park reason names — a follow-up dispatched while an earlier report was
   still parked and unwalked names both: its own addendum, and that earlier report's own path
   ("Its own findings report is also still parked and unwalked: …"), since the follow-up never
   repeats or replaces those findings. An addendum on its own carries only a "You were asked"
   section (see "Answering a tagged comment" below); walk any earlier report's own findings first
   (the "Process" steps below), then the addendum's tagged comment.
2. **Confirm nothing has been posted.** The run wrote only local files — no comment, no review,
   no reaction exists on the pull request yet. Nothing here assumes otherwise.
3. **Know the target.** The task's external reference (`h9k task show`) names the pull request:
   `owner/repo#number`. Every `gh` call below is scoped to it.

## Process

1. **Present findings one at a time** (or in small related groups), in the report's own order —
   adversarial first, conformance second, exactly as the report sections them. For each, show the
   owner: what it says, its severity/scope as the reviewer stated them, and (for conformance) that
   its basis may be thinner than a task's own acceptance criteria — the report already frames a
   thin-basis finding as a context note rather than a blocker, but the owner's judgment is final
   either way.

2. **Ask for a directive per finding**, one of:
   - **Dismiss.** Nothing posted for it. Say so and move on.
   - **I'll comment myself.** Nothing posted by this session; note it as handled and move on.
   - **Post it on my behalf.** Ask, if not already obvious from the finding: is this a blocking
     review comment (the default), or a conversational remark (praise, a non-blocking
     approve-anyway note)? A blocking comment joins the batched review below; a conversational
     remark becomes its own plain PR comment (step 4), never folded into the review body.

3. **Collect the batch.** Every "post it on my behalf" review comment accumulates — do not post
   as you go. Once every finding has a directive, ask the owner for the review's overall verdict:
   **comment**, **request changes**, or **approve** (GitHub's three review events). Assemble the
   full draft — every line-anchored comment (file, line, body) plus the overall review body and
   event — and **show it to the owner exactly as it will be posted**. Nothing is submitted from
   this step; it is the thing the owner is approving.

4. **Post only on explicit go**, per batch:
   - **The batched review**, once the owner says to send it — one call, all comments together, so
     it lands as a single formal review rather than a scatter of individual comments:

     ```bash
     REPO=owner/repo   # from the task's external reference
     NUMBER=42
     cat > /tmp/pr-review-batch.json <<'JSON'
     {
       "event": "COMMENT",
       "body": "Overall review summary the owner approved.",
       "comments": [
         { "path": "src/Foo.cs", "line": 42, "side": "RIGHT", "body": "The specific finding, as the owner approved it." }
       ]
     }
     JSON
     gh api "repos/$REPO/pulls/$NUMBER/reviews" -X POST --input /tmp/pr-review-batch.json
     ```

     `event` is `COMMENT`, `REQUEST_CHANGES`, or `APPROVE` — whichever the owner chose in step 3.
     `line`/`side` anchor to the pull request's current diff; a finding whose line no longer
     exists in the diff needs the owner's call on where (or whether) to anchor it before you post.
   - **Each conversational remark**, as its own plain comment — never batched with the review:

     ```bash
     gh pr comment "$NUMBER" --repo "$REPO" --body "The conversational remark, as the owner approved it."
     ```

   Post nothing else. No reactions, no thread replies beyond what this step just created, no
   second pass "while I'm here" comment.

5. **Answer a tagged comment, when the report ends with one.** A findings report or an addendum
   the task was minted or extended by a mention (idea 2f079bcd) ends with a "You were asked"
   section: the tagged comment verbatim with its author and time, the question in one sentence,
   the analysis citing the review's own files and lines, what could not be determined, and a
   drafted reply under its own "Drafted reply" heading. Everything above the drafted reply is
   context for the owner; only the drafted reply, or the owner's own edit of it, is ever a
   candidate for posting.
   - **Show the owner the full section**, drafted reply included, before asking anything.
   - **Ask for a directive**, the same three shapes a dismissed finding takes:
     - **Not mine to answer.** The comment was not genuinely addressed to the owner, or answering
       it is someone else's call. Nothing posted; record it and move on.
     - **Answered by hand already.** The owner has already replied outside this session. Nothing
       posted; record it and move on.
     - **Post it on my behalf.** Take the owner's edits to the drafted reply, if any, and show the
       final text back to them exactly as it will be posted before sending anything.
   - **Post only on explicit go**, as a reply in the *exact* thread the mention came from — a
     review-comment thread reply when the comment was one, a plain issue comment otherwise —
     never a new thread, and never anywhere else on the pull request. `h9k task show` tells you
     which: its "Tagged by" row names a **reply id** only when the tagged comment was an inline
     review-comment-thread reply — that numeric id is what the REST reply endpoint's own
     `in_reply_to` takes. The comment id shown beside it is GraphQL's own node id (`PRRC_…`),
     never REST's — sending it to `in_reply_to` 404s (`resolve-review-threads`'s own doc carries
     the identical warning). No reply id shown means the mention was a plain issue comment, a
     review's own top-level body, or the pull request's own description — none of those is a
     thread the REST endpoint can reply into, so post an ordinary comment instead:

     ```bash
     # A review-comment thread reply (h9k task show names a reply id):
     gh api "repos/$REPO/pulls/$NUMBER/comments" -f body="The drafted reply, as the owner approved it." \
       -F in_reply_to=<the reply id h9k task show showed, NOT the comment id>

     # An issue comment, a review body, or the pull request's own description (no reply id shown):
     gh pr comment "$NUMBER" --repo "$REPO" --body "The drafted reply, as the owner approved it."
     ```

     Post nothing else about it — no reaction, no second comment, no note that a session answered.

6. **Close the task.** Once every finding and every tagged comment has a directive, and everything
   the owner wanted posted is posted, resolve the park:

   ```bash
   h9k review resolve <task-id> --merge-ready
   ```

   This is the only verdict a pr-review task's park takes (`--needs-fixes` is refused — there is
   no diff of this task's own for a fix session to apply). It never opens or merges anything —
   the deliverable was the delivered review, or the delivered answer, not a diff — and it parks
   the task waiting on the pull request (`AwaitingAuthor`) rather than completing it outright: one
   pr-review task per pull request per install stays open until the pull request itself merges or
   closes, so a later mention on the same pull request has a live task to attach to instead of
   minting a second one. `h9k task abandon <task-id>` is the only early exit.

## What never happens here

- **Nothing posts without the owner's explicit go**, per batch, exactly as shown in step 3. A
  session assembling a batch and then submitting it unasked is the one thing this skill exists to
  prevent.
- **No line-anchored comment outside the batched-review call.** A single `gh pr review` invocation
  posts a review but cannot attach line comments to it in one step; `gh api .../reviews` with the
  full JSON payload is the only way to land the review and its comments together, atomically, as
  one formal submission rather than a scatter of individually-posted comments.
- **No approval or request-changes without the owner naming it.** The event type is always asked,
  never inferred from finding severity.
- **No reply to a tagged comment without the owner's explicit go**, in the exact thread it came
  from and nowhere else — the identical rule step 4's batched review already follows, applied to a
  single reply instead of a batch.
