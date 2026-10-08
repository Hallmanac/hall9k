---
name: resolve-review-threads
description: Triage every unresolved review thread on a pull request, whoever wrote it, into fix, decline (with evidence), or route — before any fix work — then apply, reply in-thread, and resolve. Use when a PR has unresolved review comments from Copilot, a teammate, or the author's own self-review.
---

# Resolve pull-request review threads

Read every unresolved review thread on a pull request, give it a disposition, act on it, reply in the thread, and resolve it per that disposition. This replaces the older `resolve-copilot-reviews` skill: Copilot is one reviewer among many, not the definition of review, and a teammate's unresolved thread is feedback on exactly the same footing (PLAN.md Decisions Log #62, #159).

This skill works on an **existing** PR only. Never open a PR from an agent session: the Hall9k daemon opens PRs (`PullRequestOpener`), and agents are forbidden from doing so.

**A decline or a route on a thread a person opened is not yours to post.** Not the reply, not the resolve — whether or not they formally requested changes, whether or not they approved the pull request in the same breath, and whether or not you are right. You usually are right; that was never the question. Telling a colleague their point does not hold is the owner's to send, because the reply goes out under the owner's login. Draft it, park, and let them send it, edit it, or drop it: the block shape is `DISAGREEMENT:` (carrying `thread=` and `disposition=decline|route`) / `REVIEWER ASKED:` / `MY REASONING:` / `PROPOSED REPLY:`, then `RESOLUTION: disputed`, and `h9k review resolve --post-reply-as-written` / `--post-reply "<text>"` / `--post-nothing` is what actually sends it (PLAN.md Decisions Log #62, #152, #159). Answering a question counts: a question you answer with "yes, deliberately, because X" is a decline, so it is drafted and parked like any other.

Origin incidents, both with replies that were accurate and still had to be deleted: arx-platform PR #2021 (2026-09-09) and PR #2042 (2026-09-15), where a follow-up answered a reviewer in the owner's name minutes after they approved.

Everything a **bot** opened is handled here, in-thread, as it always has been. A **fix**'s reply, in anyone's thread, is still answered in-thread, but inside a Hall9k follow-up the platform posts it (and resolves the thread) after its own push has moved the pull request's head, instead of the session posting it during the lap.

**Inside a Hall9k follow-up, in-thread replies go through the platform.** The command is `h9k pr reply <task> --thread <node id> --disposition fix|decline|route --body "<text>"`, and the `gh` reply routes are refused before they run — only that command can tell a bot's thread from a person's. A **fix** reply is held there rather than posted: the platform posts it, and resolves the thread, only after its own push has moved the pull request's head (steps 7 and 9). Running this skill standalone, outside a dispatched run, the `gh` route in step 7 is what you have, and you push the fix yourself before replying; the rule above still applies, and the draft goes to whoever asked you to run the skill.

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

4. **Triage every thread before touching any code.** Read the thread and the diff around it, then give each one exactly one disposition (task: every review thread on a pull request gets a triage disposition before any fix work — origin: PR #199 and PR #229, two full fix laps in two days bought by Copilot claims that turned out false on inspection, both resolved by hand on Brian's word after evidence, with no way for the lifecycle to do that itself):

   - **fix** — the finding is real and in scope. The only disposition that earns a code change. A finding the pull request **already satisfies on the head this lap started from** (the pull request as GitHub shows it, not a local commit this lap made) is not a fix: there is no push to wait for and nothing to claim as fixed. It is a decline whose evidence names the commit that satisfies it, so it is posted on a bot's thread and drafted and parked on a person's.
   - **decline** — you have reproduction-grade evidence it does not hold up: a scratch-repo demonstration (`mktemp -d`, reproduce the claim, show the actual behavior), or a pointer to the code path that already handles it. Disagreeing is not evidence. "I don't think that's right" is not a decline; "here is the command and its output" is.
   - **route** — real, but out of this task's own scope. File it rather than growing this diff: `h9k idea add "<text>" --project <name>`.

   Do not apply any fix until every thread has a disposition. A triage where every thread comes back decline or route pushes nothing — that is a legitimate outcome of this gate, not a failure to find work, and the run returns to watching the pull request exactly as if nothing had changed.

   Dismissal is now decline or route, not a third bucket: a suggestion to refactor something that follows an established codebase pattern is a decline citing the pattern; a suggestion that would break functionality is a decline citing why; a valid-but-out-of-scope suggestion is a route, filed as an idea rather than only mentioned in a reply.

5. **Human threads get more care than bot threads, at every disposition.** Same mechanics, higher bar:
   - **A decline or a route posts nothing at all.** See the carve-out at the top of this skill: draft the reply, park it, and let the owner send it. Steps 7 and 9 below apply to a bot's thread, and to a fix's reply in anyone's, where the platform does the posting and the resolving after it pushes.
   - **A question gets an answer, not a code change.** If the honest answer is "yes, deliberately, because X", that answer *is* the resolution — usually a decline whose evidence is the answer itself, occasionally a fix if the honest answer turns out to be "you're right". Inventing a change to look responsive is worse than saying nothing. Since answering a question is a decline, a question a *person* asked is drafted and parked rather than posted.
   - **Never resolve a human's thread without replying substantively.** A resolved thread with no answer in it is worse than an open one: it reads as handled.
   - **One honest attempt per thread.** Say your piece once, with reasoning and evidence. Never re-litigate a point a previous run already answered.
   - **A design disagreement you cannot honestly judge with evidence is not yours to settle.** That is different from decline: decline disproves a claim, this is a genuine "both positions are defensible." Do not pick a side to close the thread. Hand it to a human (see below).

6. **Apply fixes** before replying to their threads, so the reply describes something that exists. Threads disposed decline or route get no code change.

7. **Reply in the thread**, because feedback is answered where it lives — for a bot's thread on any disposition, and for a fix's reply in anyone's. A decline or a route on a person's thread posts nothing; it is drafted and parked (see the carve-out above).

   Inside a Hall9k follow-up:
   ```bash
   h9k pr reply "$TASK_ID" --thread "$THREAD_ID" --disposition fix|decline|route --body "…"
   ```
   A **fix** reply is held, not posted: the command vets your words and records them on the run. You never push; the platform does, after its gates, and a reply saying Fixed must not reach the pull request before the fix does. So it posts the reply after its push has moved the pull request's head, and it appends the push range (the short shas before and after, and GitHub's compare link) on its own line, so do not write that line yourself. A lap that pushes nothing posts nothing. A decline or a route on a bot's thread posts at once.
   Standalone, `$COMMENT_ID` is the numeric `databaseId` of a comment in the thread (the first one is the reviewer's, and replying under it is what puts your answer in that thread), never the `PRRC_…` node id:
   ```bash
   gh api "repos/$SLUG/pulls/$PR_NUMBER/comments/$COMMENT_ID/replies" -f body="…"
   ```
   Fix: acknowledge and state what was fixed. Decline: state the evidence — the scratch-repo output or the code path — not just the disagreement. Route: name the idea filed and why it sits outside this task. Concise and technical; never rude, even when the finding is wrong.

   **Whose voice.** Every reply here posts under the owner's own login, so when the owner has named a voice skill (`h9k owner set --voice-skill <name>`) the prompt that dispatched you names it: load that skill and its `contexts/code-review.md` context before writing, and let it decide the prose while the rules in this step decide what the reply has to contain.

8. **Answer a review BODY with a top-level comment.** A review's body text is not a thread and GitHub gives you nothing to reply inside. Name the review it answers (its author and URL from step 2) and summarise what you did about each point. Never leave a review body unanswered, and never leave an unanchored comment the reviewer has to connect back themselves. (Origin: the PR #20 human review was answered only through the work itself, with no visible reply on the PR.)

   Inside a Hall9k follow-up, submit every review-body answer through the platform, whatever its disposition. The shell's own `gh pr comment` and `gh issue comment` are refused there, and `$REVIEW_URL` is the review's own address (`<pull request url>#pullrequestreview-<id>`):
   ```bash
   h9k pr reply "$TASK_ID" --review "$REVIEW_URL" --disposition fix|decline|route --body "…"
   ```
   The command posts one top-level comment that names the review, and it reads the review's author from GitHub rather than taking your word. A fix is held like a thread's (step 7): the platform posts it as that top-level comment after its push has moved the pull request's head, with the push range on its own line, and a lap that pushes nothing posts nothing. A decline or a route on a person's review body posts nothing and is recorded as refused, so draft it and park it exactly as step 7's carve-out does for a person's thread, with `review=$REVIEW_URL` in the `DISAGREEMENT:` block. Submit the decline through the command anyway: the recorded refusal is what lets the owner send your draft. A bot's review body posts at once on a decline or a route, and holds a fix.

   Standalone, outside a Hall9k follow-up, there is no platform to route through, so post the comment yourself:
   ```bash
   gh pr comment "$PR_NUMBER" --body "…"
   ```

9. **Resolve the thread**, once its reply is posted, per its disposition and its author:
   - **fix**: inside a Hall9k follow-up, do **not** resolve it: the platform resolves the thread right after it posts the held reply, once its push has moved the head, bot-authored or human-authored. A thread you resolved yourself before that would read as answered over a head that does not carry the fix yet. Standalone, you push first, then resolve it — a fix invites no argument.
   - **decline or route, bot-authored**: resolve it. The evidence (or the routing note) is what a bot needed; there is nobody left to answer.
   - **decline or route, human-authored**: nothing was posted and nothing is resolved. The thread stays open and unanswered until the owner decides what it hears. **Agents never answer or close a person's thread on a decline or a route.**
   ```bash
   gh api graphql -f query='mutation($id:ID!){ resolveReviewThread(input:{threadId:$id}) { thread { isResolved } } }' -f id="$THREAD_ID"
   ```

10. **Commit any changes** following the repo's rules: the `commit-plan` skill, or `absorb-review-fixes` when the branch uses the narrative commit style. Agents never push: the platform verifies and pushes follow-up branches. A triage with nothing disposed fix has nothing to commit — that is expected, not an error.

11. **Report**: one `THREAD DISPOSITION:` block per thread, back to back with nothing between them —

    ```
    THREAD DISPOSITION: thread=<node id>; disposition=fix|decline|route; kind=human|bot; author=<login>
    <why: the fix's brief restatement, the decline's evidence, or the route's scope reason>
    ```

    — with the thread's own real node id, never the `<node id>` placeholder text above left in place. Then a line reading exactly `SUMMARY:`, followed by a plain-language recap and any follow-ups worth tracking — put it there, not between the blocks or before them, or it is read as the last thread's own evidence rather than a closing note. When running as a Hall9k follow-up, this is what `AgentPromptBuilder.BuildFollowUp`'s own summary instructions already ask for; running the skill standalone, write the same blocks anyway — they are what makes the triage measurable rather than only remembered.

## Handing a reply to a human

Two things come here: a thread that is a genuine design disagreement where both positions are defensible and the call belongs to a person, and **every decline or route on a thread a person opened**. Handle everything else first (those replies land on the PR immediately), then close your summary with a line reading exactly

```
RESOLUTION: disputed
```

Above it, under the `SUMMARY:` line step 11 asks for, record **both** positions: what the reviewer asked for and their reasoning, what you would do instead and yours, and what you already did. For each thread a person opened, add a block:

```
DISAGREEMENT: thread=<node id>; disposition=decline|route; at=path/to/file.cs:123
REVIEWER ASKED: <their point, in your own words>
MY REASONING: <why you think otherwise, with the evidence>
PROPOSED REPLY: <the words the owner will actually send, verbatim if they approve them>
```

Write the proposed reply as a reply, not as a report about the thread: it goes out as-is if they choose `--post-reply-as-written`. When this skill is running inside a Hall9k follow-up run, that marker parks the run as `NeedsHuman` with your drafts saved beside it and nothing is pushed until a human decides — `h9k review resolve` offers to send each drafted reply as written, send their own text instead, or send nothing. Park at most once: this is one honest attempt, not a negotiation.
