# Hall9k — Slice 1 Breakdown (Task 4 output)

Slice 1 = parallel fire-and-forget (PLAN.md §13): `h9k task add` → queue → daemon claims →
worktree → agent → verify → PR → `h9k status`. Every task below is written in the §4
readiness-contract format (objective, context pointers, acceptance criteria, constraints,
type) — dogfooding the contract before Hall9k can enforce it.

**The flip point is after S1-10.** Once the pipeline runs end-to-end, S1-11 onward — and all
of Slice 2 — are dispatched *through* Hall9k. The breakdown is ordered to reach that point on
the shortest path; niceties come after the flip so the system builds them itself.

Ordering note: S1-02 through S1-05 are sequential (each builds on the last). S1-06/S1-07 can
proceed in parallel with S1-08/S1-09/S1-10 once S1-05 lands, if more than one pair of hands
(or agents) is available.

---

## S1-01 · Solution skeleton

- **Objective:** The solution from PLAN.md §11.1 builds and tests green, locally and in CI.
- **Type:** chore
- **Context:** PLAN.md §11.1; TASK-MODEL.md header (package versions).
- **Acceptance criteria:**
  1. `dotnet build` and `dotnet test` succeed from a clean clone (one placeholder test).
  2. All six src projects + Hall9k.Tests exist with the settled reference graph — no extra references.
  3. `Directory.Build.props` (net10.0, nullable, implicit usings), `Directory.Packages.props`
     (Marten 8.17.0, WolverineFx(.Marten) 5.9.2, UUIDNext 4.2.3, Spectre.Console.Cli), `.editorconfig` in place.
  4. GitHub Actions workflow builds + tests on `ubuntu-latest` **and** `windows-latest` (log #3).
- **Constraints:** no application code beyond `Program.cs` stubs; CLI packs as a dotnet tool named `h9k`.

## S1-02 · Persistence backbone + reference aggregates

- **Objective:** Marten + Wolverine are configured house-style, and Owner/Node/Project/Connection
  streams round-trip through a real Postgres.
- **Type:** feature
- **Context:** TASK-MODEL.md §4, §7, §8; docker-compose.yml.
- **Acceptance criteria:**
  1. `AddMartenEventStore()` extension: STJ serialization (camelCase, enums-as-string),
     lightweight sessions, Wolverine integration with fast event forwarding, inline projections.
  2. Owner, Node, Project, Connection slices exist (flat files — small-slice rule) with events,
     aggregates, deciders, and one projection each, per TASK-MODEL.md §4.
  3. Value-object plumbing exists and is exercised: the §8 anatomy (one closed-vocab VO with
     `Unknown` sentinel + STJ converter), `Optional<T>`, `CredentialReference`, `ContextLink`,
     `.IsBlank()`-style string extensions.
  4. Integration test tier (Testcontainers Postgres): registering a project round-trips —
     events appended, aggregate rehydrates, projection queries back.
  5. Unit test tier: `FakeEvent<T>` stub proves a projection testable without a database.
- **Constraints:** no Task/Run types yet; IDs via UUIDNext only.

## S1-03 · Task + Run domain slices

- **Objective:** The full TASK-MODEL.md Task and Run slices exist with deciders and projections,
  unit-tested through every state transition.
- **Type:** feature
- **Context:** TASK-MODEL.md §2, §3, §5, §6 (the model doc is the spec — implement it, don't reinvent it).
- **Acceptance criteria:**
  1. All Task/Run events, both aggregates, `TaskState`/`RunState` transitions, and the four
     projections (`TaskDetails`, `TaskListItem`, `RunDetails`, `RunListItem`) compile and pass unit tests.
  2. Decider methods enforce the readiness contract: `TaskDecider.Add` rejects a task with no
     objective or no acceptance criteria (`DomainValidationException`).
  3. Claim path proven in an integration test: two concurrent `TaskClaimed` appends → exactly one
     wins (optimistic concurrency), generation increments, `RunId` carried on the claim.
  4. `TaskLease` and `RunActivity` telemetry documents exist and are upserted without touching streams.
- **Constraints:** no daemon logic; pure domain + tests.

## S1-04 · CLI skeleton — the thin writer

- **Objective:** `h9k` runs from the PATH and can register projects, add tasks, and list/show them.
- **Type:** feature
- **Context:** decision log #8 (thin CLI); TASK-MODEL.md §7; Spectre.Console.Cli docs.
- **Acceptance criteria:**
  1. `h9k project add --name X --repo <path> [--base-branch main]` registers a project (+ default
     Connection + Owner/Node bootstrap if missing).
  2. `h9k task add --project X --objective "…" --criteria "…" [--type feature]` and
     `h9k task add --file task.md` (frontmatter + body) append `TaskAdded` and fire raw `NOTIFY`.
  3. `h9k task list` / `h9k task show <id>` render from projections (Spectre tables); work with the
     daemon stopped.
  4. Readiness-contract violations exit non-zero with the decider's message on stderr.
  5. No Wolverine host in the CLI; cold start `h9k task list` < 500 ms against local Postgres.
- **Constraints:** no dispatch, no status aggregation yet; command surface matches PLAN.md §13 sketch.

## S1-05 · Daemon skeleton — claim loop + Aspire dev loop

- **Objective:** `h9kd` runs under the AppHost, hears the doorbell, and claims queued tasks with leases.
- **Type:** feature
- **Context:** decision log #7, #8, #10; TASK-MODEL.md §2 (claim atomicity), §6 (telemetry docs).
- **Acceptance criteria:**
  1. `Hall9k.AppHost` starts Postgres + daemon; dashboard shows daemon logs.
  2. Daemon connects with retry/backoff (survives Postgres starting late), LISTENs for the
     doorbell, and also sweeps by polling — a task added while the daemon was down is claimed on startup.
  3. Claiming appends `TaskClaimed` (new generation, minted `RunId`) and creates/updates `TaskLease`;
     heartbeat refreshes it on a timer.
  4. Concurrency cap respected: with cap N and N+2 queued tasks, only N are claimed until one finishes.
  5. Startup order implemented as adopt → sweep expired → claim (log #7), with adoption a no-op stub
     until S1-07 gives it real processes.
- **Constraints:** claiming only — dispatch lands in S1-07; keep `IProcessManager`/installer seams
  cross-platform (log #3).

## S1-06 · Worktree manager

- **Objective:** The daemon turns a claim into an isolated, correctly-branched worktree, and cleans
  up by state.
- **Type:** feature
- **Context:** decision log #4; the workspace's bare-repo + sibling-worktree layout.
- **Acceptance criteria:**
  1. Worktree created per run at `wt-<task-short>-<run-short>/`, branch `task/<id>-<slug>` via
     `git worktree add --no-track -b … origin/main`; `main` is never checked out outside `dev/`.
  2. Git operations serialized per-repo (mutex): 5 parallel worktree creations against one repo all succeed.
  3. Cleanup policy: done → removed; `needs-human`/`failed` → retained until the task closes;
     `git worktree prune` runs at daemon startup.
  4. Unit-testable against any local git repo (integration test uses a temp repo, not hall9k's own).
- **Constraints:** worktree logic behind an interface the executor consumes; no agent spawning here.

## S1-07 · Executor — spawn, capture, monitor

- **Objective:** A claimed task becomes a live `claude -p` agent whose output is captured
  restart-proof, with completion detected from the stream.
- **Type:** feature
- **Context:** decision log #1, #2, #9, #11; TASK-MODEL.md §3; `claude --help` (verify flags at build time).
- **Acceptance criteria:**
  1. `IExecutor` spawns `claude -p` in the worktree: daemon-minted `--session-id`, subscription
     flags by default (`ExecutorMode.UsesBareFlag` governs `--bare`), `--settings` disabling
     co-authored-by, `--dangerously-skip-permissions` when the project opts in, prompt assembled
     from the task's agent context + objective + acceptance criteria + project `ContextLink`s.
  2. Stdout redirected to `~/.hall9k/runs/<run-id>/stream.jsonl`; the daemon tails the file, not a pipe.
  3. `RunProcessStarted` records PID + process start time; tailing updates `RunActivity`
     (last-activity + cursor).
  4. The final `result` event — not the exit code — produces `AgentSessionCompleted` + `TokensRecorded`
     (tokens parsed from the result payload).
  5. Restart-proof: kill the daemon mid-run; the agent finishes; the restarted daemon adopts via
     PID + start time, resumes tailing from its cursor, and completes the run correctly.
  6. First end-to-end smoke: a trivial task ("append a line to SCRATCH.md") runs to `AgentSessionCompleted`
     against a throwaway repo.
- **Constraints:** macOS implementation of `IProcessManager` only (log #3); no verification/PR yet.

## S1-08 · Verification runner

- **Objective:** A completed run's worktree is verified against the project's gates before anything
  reaches a PR.
- **Type:** feature
- **Context:** decision log #15 (VerifyCommand); PLAN.md §6.5 (deterministic gates only — the
  reviewer agent is Slice 3).
- **Acceptance criteria:**
  1. After `AgentSessionCompleted`, the daemon executes the project's `VerifyCommand`s sequentially in the
     worktree; all pass → `VerificationPassed`; any fail → `VerificationFailed` (failed gate names)
     + `TaskFailed`.
  2. Command output captured to `~/.hall9k/runs/<run-id>/verify-<name>.log`.
  3. A project with no verify commands passes with an explicit "no gates configured" note on the event.
- **Constraints:** gate commands run with the worktree as cwd; timeout per gate (default 15 min) → failure, not hang.

## S1-09 · Pull request opening

- **Objective:** A verified run becomes a pushed branch and an open PR, authored as the owner.
- **Type:** feature
- **Context:** decision log #1 (no co-authored-by); PLAN.md §6.6; `gh` CLI.
- **Acceptance criteria:**
  1. On `VerificationPassed`: branch pushed, `gh pr create` with title from the objective and body
     from the run summary + acceptance criteria; `PullRequestOpened`, then `RunCompleted` + `TaskCompleted` appended.
  2. PR body contains no bot attribution; commits carry no Co-Authored-By trailers.
  3. `RunState` reaches `AwaitingReview`; worktree cleanup per policy fires only after the task closes.
- **Constraints:** target repo's default branch as PR base (project `BaseBranch`); no auto-merge.

## S1-10 · `h9k status` + `h9k logs` — the attention surface

- **Objective:** One command answers "what needs me?"; another shows any run's transcript.
- **Type:** feature
- **Context:** PLAN.md §12; decision log #11 (stall display); TASK-MODEL.md §5 (compose in the query handler).
- **Acceptance criteria:**
  1. `h9k status` renders queued / running (with last-activity age) / awaiting-review / needs-human
     / done / failed across projects; runs silent > 1 h show a stalled marker.
  2. `h9k logs <task>` streams the latest run's `stream.jsonl` (human-readable rendering; `--raw` for JSONL).
  3. Both work with the daemon down (projections + files only).
- **Constraints:** display state composed Task-state-then-Run-state per TASK-MODEL.md §2; no `--watch` yet.

---

### ✂ THE FLIP (dogfood from here) — **PASSED 2026-08-16**

**Gate met**: task `bb544945` ("Add version output to the h9k CLI") ran the full pipeline —
claim → worktree → agent → build+test gates → push → PR #1 — Copilot's review comments were
resolved by agent+skill, and the owner merged. From here, tasks go through `h9k task add`.

**Gate: the S1-10 demo.** Add a real task against the hall9k repo itself ("add `h9k --version`
output formatting", or similar), watch it claim → worktree → agent → verify → PR, merge the PR.
From this point every task below — and everything in Slice 2 — is added via `h9k task add` and
dispatched by the daemon. Manual coding after the flip is the exception and needs a reason.

---

## S1-11 · `h9k task add --from-issue <url>` (dispatched via Hall9k)

- **Objective:** An existing GitHub issue seeds a ready task with one command.
- **Type:** feature
- **Context:** PLAN.md §3.1a, §9.2; TASK-MODEL.md (ExternalReference, WorkItemProvider);
  `Hall9k.Connectors` project.
- **Acceptance criteria:**
  1. `IWorkItemProvider` (fetch + link for v0) with `GitHubWorkItemProvider` over `gh issue view --json`.
  2. `--from-issue <url>` fetches title/body, seeds objective + agent context, sets
     `ExternalReference`, and still enforces the readiness contract (missing criteria → prompt
     the user or reject, not silently pass).
  3. Duplicate adoption of the same issue is rejected with a pointer to the existing task.
- **Constraints:** no write-back to the issue in v0 beyond linking; no Jira.

## S1-12 · `h9kd install` — launchd + installed mode (dispatched via Hall9k)

- **Objective:** The daemon survives reboots as a per-user launchd agent using compose-managed Postgres.
- **Type:** feature
- **Context:** decision log #3, #10; PLAN.md §6.1, §13 distribution tier 1.
- **Acceptance criteria:**
  1. `h9kd install` writes and loads a LaunchAgent plist (per-user); `h9kd uninstall` removes it;
     `h9kd run` stays the foreground/dev mode.
  2. Installed daemon uses docker-compose Postgres; boot race handled by existing retry/backoff.
  3. Owner + Node registered on first install (§6.2); reinstall is idempotent.
- **Constraints:** macOS only; installer behind the cross-platform seam for the Windows task to fill.

## S1-13 · Orchestrator CLAUDE.md + repo CLAUDE.md (dispatched via Hall9k)

- **Objective:** An interactive Claude Code session in this repo knows how to be the orchestrator
  window, and any contributor session knows how to build/test/run.
- **Type:** chore
- **Context:** PLAN.md §2 (orchestrator window), §12; the working agreements in V0-KICKOFF-PROMPT.md.
- **Acceptance criteria:**
  1. Repo CLAUDE.md: build/test/run commands, AppHost dev loop, house style pointers (TASK-MODEL.md §8).
  2. Orchestrator section (or linked skill): the role ("window, not alarm"), the `h9k` command
     surface with examples, the ask/answer relay flow, and the rule that all new work is added via
     `h9k task add` (the flip is policy now).
  3. Verified by use: a fresh interactive session can, from the doc alone, add a task and report status.
  4. Every existing CLI command gains `.WithExample(...)` usage examples in its help output,
     per the CLI command standards section of AGENTS.md (the standard itself is already law;
     this task backfills the examples).
- **Constraints:** keep it lean — commands and role, not philosophy; PLAN.md remains the vision doc.

## S1-14 · Windows support (dispatched via Hall9k — first post-flip feature)

- **Objective:** `IProcessManager` + installer implemented for Windows; the pipeline runs on the tower.
- **Type:** feature
- **Context:** decision log #3 (Task Scheduler logon task, kill-tree semantics); CI already builds Windows.
- **Acceptance criteria:** spawn/kill-tree/reattach parity tests green on Windows; `h9kd install`
  registers a logon task; one real task runs end-to-end to a PR on the Windows machine.
- **Constraints:** no Windows-service mode; Parallels for iteration, tower for validation.

---

## Deferred beyond Slice 1 (explicitly)

- **Slice 2** (ask/answer): exit-and-resume loop — **preceded by the resume spike** (log #5).
- **Slice 3**: reviewer agent.
- Budget auto-kill enforcement (log #11) — lands with Slice 2's run-monitor maturity.
- `h9k watch [--notify]`, `h9k idea add`, funnel, Jira, multi-node.
