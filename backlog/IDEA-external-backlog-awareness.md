> **VERIFIED LIVE 2026-08-23: decisions #60/#65 built fetch-by-key adoption only; nothing surveys a tracker before work starts (no similar-item query, no epic placement). Natural first mission of idea discovery runs; schedule with that work.**

# IDEA: External-backlog awareness - discovery surveys the tracker before work duplicates

**Status:** Draft - funnel entry, not a decision
**Origin:** Brian, 2026-08-21 (the two-nodes-no-P2P walkthrough)

## The gap

Hall9k holds a subset of the real backlog. The broad spectrum - every Jira card,
every GitHub issue, every epic and its planning history - lives in the tracker, and
neither ingestion nor discovery consults it today. Two failure shapes:

1. **Within one node**: an idea gets ingested and worked when the tracker already
   holds an in-progress card, prior planning for the same feature, or an epic this
   work belongs under. Duplication and orphaned placement.
2. **Across nodes without shared state** (no P2P, no shared Postgres): two
   teammates' nodes independently ingest the same card and produce duplicate
   branches and PRs. GitHub-arbitrates catches it post-hoc at review; nothing
   catches it before the tokens are spent.

## The shape

A survey step in discovery (and optionally at task add): an agent mission against
the tracker asking three questions about the idea/task at hand -

- Is there already an in-progress work item for this?
- Has this (or something similar) already been planned - prior cards, epics,
  design docs?
- Does this belong under an existing epic or parent item?

Findings land wherever the work is happening (Brian, 2026-08-21): in the idea's
DISCOVERY workspace when the survey runs during discovery, or in the task's
REFINEMENT context when you went straight to a task (or already promoted) and
discover mid-refinement that related backlog items exist - bring them in as
context and planning input right there. Either way the findings carry provenance,
inform the draft's context, and feed the tracking preference (backlog 32): a card
created at assignment is created UNDER the right epic, not orphaned.

## Fits

- **Slim agents + CLIs (29/30)**: the survey is CLI-shaped work - twg ships
  similar-issue discovery and duplicate detection; gh covers issues. No MCP needed.
- **Discovery runs (IDEA-draft-refinement-runs)**: the survey is a natural first
  mission of `h9k idea discover`.
- **Ingestion doc's mandatory-discovery doctrine**: "an idea always goes through
  discovery" gains teeth when discovery includes the duplicate check.
- **The two-node interim**: the survey is also the honest mitigation for
  independent nodes - the tracker is the shared truth both nodes CAN see, so
  checking it narrows the duplicate-work window even without shared state. The
  full answer for teams pre-P2P remains one shared Postgres (roadmap multi-node,
  leases already safe); the survey helps either way.

## Open questions

- Advisory or blocking: does a strong duplicate signal park the ingestion for a
  human, or just annotate the context? (Never-loop suggests: annotate, and let
  the human decide at publish.)
- Does the survey run automatically on every ingestion with a tracker-bound
  project, or only when discovery is explicitly dispatched? Cost per survey vs
  duplicate risk.
- In-progress detection across teammates' Hall9k nodes is only as good as their
  tracker hygiene (cards moved to in-progress); state the dependency honestly.
