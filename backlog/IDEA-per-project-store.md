> **ARCHIVED 2026-08-23: superseded by idea 64e4ebd2 (the project-centred structure) - the database-as-room model in its DISCOVERY.md replaces this file's framing entirely.**

# IDEA: The project chooses its store - per-project database pointers

**Status:** Draft - funnel entry, not a decision
**Origin:** Brian, 2026-08-21, rejecting owner-wide shared Postgres for teams

## The objection that produced this

The pre-P2P team topology on the roadmap was "several daemons, one shared
Postgres" - but a shared store is OWNER-WIDE: it would hold every owner's
projects, including personal side projects the teammates must not see. That is
exactly the boundary the P2P design already refused to cross ("some projects are
personal, so peers must never learn they exist" - the reason peering is
per-project, never node-to-node). Wholesale store-sharing violates at the
database layer what the P2P design protects at the network layer.

## The idea

Each PROJECT points at a database. One owner's nodes sharing one store stays fine
(all projects are theirs). Team sharing happens per project: the shared work
project points at a Postgres the team can reach; everyone's personal projects
stay on their local stores. The sharing unit becomes the project - the same
boundary as P2P membership, delivered over a connection string instead of a wire
protocol. When P2P lands, it replaces the transport, not the boundary.

## What it would take (flagged honestly - this is not a small change)

- Multiple DocumentStores per daemon, keyed by the project's connection; today
  there is exactly one store and one connection string.
- A home store question: Owner, Node, Ideas, and owner-scoped learnings are not
  project data - they need a designated home store (the local default).
- Cross-project surfaces (h9k status, project list) become fan-in queries across
  stores; the doorbell and dispatch loop listen per store.
- Leases and claims already carry owner identity (decision #39's claims-belong-
  to-the-owner shape), so multi-owner correctness inside one shared project store
  mostly rides existing machinery - the fencing was built for this.
- The doctor check (backlog 24) grows a per-project dimension.

## Open questions

- Is this worth building as the pre-P2P team path, or does its cost argue for
  going straight to P2P? (Brian's instinct: owner-wide sharing is too much
  infrastructure for an interim; per-project pointers might be the honest interim
  - or the argument for skipping interims.)
- Migration: moving an existing project's streams between stores (export/import,
  or the P2P sync machinery arriving early in disguise).
- Where the project-to-connection binding itself lives (it cannot live only in
  the store it points to - bootstrap problem; likely the home store).
