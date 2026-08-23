> **ARCHIVED 2026-08-23: the mechanism shipped as decision #34 (assignment as dispatch trigger, claim guard, publish --assign offer). The multi-owner remainder (reassignment authority, anyone-assignment, notification) is carried forward by IDEA-ranking-and-grooming, which cites and revises this file's central rule.**

# Idea: task assignment to owners (multi-owner projects)

Captured 2026-08-17 from a conversation about publish semantics once nodes are
peer-to-peer. Not schedulable until multi-owner projects and P2P sync exist
(see IDEA-p2p-lazy-sync). The core mechanism ships earlier in backlog 05:
assignment (an explicit human TaskAssigned event) is the dispatch trigger, and
the claim guard checks the assigned owner. This idea file holds only what
multi-owner adds on top.

## The concern

Once teammates run their own nodes against a shared project, "published" cannot
mean "any node grabs it." A whole epic might be intended for one teammate; a
task picked up by someone else's node without the author's knowledge is
sometimes fine and sometimes very much not.

## The rule (one rule, no special cases)

A node may claim a task only if the task's assigned owner is the node's owner.

- Assignment is always an explicit human act (TaskAssigned). Single-owner
  projects may be OFFERED auto-assign at publish as CLI sugar producing the
  same event; multi-owner projects are never offered it.
- Multi-owner project: until someone assigns, nobody's nodes can claim.
  Arbitrary pickup is structurally impossible, not policy-forbidden.
- Which of an owner's nodes runs the task is the owner's business - their
  nodes race for the lease exactly as the single-node claim works today
  (Append(expectedVersion) is already the lock).

## Open questions for the future design

- Assigning a set (an epic / a dependency chain from backlog 05) in one act:
  assignment probably wants to operate on a published graph, not one task at
  a time.
- Reassignment: who may reassign (author? current assignee? any owner?), and
  what happens to a task mid-lease when reassigned (probably: refuse while
  leased; requeue-then-reassign is the explicit path).
- An explicit "anyone" assignment for multi-owner projects that genuinely want
  a shared pool - opt-in, never the default.
- Whether assignment implies notification (assignee's nodes ring their own
  doorbell on sync; the human sees "you were assigned X" in h9k status).
