---
project: hall9k
type: feature
objective: Connect Jira as a first-class work-item source and destination - import cards as tasks, create cards from tasks, and keep the reference honest in both directions
criteria:
- h9k connection add jira registers a Jira Cloud connection on the Connection aggregate (site URL, account email, API token) with the token stored per the existing CredentialRef discipline, never in an event payload
- A project can bind its Jira project key (h9k project set --jira <KEY>), visible in h9k project show
- h9k task add --from-jira <ISSUE-KEY> imports a card through the source-agnostic resolver seam from backlog 17: summary seeds the objective, description becomes agent context, the card key lands as ExternalReference; criteria remain human-supplied (never invented from a card)
- Card creation is agent-mediated, never modeled by the platform: h9k task push-to-jira <task> dispatches an agent run whose prompt carries the task's content and defers card semantics (issue types, fields, routing rules) to the project's own repo skills and the agent's MCP access; the platform then VERIFIES the agent-reported card key with a config-agnostic API read and records ExternalReference from the observed response, never from the agent's claim
- Closeout tells Jira what happened: when the monitor observes the merge of a task carrying a Jira reference, it comments on the card with the PR link (comment, not transition - status transitions are workflow-specific and deferred until real usage shows which ones matter)
- Every Jira API failure is loud and self-correcting (names the site, the key, and the likely auth fix); the platform never retries Jira writes blind
- dotnet build and dotnet test pass
---
Brian's direction (2026-08-20): Jira is needed close behind GitHub issues - his real
project management lives there, and Hall9k should both ingest cards and create them.
Import rides backlog 17's resolver seam; creation is explicit and on-demand.

Design constraints:
- The read/write asymmetry is the doctrine (Brian, 2026-08-20): reading Jira is
  config-agnostic (a GET returns the same shape however exotic the project's
  types), writing is config-laden (custom issue types like "dev task" vs "support
  request", org routing rules). So the platform's registered credential is for
  READS - import snapshots, verification of agent-reported keys - plus the one
  config-agnostic write (the closeout comment; comments are untyped). All card
  AUTHORING goes through agent runs using the project's repo skills and MCP,
  exactly the setup Brian's team already runs by hand: the org's Jira rules live
  in a skill file Hall9k never has to model, and DiscoverRepoSkills already
  delivers it to every dispatched agent.
- Verification is how never-guess survives agent-mediated writes: the platform
  records what it observed by reading, never what an agent reported doing.
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
