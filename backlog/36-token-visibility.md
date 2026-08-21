---
project: hall9k
type: feature
objective: Token usage is visible and current - the PR footer stays updated across follow-up pushes and h9k task show reports per-run and task-total tokens, all formatted with underscore separators
criteria:
- The pull request footer line (run id and token count) is updated on every follow-up push, not written once at PR-open: it names the current run of record and the CUMULATIVE token total across every run that carried the pull request (origin example, 2026-08-21: PR 21's footer still named the original run and 18_401_309 tokens while twelve later generations had multiplied the real figure)
- h9k task show gains a tokens surface: one line per run (generation, run id short form, input/output tokens) and a task total, reading from the TokensRecorded events / RunDetails projection that already exist - display composition, no new capture
- Every token count rendered anywhere (PR footer, task show, any future status surface) goes through one shared formatter using underscore group separators (18_401_309), culture-invariant
- The closeout monitor's existing follow-up push seam is where the footer update rides - no new polling, no extra GitHub calls beyond the edit itself; a failed footer edit never fails the push or the run
- dotnet build and dotnet test pass
---
Brian's ask (2026-08-21, orchestrator session): are we updating the token number in
the PR description as we go (no - written once at open, stale by generation 4), how
do I see it for a task in h9k (you cannot yet - the events exist, no display reads
them), and format the numbers with underscores rather than commas.

Design constraints:
- DRAFT ONLY at creation - Brian refines this alongside the backlog documents
  before publishing.
- Collides with PR 21's files (PullRequestBody/PullRequestOpener, TaskShowCommand);
  queue only after task 17 reaches true closeout.
- Cumulative means every session the platform recorded for the task's runs: build
  sessions, review cycles, fix runs, follow-ups. The point is honest cost
  legibility, not just the build run's number.
