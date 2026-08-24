---
project: hall9k
type: feature
objective: The node keeps a registry of known databases and every project records which one it lives in - the database is the trust room, a project lives in exactly one, and the CLI and daemon federate across the rooms this node has joined
criteria:
- The node holds a registry of database connections: the local default plus any shared rooms joined by connection string; h9k has commands to list rooms and join one, and joining verifies the connection before recording anything
- The registry itself lives in the node's local database, which is why a local database is constitutive of being a node rather than optional: it is always reachable before any remote room is, it solves the registry's own bootstrap problem without making a file authoritative, and it is where project-less ideas and the default room already live - a node whose projects all point at remote rooms still keeps its local database, holding the registry and the standing local-first baseline (ruled 2026-08-24, Brian's walk question)
- A project lives in exactly one database, recorded at project add (local default, remote by choice) and shown by project show; nothing anywhere creates or implies a second copy of a project's streams - the one-database law from the discovery record, stated in code and help text
- h9k status, task list, and project list federate across every registered room, each row saying which room it came from when more than one exists; one unreachable room degrades to a named warning for that room, never a blank board
- The daemon dispatches across rooms: it sweeps each registered database for claimable work owned by this node's owner, and leases live in the project's own room
- Dependencies are refused across projects in different rooms with a teaching refusal naming the law; within one room the existing rules stand
- Owner identity stays per-database (matched by convention, unified by P2P keys later); nothing introduced here assumes a global owner id
- dotnet build and dotnet test pass
---
Slice 4 of the project-centred structure (idea 64e4ebd2). One entry today (the
local room), so this lands trivially on the current install - and it is the
door the shared-room collaboration step walks through: an invitation IS a
connection string, and sharing a local project means moving it to a reachable
room first (the move is the separate project-move capability, not this task).
