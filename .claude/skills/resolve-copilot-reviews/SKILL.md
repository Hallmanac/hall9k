---
name: resolve-copilot-reviews
description: Analyze GitHub Copilot's automated review comments on a pull request, apply valid fixes, reply to each thread, and resolve them. Use when a PR has unresolved Copilot review comments.
---

# Resolve GitHub Copilot PR Review Comments

Analyze and respond to GitHub Copilot's automated review comments on a pull request: review each comment, decide whether it's valid, apply accepted fixes, then reply to and resolve each thread.

This skill works on an **existing** PR only. Never open a PR from an agent session — the Hall9k daemon opens PRs (`PullRequestOpener`); agents are forbidden from doing so.

## Process

1. **Identify the PR**: If no PR number is provided in the arguments, detect the current branch and find its open PR with `gh pr list --head $(git branch --show-current)`. If a PR number was given, use that. If no PR exists, stop and report — do not create one.

2. **Fetch Copilot review comments**: Extract the repo slug with `gh repo view --json nameWithOwner -q .nameWithOwner`, then run in parallel:
   - `gh api repos/{owner}/{repo}/pulls/{number}/reviews` for the review summary
   - `gh api repos/{owner}/{repo}/pulls/{number}/comments` for the individual line comments
   - `gh pr diff {number}` for the diff context

3. **Filter to Copilot comments only**: Only process comments from `copilot-pull-request-reviewer[bot]` or user login `Copilot`. Never touch threads from human reviewers.

4. **Analyze each comment**: Read the diff context around each comment so you judge what the code actually does, then determine:
   - **What it's suggesting** — one-sentence summary of the concern
   - **Verdict** — **Accept** (correct, should be implemented) or **Dismiss** (wrong, not applicable, or out of scope)
   - **Reasoning** — brief, referencing repo constraints (AGENTS.md, PLAN.md decisions, TASK-MODEL.md) or existing patterns where relevant

   Dismissal guidance:
   - A suggestion to refactor something that follows an established codebase pattern is usually dismissed for this PR.
   - Suggestions that would break functionality (even if theoretically "safer") are dismissed with an explanation.
   - Valid-but-out-of-scope suggestions are dismissed but flagged as a follow-up in the reply and in your final summary.

5. **Apply accepted changes**: Implement the fix for every Accept verdict before replying.

6. **Reply to each thread**:
   ```
   gh api repos/{owner}/{repo}/pulls/{number}/comments/{comment_id}/replies -f body="..."
   ```
   - Accepted: acknowledge and state what was fixed. Example: "Good catch — fixed. The handler now clears both fields to prevent stale references."
   - Dismissed: explain clearly why, citing the specific pattern, constraint, or spec. Example: "Not addressing in this PR — this follows the existing decider pattern; refactoring would touch all callers."
   - Concise and technical; never rude, even when the suggestion is wrong.

7. **Resolve the Copilot threads** via GraphQL:
   - Fetch thread IDs:
     ```
     gh api graphql -f query='query { repository(owner:"...", name:"...") { pullRequest(number:N) { reviewThreads(first:50) { nodes { id isResolved comments(first:1) { nodes { body author { login } } } } } } } }'
     ```
   - Resolve each unresolved Copilot thread:
     ```
     gh api graphql -f query='mutation { resolveReviewThread(input: {threadId: "..."}) { thread { isResolved } } }'
     ```

8. **If changes were made**: Commit following the repo's rules (see the `commit-plan` skill — no attribution trailers) and push to the existing PR branch.

9. **Report**: End with a summary table — comment, verdict, reasoning — plus any follow-ups worth tracking.
