---
project: hall9k
type: feature
objective: A project bound to an external tracker mirrors its tasks outward at the moments a human wants them visible - publish always creates the external item, a draft can opt in during refinement, and the platform-vs-agent line decides who writes
criteria:
- Publishing a task in a project bound to a tracker creates the external item: a GitHub issue written directly by platform code (title from the objective, body from criteria and context, labels from task type), or a Jira card via the existing agent-mediated publication session (task 18's machinery, triggered by publish instead of only push-to-jira); the created reference lands as the task's ExternalReference exactly as adopted items do
- A draft can opt in early via an explicit command or refinement prompt ("shaped enough to be visible?"); mirroring never fires automatically at task creation - the publish-floor and draft-opt-in rulings from the discovery record
- A task adopted FROM the tracker (--from-issue, --from-jira) is never re-mirrored back; its existing reference is the mirror
- At true closeout the platform closes a mirrored GitHub issue (a config-agnostic write) and keeps Jira's existing comment-only behaviour; abandonment of a mirrored task comments the external item with the recorded reason and closes the GitHub issue
- The mirror is a surface, not a sync: no state mirroring between publish and closeout, no inbound updates, and repos with their own issue conventions can route GitHub creation through the agent path via a project setting instead of the platform's bare issue
- dotnet build and dotnet test pass
---
Slice 5 of the project-centred structure (idea 64e4ebd2). GitHub on this repo
is the proving ground. Hierarchy mapping (dependency edges as sub-issues,
epics) is deliberately deferred until real usage shows which relationships
matter; the discovery record holds the open questions.
