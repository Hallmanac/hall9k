---
project: hall9k
type: feature
objective: Make the model each session runs on a deliberate, recorded platform decision instead of a silent inheritance of the human's personal default
criteria:
- ClaudeExecutor passes --model explicitly on every spawn; the platform never again inherits whatever the human's personal settings default happens to be that day (origin incident, 2026-08-20: runs drifted from Fable 5 to Opus 5 1M when Brian changed his own setting, with no platform record that it happened)
- Model resolution is a chain, most specific wins: task-level override (h9k task add --model) > per-role default > project default (h9k project set --model) > platform default (DaemonOptions); every level is optional and the chain bottoms out at an explicit platform default
- Per-ROLE defaults are first-class because the roles have different shapes: the build session, the review session, the fix session, and (future) refinement runs are each independently configurable - a review session reads far more than it writes and may warrant a different tier than a fix session
- RunDispatched records the resolved model as an observed fact; h9k task show and the run projections display it, and with the per-run token accounting from backlog 10 this makes spend-by-model a queryable question instead of a guess
- Review and fix sessions record their models the same way on their session milestones
- The default configuration ships conservative: everything on one configured model, no tiering - the point of this task is the KNOB and the RECORD, not an opinion about which model which task deserves
- dotnet build and dotnet test pass
---
Brian's question (2026-08-20): are agents auto-adjusting model to task complexity?
They are not - nothing chooses at all. This task makes the choice deliberate,
per-role, and recorded. Deliberately NOT in scope: automatic complexity-based
selection (an agent or coordinator judging "this task only needs Sonnet with high
thinking"). That is a judgment layer that should wait for data - once models are
recorded and token accounting is per-run, a few weeks of real usage will show
whether review sessions on a smaller tier actually hold the quality bar (the PGID
catch and the seven-cycle daemon review are the bar). Capture the auto-selection
idea in the coordinator-agent's orbit when the data exists.

Design constraints:
- Subscription mode framing: spend here is usage-limit burn, not API dollars -
  which makes over-provisioning invisible until limits bite. Observability first.
- The model value is a closed-vocabulary value object per house style (known
  tiers plus a passthrough for exact model ids), stored as the string claude -p
  accepts.
- Interaction with sessions that resume (--resume for review re-prompts): the
  resumed session keeps its original model; record, do not re-resolve.

## Post-queue direction (Brian, 2026-08-20 - the running agent has the frozen
## snapshot above; reconcile these at PR review)

- Sane default: Opus 5 (1M) for everything the platform dispatches - build,
  review, fix, and future refinement/info-gathering sessions. Fable is the
  human-interactive tier (long discovery and problem-solving sessions the human
  is in), not a silent-agent default. Data point from 71 sessions: per-session
  output volume is nearly identical (~30k) across both models, so the saving is
  tier weight, not session length.
- Precedence question to settle at review: Brian wants the NODE able to
  override the project ("on this node, run Fable"). DaemonOptions is already
  per-node configuration, so this is about where the node default sits in the
  chain relative to the project default - the frozen spec has project above
  platform/node; Brian's direction puts node above project.
- Multi-executor future: model identity stays an open value (exact-id
  passthrough), never a Claude-only closed set - other executors (other CLIs,
  other providers) are on the long-range map.
