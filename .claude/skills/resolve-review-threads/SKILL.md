---
name: resolve-review-threads
description: Triage every unresolved review thread on a pull request, whoever wrote it, applying valid fixes, replying in-thread, and resolving. Use when a PR has unresolved review comments from Copilot, a teammate, or the author's own self-review.
---

# Resolve pull-request review threads

Read every unresolved review thread on a pull request, judge it, act on it, reply in the thread, and resolve it. This replaces the older `resolve-copilot-reviews` skill: Copilot is one reviewer among many, not the definition of review, and a teammate's unresolved thread is feedback on exactly the same footing (PLAN.md Decisions Log #62).

This skill works on an **existing** PR only. Never open a PR from an agent session: the Hall9k daemon opens PRs (`PullRequestOpener`), and agents are forbidden from doing so.

## Whose comment is whose

The one attribution rule you can rely on:

> **Agents never START review threads. They only ever reply inside existing ones.**

So the author of a thread's **first comment** is always a reviewer. That holds even when the author is the pull request's own login: agents commit and comment as the human (PLAN.md §6.6, no bot identity), so a thread the PR author started is a human reviewing their own work, and it is reviewer feedback like any other. Later comments in a thread are a different matter: a reply under the PR author's login may be the human's or a previous run's, so judge those by content.

Two consequences worth stating:

- **Hold the invariant yourself.** Reply within threads; never open a new review thread. Opening one would make the next run unable to tell your comment from a reviewer's.
- **Origin incident (2026-08-20).** Brian commented on PR #20 and the machinery was structurally blind to it: the closeout inspector filtered threads to Copilot authors, and agent replies under his own login made human and agent comments indistinguishable by author. The thread-starter rule is what survives that, and it survives only while the invariant above holds.

**A pending review is invisible.** GitHub hides a review's comments while that review is still `PENDING`, meaning the reviewer has written them but not clicked *Submit review*. They reach the API only on submit. Work the threads that exist and never read silence as "the reviewer had nothing to say".

## Process

1. **Identify the PR.** If no PR number is in the arguments, find the current branch's open PR with `gh pr list --head $(git branch --show-current)`. If none exists, stop and report; do not create one.

2. **Fetch the threads with their authors.** The REST comment endpoints have no thread-resolution data, so GraphQL is what answers "which threads are still open, and who started them":

   ```bash
   SLUG=$(gh repo view --json nameWithOwner -q .nameWithOwner)   # e.g. Hallmanac/hall9k
   gh api graphql -f query='
     query($owner:String!,$name:String!,$number:Int!) {
       repository(owner:$owner, name:$name) {
         pullRequest(number:$number) {
           author { login }
           reviews(last: 20) { nodes { author { login __typename } state body url } }
           reviewThreads(first: 100) {
             nodes {
               id isResolved isOutdated path line
               comments(first: 50) { nodes { id databaseId body url author { login __typename } } }
             }
           }
         }
       }
     }' -f owner="${SLUG%%/*}" -f name="${SLUG##*/}" -F number="$PR_NUMBER"
   gh pr diff "$PR_NUMBER"                                       # diff context
   ```

   `__typename` is `Bot` for app accounts (Copilot) and `User` for people: the provider's own answer, not a guess from the login string.

   Both comment ids are fetched because the two APIs do not share one. `id` is the GraphQL node id (`PRRC_…`), which is what the resolve mutation in step 9 takes; `databaseId` is the numeric REST id, which is what the reply endpoint in step 7 takes. Sending a node id to the REST endpoint 404s.

3. **Take every unresolved thread.** No author filter. Sort them so human threads come first: a person is waiting on an answer, a bot is not.

4. **Judge each thread**, reading the diff around it so you know what the code actually does:
   - **What it is**: a defect claim, a suggestion, a question, or a design disagreement.
   - **Verdict**: accept and fix, answer without changing code, or dismiss with reasoning.
   - **Reasoning**: brief, citing repo constraints (AGENTS.md, PLAN.md decisions, TASK-MODEL.md) or existing patterns where relevant.

   Dismissal guidance: a suggestion to refactor something that follows an established codebase pattern is usually dismissed for this PR; a suggestion that would break functionality is dismissed with an explanation; a valid-but-out-of-scope suggestion is dismissed and flagged as a follow-up, in the reply and in your summary.

5. **Human threads get more care than bot threads.** Same mechanics, higher bar:
   - **A question gets an answer, not a code change.** If the honest answer is "yes, deliberately, because X", that reply *is* the resolution. Inventing a change to look responsive is worse than saying nothing.
   - **Never resolve a human's thread without replying substantively.** A resolved thread with no answer in it is worse than an open one: it reads as handled. Reply first, resolve after.
   - **One honest attempt per thread.** Say your piece once, with reasoning. Never re-litigate a point a previous run already answered.
   - **A design disagreement you cannot honestly judge is not yours to settle.** Do not pick a side to close the thread. Hand it to a human (see below).

6. **Apply accepted changes** before replying, so the reply describes something that exists.

7. **Reply in the thread**, because feedback is answered where it lives. `$COMMENT_ID` is the numeric `databaseId` of a comment in the thread (the first one is the reviewer's, and replying under it is what puts your answer in that thread), never the `PRRC_…` node id:
   ```bash
   gh api "repos/$SLUG/pulls/$PR_NUMBER/comments/$COMMENT_ID/replies" -f body="…"
   ```
   Accepted: acknowledge and state what was fixed. Dismissed: explain why, citing the pattern, constraint, or decision. Answered: give the answer. Concise and technical; never rude, even when the suggestion is wrong.

8. **Answer a review BODY with a top-level comment.** A review's body text is not a thread and GitHub gives you nothing to reply inside. Use `gh pr comment "$PR_NUMBER" --body "…"`, naming the review it answers (its author and URL from step 2) and summarising what you did about each point. Never leave a review body unanswered, and never leave an unanchored comment the reviewer has to connect back themselves. (Origin: the PR #20 human review was answered only through the work itself, with no visible reply on the PR.)

9. **Resolve the thread** once its reply is posted:
   ```bash
   gh api graphql -f query='mutation($id:ID!){ resolveReviewThread(input:{threadId:$id}) { thread { isResolved } } }' -f id="$THREAD_ID"
   ```

10. **Commit any changes** following the repo's rules: the `commit-plan` skill, or `absorb-review-fixes` when the branch uses the narrative commit style. Agents never push: the platform verifies and pushes follow-up branches.

11. **Report**: a summary table (thread, author, verdict, reasoning) plus any follow-ups worth tracking.

## Handing a disagreement to a human

If a thread is a design disagreement where the reviewer's position and yours are both defensible and the call belongs to a person: handle every other thread first (your replies land on the PR immediately), then close your summary with a line reading exactly

```
RESOLUTION: disputed
```

Above it, record **both** positions: what the reviewer asked for and their reasoning, what you would do instead and yours, and what you already did. When this skill is running inside a Hall9k follow-up run, that marker parks the run as `NeedsHuman` with your text saved beside it and nothing is pushed until a human decides (`h9k review resolve`). Park at most once: this is one honest attempt, not a negotiation.
