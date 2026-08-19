# Idea: Retention/purge policy for completed tasks' run artifacts

Captured 2026-08-17 (quick idea; talk through later).

## The idea (Brian's framing)

Purge run files for tasks that have been complete past a configurable age — maybe 90 days,
maybe 180, maybe less or more; it should be configurable. The task record itself always
stays (events/projections in Postgres are the permanent history); it's the on-disk
artifacts that age out. Example: the agent's stream-json output file loses value as
historical context the longer it sits. Goal: keep the daemon and the node lean without
sacrificing functionality — strike the balance.

## Initial mapping (to refine later)

- Scope: ~/.hall9k/runs/<run-id>/ contents — stream.jsonl (biggest), prompt.md,
  settings.json, stderr.log, verify-*.log, pr-body.md. Later: content-addressed
  attachment store (§8.1) needs its own story (dedup means shared blobs — refcount
  or reachability, not simple age).
- Trigger: only terminal tasks (Done after closeout / Failed / Abandoned), aged past
  the configured retention from FinishedAt. Never purge NeedsHuman/AwaitingReview.
- Config: DaemonOptions (e.g. ArtifactRetention, default 90d?) — per-node; maybe
  per-project override later.
- Mechanism: a daemon sweep (startup + daily) — same family as the worktree prune.
- Record the purge honestly: cheap marker (RunArtifactsPurged event? or a document flag)
  so `h9k logs` says "purged per retention policy (Xd)" instead of a confusing
  file-not-found.
- Keep-the-distillate option worth discussing: before purging a transcript, keep the
  final result line (summary + tokens) or a few-KB digest — the "run artifacts:
  summary/decisions/questions" idea from PLAN.md §4 — so cheap context survives even
  after the bulky transcript goes.
- Roadmap fit: daily-driver hardening (roadmap #3).
