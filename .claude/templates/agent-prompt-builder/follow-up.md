===heading===
# Follow-up task: resolve review feedback on an existing pull request
===intro===
The original task below already shipped in the pull request above, which now has
unresolved review threads. Your job is to resolve that review feedback — not to
redo the original work.
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
===resolve-skill===
- Use the resolve-review-threads skill for the mechanics of triaging the unresolved
  threads on {{PullRequestUrl}} that the section above says you may read: give each one
  a disposition, apply the fixes, reply in-thread, and resolve per the rules above. Where
  the skill says to fetch a thread's comments, fetch only the comments that section names.
===closing-summary===
- End with a short summary: one THREAD DISPOSITION block per thread as the
  triage section above asks for, then `{{ThreadDispositionSummaryMarker}}` followed
  by which threads you fixed, which you declined or routed and why, and any open
  questions.
