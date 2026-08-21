---
project: hall9k
type: feature
objective: Route context along dependency edges - a run hands off what it learned at true closeout, and a claimed task starts with its immediate blockers' summaries
criteria:
- A run's handoff summary (what it did, what a dependent needs to know, what it deliberately left undone) is captured from the agent's own session-end output, never authored by a separate session; the event carrying it lands only at TRUE closeout, on the same CloseoutEngine path that unblocks dependents, so an unmerged run's summary never travels
- A run that closes out without a usable summary is valid; the absence is recorded and visible, never silently empty (a parked run resolved by hand, historical streams)
- The summary is also written as an inspectable artifact in the run's directory alongside the review findings
- When DispatchEngine claims a task, the launched run's context includes the handoff summaries of its IMMEDIATE BlockedBy blockers only - never deeper ancestry (a needed two-hop fact is evidence of a missing edge, not a context gap; depth-one keeps the graph self-correcting)
- Blocker count above a configurable threshold (DaemonOptions, default 3) dispatches a synthesis pass that condenses the blockers' summaries into one context document before the dependent starts; below it, summaries pass through raw
- A task whose blocker died and was later retried to a successful merge receives the successful run's summary, never the failed run's - pinned by a test
- A blocker with no summary falls back to its objective and acceptance criteria
- h9k task show displays the context a task would receive if claimed now
- Historical streams without summaries replay and dispatch unchanged
- PLAN.md gains the decision-log entry; TASK-MODEL.md records the new event and the depth-one rule
- dotnet build and dotnet test pass
---
This is IDEA-context-routing promoted to a task (Brian, 2026-08-20), sequenced
deliberately BEFORE the 16/18/20 dependency-edge batch so those edges become this
feature's first live exercise. Read backlog/IDEA-context-routing.md in full before
designing - it carries the reasoning, including why agent-to-agent messaging is
rejected (the daemon routes context because a supervisor exists; a note depends on
agent A predicting what B needs, the daemon decides after the fact with knowledge A
could not have had).

Design constraints:
- The edge does double duty by construction: BlockedBy was built for scheduling, and
  this task makes it carry relatedness - no second structure, nothing to maintain.
- Capture-then-land split: summary text is parsed at session end (the result parsing
  already extracts a summary today - extend, do not duplicate); the handoff event is
  appended by CloseoutEngine at merge observation. Two moments, one fact, honestly
  ordered.
- Reviewer findings do NOT travel downstream in v1 - summary only. Noted as an open
  extension in the idea file; do not build it speculatively.
- TaskDependencyQuery's transitive closure stays for cycle detection; context
  assembly reads only the first hop, deliberately.
- Streams carry milestones: the summary event's text is the milestone content and
  stays bounded (the prompt instructs the agent to keep the handoff short); the run
  directory artifact is the inspectable copy.
- The synthesis pass is a platform-dispatched session following the review-session
  patterns (recorded model per decision #33, tokens recorded, artifacts in the
  dependent run's directory).
