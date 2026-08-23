# Operations

Running Hall9k: the daemon's lifecycle, where things live, what to configure, what `needs you`
means, and how to get out of trouble.

- [The daemon lifecycle](#the-daemon-lifecycle)
- [Postgres](#postgres)
- [What lands on disk](#what-lands-on-disk)
- [Configuration](#configuration)
- [What "needs you" means](#what-needs-you-means)
- [The recovery levers](#the-recovery-levers)
- [Reading a run](#reading-a-run)
- [Known operational gaps](#known-operational-gaps)

---

## The daemon lifecycle

The daemon's lifecycle belongs to the CLI. `h9k install` registers no background service and no
login item; start-at-login is a separate opt-in that nothing else implies.

```bash
h9k daemon start      # launches h9kd detached from this terminal; survives shell exit
h9k daemon stop       # graceful: in-flight event appends finish
h9k daemon status     # running or not, pid, uptime, autostart posture, recent log lines
```

**A stopped daemon costs latency, never correctness.** Commands still land in Postgres while it
is down, and startup catches up on everything that happened meanwhile, in a fixed order: adopt
live runs, sweep expired leases, then claim new work. It says what it caught up on, both to the
terminal and in the log:

```
Catch-up complete — adopted 1 run(s), failed 0 orphaned run(s), requeued 0 expired
lease(s); closeout sweep inspected 0 pull request(s) and observed 0 merge(s)
```

**Stopping the daemon does not stop the agents.** They are detached processes by design, so they
keep working and the next start adopts them. `h9k daemon stop` says so.

Start-at-login is macOS-only today, as a launchd LaunchAgent:

```bash
h9k daemon autostart enable
h9k daemon autostart disable
```

`enable` snapshots the enabling shell's `PATH` (plus any `HALL9K_*` variables that are actually
set) into the registration, because launchd starts from its own minimal environment and the
daemon resolves `claude`, `gh`, and `git` through `PATH`. It reports any of those three that the
captured environment cannot resolve, at enable time, where you can still fix it. Move a tool
afterwards and you re-run `enable`. Only variables that are genuinely set are recorded; an unset
one stays unset rather than being filled in with a plausible default.

A launchd-owned daemon restarts after a crash but never after a clean stop, and
`h9k daemon stop` goes through `launchctl` when autostart owns the job, so stopped means stopped.

## Postgres

Hall9k requires a Postgres connection string and takes no position on where Postgres runs. Two
provisioning paths exist and are deliberately separate today:

- **Installed mode**: `docker compose up -d` brings up `postgres:18` on `localhost:5432` with the
  database, user, and password the default connection string expects.
- **The dev loop**: `dotnet run --project src/Hall9k.AppHost` brings up its own Postgres container
  alongside the daemon and the Aspire dashboard.

They bind the same port, so run one at a time.

If Postgres is unreachable, the CLI says exactly that and names the fix rather than surfacing a
driver exception.

## What lands on disk

Everything hangs off `~/.hall9k` (or `HALL9K_HOME`):

```
~/.hall9k/
├── bin/                        h9k and h9kd release binaries (h9k install)
├── credentials/                file-kind secrets, one file per credential
├── h9kd.log                    the daemon log; h9k daemon status tails it
├── h9kd.pid, h9kd.lock         the local liveness probe
├── ideas/<idea-id>/workspace/  discovery workspaces: research, files, prototypes
└── runs/<run-id>/
    ├── prompt.md               what the session was actually given
    ├── settings.json           the settings override the spawn used
    ├── stream.jsonl            the agent's stream-json, as written
    ├── stderr.log
    ├── review-<lens>-<cycle>-<session>.stream.jsonl
    ├── review-<cycle>-<lens>-findings.md    one per lens, exactly as that pass wrote it
    ├── review-<cycle>-findings.md           the merged document a fix session reads
    ├── review-<cycle>-fix-position.md       the fix session's closing summary
    ├── review-thread-dispute.md             a follow-up's position on a disputed review thread
    ├── blocker-context.md      what the immediate blockers handed down
    └── handoff.md              what this run hands down in turn
```

`credentials/` is the one directory here that holds a secret. A registered connection records a
*reference* rather than a value, and a `file:` reference names a file in that directory, which is
where `h9k connection add` puts a token you supply at the prompt or with `--token` (an `env:` or
`keychain:` reference keeps the secret out of `~/.hall9k` entirely). Nothing else on the platform
reads or writes it, so it is what you carry to a new machine along with the rest, and it is what
you exclude from anything you would not put a token in.

Transcripts are artifacts, not events. Event streams carry milestones only, and the bulky
material lives on disk and is referenced from the stream. When a review parks, the findings and
the fix session's counter-position are both on disk, and the park points at them.

Worktrees are not under `~/.hall9k`. Each one is created as a **sibling of the project's
registered repository path**, named `wt-<task>-<run>` from the short forms of the two ids, and it
lives until the pull request completes, because a parked run's worktree is the human's workspace.
An observed merge removes it and deletes the branch locally, on the remote, and in
remote-tracking refs.

## Configuration

### Environment

| Variable | Effect |
|---|---|
| `HALL9K_CONNECTION_STRING` | The Postgres connection string. Default: `Host=localhost;Port=5432;Database=hall9k;Username=postgres;Password=hall9k` |
| `HALL9K_HOME` | Relocates the whole on-disk layout away from `~/.hall9k` |
| `HALL9K_CLAUDE_PATH` | Pins the `claude` binary instead of resolving it through `PATH` |

Daemon options bind from configuration in the usual .NET way, so each is also an environment
variable under the `Hall9k__` prefix. The ones worth knowing:

| Option | Default | What it governs |
|---|---|---|
| `Hall9k__MaxConcurrentAgentSessions` | 3 | The node's session ceiling, counted in **agent sessions**, not runs. See the conversion below: at the default of 3, one run dispatches at a time. |
| `Hall9k__LeaseTimeout` | 60s | How long a lease survives without a heartbeat before the sweep requeues it |
| `Hall9k__VerifyGateTimeout` | 15m | Per gate |
| `Hall9k__PullRequestPollInterval` | 3m | How often the closeout monitor polls an open pull request |
| `Hall9k__MaxAutomaticCloseoutRuns` | 2 | Automatic follow-ups before closeout parks and asks for you |
| `Hall9k__MaxComplianceReviewCycles` | 3 | The conformance track's cap |
| `Hall9k__MaxAdversarialReviewCycles` | 10 | The adversarial track's cap |
| `Hall9k__AdversarialSeverityGateFromCycle` | 4 | From this cycle, only a `high` re-triggers the adversarial loop |
| `Hall9k__DefaultReviewRerequest` | disabled | Whether closeout asks reviewers for another pass after fixes push |
| `Hall9k__DefaultModel`, `Hall9k__ModelByRole__*` | | The node's model policy, per role (build, review, fix, synthesis, refinement, publication) |

The ceiling is set in sessions and spent in runs, so there is a conversion between the number you
configure and the number of tasks in flight. A run tree's peak is however many review lenses a
cycle dispatches together, which is two today (conformance and adversarial), and every live run is
charged that peak the whole time rather than what it happens to be holding this second, because a
run that is building today reaches its review cycle on this same machine. So the runs that may
start at once are the session ceiling divided by two, floored at one:

| `Hall9k__MaxConcurrentAgentSessions` | Runs in flight |
|---|---|
| 3 (the default) | 1 |
| 4 | 2 |
| 6 | 3 |

That is why a board with the shipped default says `waiting for a slot — 1 of 1 running`, and why
raising the ceiling from 3 to 4 buys a second run rather than a fourth. The floor of one is
deliberate: a budget smaller than one run's peak would dispatch nothing at all, and a node that
silently does no work is the worse answer. Appending a third review lens would tighten this on its
own, because the divisor is read off the lens list rather than written down.

A queued row names the concurrency ceiling as its reason only when the dispatcher recorded a
current measurement saying this node is full. With none, it says it is ready and stops, because a
queue that is not moving has many causes and a stopped daemon is the commonest.

### Per project and per owner

Settings resolve most-specific-wins, and each chain always ends somewhere explicit. The review
re-request policy resolves **project over owner over the node default**. The agent model resolves
**task override, then this node's per-role default, then the project default, then the platform
fallback**, and the resolved value is recorded on the dispatch event as an observed fact of the
run.

```bash
h9k project set myproject --verify "build=dotnet build" --verify "test=dotnet test"
h9k project set myproject --model claude-opus-5
h9k project set myproject --commit-style narrative
h9k project set myproject --max-parallel 2
h9k project set myproject --skip-permissions true
h9k project set myproject --link "api-conventions=https://…"
h9k project set myproject --jira PROJ
h9k project set myproject --rerequest-review on

h9k owner set --rerequest-review on
```

`h9k project show <name>` prints every setting a project runs by, alongside how it is registered.
Ask `h9k project set --help` for the current list and what each value means.

## What "needs you" means

The **needs-you** section of `h9k status` is the whole point of the pane. What identifies a row
in it is the **cause line underneath**, not the Status column: that column keeps the lifecycle
word (a run that stopped before pushing still reads `Working`, one that stopped with its pull
request open reads `Delivered`, work nothing has claimed reads `Published`, a failed run reads
`Failed`). There is no needs-a-human word in the seven, and `--state NeedsHuman` is refused, so
read the line beneath the row rather than grepping the column for a state.

Each cause is quoted from a record rather than inferred, and each carries the lever that clears
it:

| The cause line says | What happened | The lever |
|---|---|---|
| the pre-PR review loop's own park reason | The loop spent its automatic fixes, or a fix session disputed a finding | `h9k review resolve` |
| closeout's own park reason | The closeout monitor spent its follow-up budget on an open pull request | `h9k pr resolve` |
| the recorded dependency failure | A blocker died, so the dependent stays `Blocked` rather than silently unblocking | recover the blocker, as the recorded reason names |
| the agent asked a question and stopped | A run recorded a question and exited. `h9k ask` and `h9k answer` are Slice 2, so no command answers it | `h9k task show`, then decide it by hand |
| why the run failed, composed from what was recorded | The run itself failed | `h9k task retry` / `resolve` / `abandon` |
| the pull request is open and the task is unassigned | A follow-up reopened the task and it was then unassigned, so nothing will claim it | `h9k task assign` |
| no run record is watching it for a merge | The pull request is open and no run is left to observe the merge | the pull request itself |
| the run ended without a merge being observed | The run that owned an open pull request failed, or that pull request was closed unmerged | `h9k pr resolve`, or the pull request when it was closed unmerged |

That is what the composer can say today, not a closed set: every cause is read off a record, so a
new record adds a line here. Read the cause, then `h9k task show <id>`, then `h9k logs <id>` if
the cause is not already enough. These rows exist precisely because the platform refused to guess,
so answering them is a decision, not a formality.

Two things the pane deliberately keeps out of this section. A **stalled** row (live work whose
stream has gone silent, or whose recorded process is gone) gets its own section directly below,
because a silence is a different question from a park. And a `Delivered` row whose only
outstanding ask is the merge itself stays under Delivered: its attention column says it wants
you, but moving every open pull request into needs-you would empty the group a reader scans for
exactly that.

There is one more thing the pane can say, and it is not needs-you either: **waiting but handled**.
A task whose blockers are all still alive, or a pull request the closeout monitor still owns the
next move on, renders dim as its own level. It is there so you can consciously ignore it.

## The recovery levers

Five levers. Picking the wrong one loses work, and the question that separates them is *what
actually failed*.

| Lever | Use it when | What it does |
|---|---|---|
| `h9k task retry <id>` | The task is **Failed** and the machinery is what failed (a daemon bug, a dead process, a rejected push). The work has to run again. | Requeues the task. The failure stays on the stream. The new run resumes the failed run's branch when it survived, or starts clean from the base branch when the artifacts are gone. |
| `h9k task resolve <id> --reason "…"` | The task is **Failed** but the objective was met anyway: the work merged, or you finished it by hand, and only the bookkeeping died. | Ends the task Done on your attestation. `--reason` is required, because an attestation without a why is a guess. `--pr` records where the work landed. |
| `h9k task abandon <id> --reason "…"` | You have stopped believing in the work. Reaches every non-terminal state, drafts and published tasks included. | Terminal. Releases any lease. Nothing is deleted: the reason is the record. |
| `h9k pr resolve <id> [--checks]` | The row reads **Delivered**, which is a pull request open with the merge not yet observed, and review feedback or failing CI needs another pass. | Dispatches a follow-up run onto the existing branch and resets the monitor's automatic retry budget. |
| `h9k review resolve <id> --merge-ready` / `--needs-fixes "<why>"` | A run parked **before** its pull request, in the internal review loop, waiting on your verdict. | `--merge-ready` proceeds to the pull request. `--needs-fixes` dispatches a fix session with your reason as its findings and restores the fix budget. |

Two distinctions get confused, so they are worth stating flatly:

- **`review resolve` is pre-PR; `pr resolve` is post-PR.** No pull request yet means the park is
  the internal reviewer's. A pull request means it is closeout's.
- **`task retry` re-runs the work; `task resolve` declares it already done.** Retry when the
  objective is unmet, resolve when it is met and the run merely failed to say so. Retrying
  finished work rebuilds it; resolving unfinished work loses it.

All three exits from `Failed` are human-only on purpose, and exactly one of them closes it.

## Reading a run

```bash
h9k task show <id>              # every run on the task, with outcomes and pull requests
h9k logs <id>                   # the newest run's transcript, rendered
h9k logs <id> --raw             # the stream-json itself
h9k logs <id> --run <run-id>    # an earlier run
tail -f ~/.hall9k/h9kd.log      # what the daemon is doing
```

For a parked review, the artifacts on disk are the argument itself: `review-<cycle>-findings.md`
is what the fix session was given, and `review-<cycle>-fix-position.md` is what it said back.
Read both before resolving, because a dispute park exists precisely because two reasonable
positions disagreed.

## Known operational gaps

Recorded honestly rather than left to be discovered:

- **`h9k status` probes this machine's pid file.** Its leading red line answers "is a daemon
  alive here", not "is a daemon serving this database". Point the CLI at a second database with
  `HALL9K_CONNECTION_STRING` while a daemon runs against the first, and the pane reads healthy
  while nothing will ever claim the queue. Found by the S1-13 verification session, 2026-08-22.
  On a default install there is one database and one daemon, and the line is exact.
- **Two Postgres provisioning paths coexist** (the Aspire AppHost's and `docker-compose.yml`) and
  nothing reconciles them. Whether a doctor check unifies them is open (PLAN.md §15, row 28).
- **Token exhaustion is not yet a distinct failure shape.** When the subscription window runs dry
  mid-flight, sessions die with a generic error and the board shows the text the machinery wrote
  rather than a category nobody observed. `backlog/40-token-exhaustion.md` is the fix.
- **There is no `h9k watch --notify`.** Nothing pushes a desktop notification, and no interactive
  session should promise to tell you when something finishes. `h9k status` is a window you look
  through, not an alarm.
