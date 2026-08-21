---
project: hall9k
type: feature
objective: An idea fans out into any number of draft tasks through the ordinary add door, and an explicit human act concludes it - no single graduation ceremony
criteria:
- h9k task add --from-idea <id> --objective "..." creates ONE draft task sourced from the idea, through the same source-resolver seam as --from-issue and --from-jira (backlog 17); invoked repeatedly, one idea yields many tasks
- The objective is required per cut (several tasks cannot share the idea's first sentence); the idea's text and discovery-workspace pointer ride in as context automatically, with optional per-task --context on top, and --blocked-by works at cut time so discovery's output can be a dependency graph of drafts
- Provenance is repeatable, not terminal: each cut appends an event on the idea naming the spawned task, each task records its source idea, and h9k idea show displays the whole fan-out with each task's current state
- An idea has exactly two terminal states, both explicit human acts (Brian, 2026-08-21): CONCLUDED - h9k idea conclude <id> --reason - means discovery happened and something came of it (tasks cut, or an outcome acted on); ARCHIVED - h9k idea archive <id> --reason - means we went through discovery and chose not to pursue it. Cutting a task never auto-ends the idea, because discovery may keep producing
- The shipped 1:1 promote is reconciled honestly at design time: either it becomes sugar (cut one task with the first-sentence objective and conclude, in one command) or it is retired with its help text pointing at the new door - never two parallel doors with diverging semantics
- The shipped IdeaDiscarded is reconciled with archive at design time - one door, honestly renamed or superseded, never two names for setting an idea aside; "discovery found nothing to do" is the archive case, with the reason carrying what was learned
- dotnet build and dotnet test pass
---
Brian's question (2026-08-21): how does one idea become several tasks - is there
even a promotion ceremony? Conclusion: no. The ingestion-and-planning doc's
backbone already says Idea -> Discovery -> one or more Draft Tasks, and a fan-out
has no single graduation moment. The idea becomes a SOURCE in the resolver seam,
cutting is repeatable, and what ends an idea is an explicit human conclusion, not
the first task it produces.

Design constraints:
- Depends on backlog 17's resolver seam (in flight) and revises shipped backlog 22
  behavior - the reconciliation-of-promote criterion is the honest handling of
  that revision.
- The idea-shows-its-children display should reuse the dependent-count/status
  composition patterns rather than inventing a second rollup style.
