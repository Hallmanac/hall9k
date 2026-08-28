---
name: walk-pr-review-findings
description: Walk a pr-review task's findings report with the owner, finding by finding, and post exactly what they direct — a batched GitHub review or a plain comment — only on their explicit go. Use when a pr-review task (h9k task add --type pr-review --from-pr) is parked NeedsHuman with a findings report.
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
   which names the findings-report path directly (`review-1-findings.md` under the run's
   directory). Read it.
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

5. **Close the task.** Once every finding has a directive and everything the owner wanted posted
   is posted, resolve the park:

   ```bash
   h9k review resolve <task-id> --merge-ready
   ```

   This is the only verdict a pr-review task's park takes (`--needs-fixes` is refused — there is
   no diff of this task's own for a fix session to apply). It closes the task without opening or
   merging anything; the deliverable was the delivered review, not a diff, and closeout's
   merge-watch never applied to this task in the first place.

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
