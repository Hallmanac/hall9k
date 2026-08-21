---
project: hall9k
type: feature
objective: Projects declare their external work-item tracking, and assignment offers - never forces - to create the missing card or issue
criteria:
- A project-level preference (h9k project set --tracking none|github-issue|jira-card, default none, shown in project show) declares whether tasks in this project should carry an external work item for human-consumable planning
- At ASSIGNMENT of a task with a tracking preference and no ExternalReference, the interactive flow prompts: no card/issue exists for this task - create one before work starts? Accepting runs the creation flow; declining assigns as normal
- The decline is recorded as an event on the task (an observed human choice), and a declined task never re-prompts on later reassignment - the answer stands until a human changes it
- Non-interactive assignment (scripts, future automation) warns on stderr and proceeds; it never blocks and never creates silently
- Creation honors the read/write asymmetry (backlog 18): a GitHub issue is created by deterministic platform code (gh issue create from the objective and criteria, reference recorded from the observed response); a Jira card rides 18's agent-mediated path with the project's own skills, verified through the link-jira observation gate
- Tasks imported --from-issue or --from-jira already carry their reference and are never prompted; the created reference feeds the existing closeout behavior (the PR link lands on the card/issue at merge per 18)
- The prompt and warnings teach per CLI standards, naming the preference and how to change it
- dotnet build and dotnet test pass
---
Brian's direction (2026-08-21): his workflow needs tasks to produce Jira cards or
GitHub issues for human-consumable documentation and planning - but Hall9k will be
used by people who need neither, so it is a per-project preference, defaulting to
none. And the surfacing rule matters as much as the feature: Hall9k must never
aggressively spawn an agent because it noticed a missing card - the missing
reference surfaces as a prompt at assignment, where a human is definitionally
present and can simply decline.

Design constraints:
- Depends on 17 (reference plumbing, in flight) and 18 (Jira path) for the
  jira-card value; the github-issue value could ship with 17 alone.
- Assignment stays fast for the common case: the check is one field read; the
  prompt only appears when preference is set AND reference is absent AND no
  recorded decline exists.
