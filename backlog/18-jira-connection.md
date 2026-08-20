---
project: hall9k
type: feature
objective: Connect Jira as a first-class work-item source and destination - import cards as tasks, create cards from tasks, and keep the reference honest in both directions
criteria:
- h9k connection add jira registers a Jira Cloud connection on the Connection aggregate (site URL, account email, API token) with the token stored per the existing CredentialRef discipline, never in an event payload
- A project can bind its Jira project key (h9k project set --jira <KEY>), visible in h9k project show
- h9k task add --from-jira <ISSUE-KEY> imports a card through the source-agnostic resolver seam from backlog 17: summary seeds the objective, description becomes agent context, the card key lands as ExternalReference; criteria remain human-supplied (never invented from a card)
- h9k task push-to-jira <task> creates a Jira card from an existing task (objective as summary, criteria and context as description) and records the created key as the task's ExternalReference - the create-cards direction Brian asked for, on demand rather than automatic
- Closeout tells Jira what happened: when the monitor observes the merge of a task carrying a Jira reference, it comments on the card with the PR link (comment, not transition - status transitions are workflow-specific and deferred until real usage shows which ones matter)
- Every Jira API failure is loud and self-correcting (names the site, the key, and the likely auth fix); the platform never retries Jira writes blind
- dotnet build and dotnet test pass
---
Brian's direction (2026-08-20): Jira is needed close behind GitHub issues - his real
project management lives there, and Hall9k should both ingest cards and create them.
Import rides backlog 17's resolver seam; creation is explicit and on-demand.

Design constraints:
- Platform-native and deterministic: the daemon and CLI talk to Jira's REST API with
  the registered credential. This is deliberately separate from agents' own MCP
  access (dispatched agents inherit the user's Atlassian MCP and may read Jira
  in-run; the PLATFORM's writes go through the connection, auditably).
- Direction of truth: Hall9k is the source of truth for task state; Jira is a
  mirror surface. Never import state changes from Jira after the initial snapshot,
  and never let a Jira edit mutate a task (the board-as-truth lesson from the
  Collaboard comparison, and the same stance taken for GitHub issue mirroring).
- Bidirectional status sync, automatic card creation at publish, and webhook-driven
  updates are all explicitly deferred - each is a policy decision that should wait
  for real usage (and for backlog 05's lifecycle, which changes what "created" and
  "started" even mean).
- Auth scope: Jira Cloud API token first (Brian's instance); server/data-center
  variants out of scope until needed.
