===heading===
# Follow-up task: fix the failing CI checks on an existing pull request
===intro===
The original task below already shipped in the pull request above, but its CI
checks are failing. Your job is to make the checks pass — not to redo the
original work.
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
===inspect-failures===
- Inspect the failures yourself: `gh pr checks {{PullRequestUrl}}` lists the checks,
  and `gh run view <run-id> --log-failed` shows a failing workflow's log.
===fix-and-rerun===
- Fix the causes and re-run the failing commands locally until they pass.
===closing-summary===
- End with a short summary: what was failing, what you changed, and any open
  questions.
