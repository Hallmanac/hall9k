---
project: hall9k
type: feature
objective: Separate task development from task dispatch with a Draft/Published/Assigned lifecycle and BlockedBy dependency chains
criteria:
- h9k task add creates tasks in Draft; a real id, invisible to the dispatcher; creation requires only project and objective (identity, not readiness)
- h9k task publish enforces the full readiness contract (checkable acceptance criteria) with self-correcting errors and moves Draft -> Published; a Published task is visible, referenceable, and ready to assign, but NOT claimable and NOT editable
- A task can be revised (objective, criteria, context, dependencies) via a TaskRevised event the decider permits ONLY in Draft
- h9k task draft <id> returns a Published task to Draft for refinement (refused from Assigned onward; unassign first) - the edit-after-the-fact path is unassign -> draft -> revise -> publish -> assign, each step an explicit act
- h9k task assign <id> <owner> is the dispatch trigger and is always an explicit human act: it moves Published -> Queued when all dependencies are truly complete, otherwise -> Blocked
- h9k task unassign returns a Queued or Blocked task to Published for further revision; it is refused while a lease is held
- The dispatch claim guard is: task is Queued AND task's assigned owner == node's owner (single rule; no other path to a claim)
- The CLI publish flow MAY offer auto-assign as an opt-in convenience when the project has exactly one owner (producing the same explicit TaskAssigned event); with more than one owner it never offers it
- BlockedBy dependencies can be declared at creation or via revision; a dependency counts as complete only at true closeout (RunCompleted from the closeout monitor); Draft, Published, Queued, Running, and AwaitingReview dependencies all block by the same rule
- When a task reaches true closeout, its dependents re-evaluate: assigned tasks with no remaining unmet dependencies move Blocked -> Queued and the doorbell rings
- Dispatch order is unchanged within the ready set: FIFO by AddedAt among Queued tasks; dependencies and assignment shape the ready set, not the ordering
- Publishing a task whose dependency chain contains a cycle is refused with a self-correcting error naming the cycle (drafts may transiently hold cycles while a graph is authored)
- A dependency that reaches Failed or Abandoned parks its assigned dependents NeedsHuman with the reason (do not silently unblock, do not silently strand)
- Draft, Published, and Blocked states are visible in h9k status, including a Blocked task's unmet dependencies and every task's assignee
- h9k task abandon works on Draft and Published tasks
- dotnet build and dotnet test pass
---
Today TaskAdded goes straight to Queued and the daemon claims it within seconds. That
conflates two different lifecycles: task development (discovery produces a draft, it
gets refined, eventually it passes the readiness gate) and task dispatch (a human
decides this task should run now, and on whose nodes). This task separates them:

  Draft (editable) -> Published (immutable, assignable; the readiness gate)
       -> Assigned (dispatch trigger) -> Queued or Blocked
       -> claimed by the assigned owner's nodes as today
  Reverse edges are explicit: unassign (Assigned -> Published, refused while
  leased) and draft (Published -> Draft). Each state has exactly one meaning:
  Draft = being developed, Published = ready to assign, Assigned = should run.

Design constraints:
- Hard dependency on 04 (closeout monitor): "dependency complete" means RunCompleted,
  which only exists once merge detection does. Do not invent a weaker completion signal.
- Assignment is the ONLY way a task becomes claimable, and it is always an explicit
  human decision recorded as TaskAssigned. Auto-assign is CLI sugar that emits the
  same event after asking; it is never silent and never applies to multi-owner
  projects. (Multi-owner semantics themselves are future work: IDEA-task-assignment.)
- Revision happens only in Draft because each later state carries a promise that
  editing would break: Published promises "a human may assign this at any moment and
  it satisfies the readiness contract" (so validation lives at publish alone, as an
  invariant of the state), and Assigned promises "a node may read this at any moment"
  (revising a claimable task races the dispatcher). The explicit revert-to-draft
  ceremony is deliberate, not accidental friction.
- Cycle detection lives at publish only: drafts may transiently reference cycles
  while a graph is being authored; a cycle can never become assignable.
- Decided (Brian, 2026-08-17): draft-by-default (creation is part of discovery),
  assignment-as-trigger (publish is the quality gate, not the go signal),
  human-decided assignment with opt-in single-owner auto-assign, and
  edit-only-in-Draft with an explicit Published -> Draft revert.
- New events and states go through TaskDecider with guards, and TASK-MODEL.md is
  updated (likely TaskPublished, TaskRevised, TaskAssigned, TaskUnassigned; document
  the choices in the decisions log with the usual why/does-this-block-later line).
- Both projections (TaskListItem, TaskDetails) surface the new states and assignee;
  the daemon's queue query must remain a cheap indexed-friendly filter
  (State == Queued plus assigned owner).
- Multi-node: unblocking is driven by the closeout monitor's RunCompleted append, so
  whichever node completes the dependency triggers re-evaluation. Keep the
  re-evaluation query cheap (tasks in Blocked whose BlockedBy contains the id).
- Migration: existing tasks predate these states; anything currently Queued or beyond
  is treated as already published and assigned to the sole owner. Do not strand the
  historical streams.
