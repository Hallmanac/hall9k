---
project: hall9k
type: feature
objective: A run is inspectable as a thing in itself - h9k task show gains a per-run drill-down with the model each session ran and why, the phase story, timing, and token cost, and the PR footer stays honest across follow-up pushes
criteria:
- h9k task show <task> --run <run-id-or-prefix> renders one run's detail: its generation, state, phase timeline (build, gates with outcomes, each review cycle with verdicts, fix sessions, follow-ups), start and end times per phase where recorded, and the teach-line pointing at h9k logs <task> --run for the full transcript - progressive disclosure, never the transcript itself
- The run detail names the model every session of the run used AND its provenance in the resolution chain (task override, node role, project, platform default), read from what the dispatch actually recorded - never re-derived after the fact; a session dispatched before model recording existed says so honestly rather than guessing
- Effort joins model on the same line the day backlog 56 lands - the surface is built so that recording it is a display addition, not a redesign
- The runs table already at the bottom of plain h9k task show gains per-run input/output token columns and a task total, reading from the TokensRecorded events / RunDetails projection that already exist - display composition, no new capture
- The table's existing Model column stops answering yesterday's question: it shows the model of the run's CURRENT session - the one the State column is describing - so an UnderReview row names the reviewing model, not the build model it shows today; a terminal run shows its last session's model by the same rule, and a compact marker appears whenever the run's sessions spanned more than one model, with the --run drill-down holding the full per-session timeline (ruled at the 2026-08-24 walk, origin incident same day: the row read State UnderReview beside Model claude-sonnet-5 while both review lenses were running claude-opus-5[1m], and Brian reasonably read it as the role split being violated)
- The pull request footer line (run id and token count) is updated on every follow-up push, not written once at PR-open: it names the current run of record and the CUMULATIVE token total across every run that carried the pull request (origin example, 2026-08-21: PR 21's footer still named the original run and 18_401_309 tokens while twelve later generations had multiplied the real figure)
- Every token count rendered anywhere (PR footer, task show, any future status surface) goes through one shared formatter using underscore group separators (18_401_309), culture-invariant
- The closeout monitor's existing follow-up push seam is where the footer update rides - no new polling, no extra GitHub calls beyond the edit itself; a failed footer edit never fails the push or the run
- dotnet build and dotnet test pass
---
Brian's asks, now two sittings deep. 2026-08-21: are we updating the token
number in the PR description as we go (no - written once at open, stale by
generation 4), how do I see it for a task in h9k (you cannot yet - the events
exist, no display reads them), format with underscores. 2026-08-24: the
natural next intuition - task show lists the runs, so a run should be
showable, with the model it is using and why, without dumping the transcript;
the transcript stays h9k logs' job and the run view teaches the command.

Command-shape ruling from the 2026-08-24 walk: the drill-down hangs off task
show (--run) rather than a first-class h9k run command, because runs live
under tasks everywhere else - decision #79 made it literal on disk, h9k logs
already anchors --run under its task, and decision #66 keeps run vocabulary
as drill-down material rather than top-level surface.

Design constraints:
- DRAFT ONLY at creation - Brian refines alongside the backlog documents
  before publishing.
- Model provenance must come from dispatch-time records (the daemon logs it
  today; the run stream should carry it) - never inferred later from current
  settings, which may have changed since. Never-guess applies to provenance.
- Cumulative means every session the platform recorded for the task's runs:
  build sessions, review cycles, fix runs, follow-ups. The point is honest
  cost legibility, not just the build run's number.
