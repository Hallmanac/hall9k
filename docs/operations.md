# Operations

Running Hall9k: the daemon's lifecycle, where things live, what to configure, what `needs you`
means, and how to get out of trouble.

- [Installing on a bare machine](#installing-on-a-bare-machine)
- [The daemon lifecycle](#the-daemon-lifecycle)
- [Postgres](#postgres)
- [What lands on disk](#what-lands-on-disk)
- [Configuration](#configuration)
- [What "needs you" means](#what-needs-you-means)
- [Working a task interactively](#working-a-task-interactively)
- [A deliberate human kick-off](#a-deliberate-human-kick-off)
- [The recovery levers](#the-recovery-levers)
- [Reading a run](#reading-a-run)
- [Known operational gaps](#known-operational-gaps)

---

## Installing on a bare machine

A tagged commit on `main` becomes a GitHub release carrying `h9k` and `h9kd` binaries for macOS
arm64, Windows x64, and Linux x64 (`.github/workflows/release.yml`, backlog 42) — built alongside
`ci.yml` rather than replacing it, and only on a version tag. A machine with no repo checkout and
no .NET SDK bootstraps from that release directly; see [docs/INSTALL.md](INSTALL.md) for the full
walkthrough (it is written to be followed by an AI agent as much as by a human) and the
[README's Install section](../README.md#install) for the one-liners.

The mechanism, in short:

- The bootstrap scripts (`scripts/install.sh`, `scripts/install.ps1`) fetch the latest release
  for the current platform via `gh`, **verify its checksum** against the release's own
  `checksums.txt`, **ask consent**, unpack it, and run the release's own
  `h9k install --from-release <payload>` — the same idempotent publish-and-refresh
  `h9k install` has always done (Decisions Log #31), just fed from a downloaded, checksum-verified
  archive instead of a local `dotnet publish`. `--from-release` also carries the canonical skill
  set bundled into the release payload, published to `~/.hall9k/skills` the same way `--repo`
  publishes from a checkout's `.claude/skills`.
- The scripts finish by running `h9k doctor`, so a fresh machine ends setup knowing what still
  needs attention (almost always: a Postgres connection string — see [Postgres](#postgres) below)
  rather than declaring victory silently.
- **`h9k update`** is the one-command path for a machine that already has `h9k`: it fetches the
  latest release for the platform via `gh`, verifies the checksum, republishes binaries and the
  skill set through the same `--from-release` finish, and offers to restart a running daemon —
  no repo checkout, no .NET SDK, on the machine that runs it.
- Installing this way registers no background service and no autostart, exactly as a local
  `h9k install` does (Decisions Log #31, S1-12).

**Platform note.** Release binaries are self-contained (no .NET runtime needed on the target
machine) for all three platforms. The daemon lifecycle (`h9k daemon start|stop|status`) runs on
macOS, Windows, and Linux; `h9k daemon autostart enable|disable` runs on macOS and Windows (a
Linux systemd user unit is unbuilt) — see [The daemon lifecycle](#the-daemon-lifecycle) below for
what each platform's mechanism is.

## The daemon lifecycle

The daemon's lifecycle belongs to the CLI. `h9k install` (or, on a machine fed by a release
instead of a repo checkout, `h9k update` — see [Installing on a bare machine](#installing-on-a-bare-machine))
registers no background service and no login item; start-at-login is a separate opt-in that
nothing else implies.

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

On Windows, `stop` has no SIGTERM to send an arbitrary process, so it asks gracefully instead: it
writes a small stop-request file the running `h9kd` polls for and honors itself
(`WindowsStopRequestWatcher`) — same effect, in-flight event appends finish either way, just a
different way of asking.

Start-at-login works on macOS (a launchd LaunchAgent) and Windows (a Task Scheduler logon task):

```bash
h9k daemon autostart enable
h9k daemon autostart disable
```

`enable` snapshots the enabling shell's `PATH` (plus any `HALL9K_*` variables that are actually
set) into the registration, because the service manager starts from its own minimal environment
(or, on Windows, from none of its own at all) and the daemon resolves `claude`, `gh`, and `git`
through `PATH`. It reports any of those three that the captured environment cannot resolve, at
enable time, where you can still fix it. Move a tool afterwards and you re-run `enable`. Only
variables that are genuinely set are recorded; an unset one stays unset rather than being filled
in with a plausible default — **except `HALL9K_CONNECTION_STRING` on Windows**, which is
deliberately never carried into the registration even when the enabling shell has it set: unlike
`PATH` or `HALL9K_CLAUDE_PATH`, the connection string already has a durable home once an install
reaches this point (the platform config file, which `h9k install` or `h9k doctor`'s start-offer
writes to when nothing was configured yet), and embedding it in the launch script would only add
a second, weaker plaintext copy on disk. `enable` warns at the time you'd still be able to fix it
whenever the shell has the variable set but the platform config file's own value does not
currently answer — whether because `config.json` names no connection string at all, or because it
names one that just isn't reachable right now. The two cases differ in whether `h9k doctor` can
help. When `config.json` has no connection string of its own, doctor will not fix this for you:
your shell already resolves a connection string from the variable, so doctor reports healthy and
never touches `config.json` — add `{"connectionString": "…"}` to `~/.hall9k/config.json` by hand
instead. When `config.json` does name a connection string but it isn't answering right now,
whether `h9k doctor --yes` helps depends on what the variable names: doctor probes the variable's
target, not `config.json`'s, so it fixes this only when the two happen to name the same
unreachable Postgres — naming something else leaves `config.json` untouched either way, and the
remedy is the same hand-edit: bring up whatever `config.json` names, or point it at a Postgres
that already answers. On Windows the captured
variables travel as `set` prefixes scoped to the one `cmd.exe` invocation that then runs `h9kd`,
never as a registry mutation — nothing outside that one task is touched.

A launchd- or Task-Scheduler-owned daemon restarts after a crash but never after a clean stop
(`RestartOnFailure` on Windows mirrors launchd's `KeepAlive SuccessfulExit=false`), so stopped
means stopped either way — but the two platforms get there differently. On macOS, `h9k daemon
stop` routes through launchd itself (`launchctl bootout`), unloading the job so its restart
policy cannot resurrect the daemon you just killed. Task Scheduler has no equivalent per-job stop
verb, so on Windows `h9k daemon stop` sends the same graceful stop-request file the non-autostart
path always uses; the task registration itself is untouched and stays `Ready` until the next
logon. Windows registers the task at `\Hall9k\h9kd` in Task Scheduler's own library — never a
Windows service, which would run as a different identity and lose your Claude Code, git, and `gh`
credentials (Decisions Log #3).

## Postgres

Hall9k requires a Postgres connection string and takes no position on where Postgres runs
(Decisions Log #57). Nothing is *started* at install time — no prompt, no provisioning — and
`h9k doctor` is what teaches the fix and offers to run it, at the moment you can act on it
(Decisions Log #58). The one thing install does write is the connection string itself, and only
when nothing resolves yet anywhere in the precedence chain below: the compose file it just wrote
fully determines what that string has to be, so recording it is not a guess, and it is never
written over a value that already resolves (Decisions Log #118). It also skips the write when
something is already listening on `localhost:5432`, because a native Postgres of your own is a
supported deployment, and writing install's compose credentials against it would turn doctor's
honest "something is already listening" diagnosis into a manufactured authentication failure. You
get a line saying the machine was left unconfigured for that reason; run `h9k doctor` to see what
is listening, or set `HALL9K_CONNECTION_STRING` (or `~/.hall9k/config.json`) to your own server
yourself.

### The doctor check

```bash
h9k doctor
```

Runs automatically, too: the first command that needs a database and cannot reach one runs this
check instead of failing raw, and `h9k daemon start` runs it before spawning the daemon process at
all. Four questions, answered in order, stopping at the first one that fails (Decisions Log #73,
#74):

1. **Is a connection string configured at all?** If not, that is the entire answer.
2. **Is it reachable?** "Nothing is listening" and "reached it, credentials rejected" are named
   separately — completely different fixes.
3. **Is the schema there?** Marten creates its own tables on first use, so this is mostly an
   offer: *shall I set that up now?*
4. **Only if nothing was configured** — what is available: a running container runtime, a native
   Postgres already on 5432, and, the nicest possible finding, a **stopped** `hall9k-postgres`
   container from a previous session ("your database exists, it is just not running").

When the check finds Postgres not running but a container runtime available, it offers to start
it — offer-never-force, the same shape as the auto-assign prompt at publish — and offers to
create the schema the same way once it can reach a server that does not have one yet. A
non-interactive invocation (a script, a dispatched agent) with nobody there to answer names the
skipped prompt and the flag that would have answered it, `h9k doctor --yes`, rather than falling
through to generic advice. Pass `--yes` for exactly that: it starts Hall9k's own Postgres via the
generated compose file and creates the schema without asking, so a fresh release install reaches
a passing doctor in one command after the installer (`h9k doctor --yes`, then a plain
`h9k daemon start`). The boundary is Docker itself: if the container runtime is not running, the
check names that and stops even with `--yes` — starting Docker Desktop is a machine-level action
and always yours.

The check runs a raw Npgsql connection attempt, never a Wolverine host or a Marten codegen pass,
so it survives the thin-CLI rule even run before every database-touching command. It lives in the
CLI rather than the daemon for exactly that reason — it has to work while the daemon is down.

### Where the connection string lives

Precedence, highest first (Decisions Log #73):

1. `HALL9K_CONNECTION_STRING` — this shell, this invocation.
2. The platform config file, `~/.hall9k/config.json` (`{"connectionString": "…"}`) — a durable
   per-machine setting, written by hand, by `h9k install` when nothing was configured yet
   (recording the connection string that matches the compose file it just wrote), or by the
   doctor's own start-offer.
3. A per-project override file, `.hall9k-connection` (one line, the connection string alone) —
   found by walking up from the working directory the same way `h9k install` finds `Hall9k.slnx`.
   Checked **last**, deliberately: it is the one entry in this chain that can arrive already
   sitting in a repository checkout, and a file nobody meant to commit should never silently
   outrank a connection string you configured on purpose. Keep it out of version control the same
   way you would a `.env` file.

Remote Postgres is supported through any of the three homes above but never suggested — a
database in someone else's cloud quietly forfeits the local-first, works-offline promise
(Decisions Log #57).

### Provisioning: two paths, deliberately separate

- **Installed mode**: `h9k install` writes Hall9k's own Postgres definition to
  `~/.hall9k/postgres/docker-compose.yml` on every publish-and-refresh (a local edit there is lost
  on the next install — it is not a customization surface). Nothing is started at install time;
  `h9k doctor` or `h9k daemon start` bring it up the first time it is actually needed, on your
  confirmation. Run it by hand any time with `docker compose -f ~/.hall9k/postgres/docker-compose.yml up -d`.
  **If your installed Postgres predates this branch's `name:` pin**, Compose had been prefixing
  the volume with its own notion of the project name instead of the literal `hall9k-pgdata` —
  typically `postgres_hall9k-pgdata` for `~/.hall9k/postgres/docker-compose.yml` (Compose derives
  the prefix from the compose file's directory, `postgres`). Republishing the compose file (via
  `h9k update` or a fresh `h9k install`) does not migrate that data forward: the pinned name
  points at a different, empty volume, so the next `docker compose up` recreates the container
  against nothing and the board looks wiped even though the old volume is untouched. The same
  applies to a contributor's own checkout-rooted `docker compose up -d` against the repository's
  `docker-compose.yml`, whose pre-pin volume is `<checkout-dirname>_hall9k-pgdata`. Confirm the
  old volume is still there with `docker volume inspect postgres_hall9k-pgdata` (or the checkout
  variant), then either rename it forward (`docker run --rm -v postgres_hall9k-pgdata:/from -v
  hall9k-pgdata:/to alpine sh -c 'cp -a /from/. /to/'`) or point the compose file at the old name
  for one run to read it back out.
- **The dev loop**: `dotnet run --project src/Hall9k.AppHost` brings up its own Postgres container
  alongside the daemon and the Aspire dashboard, injecting the connection string directly rather
  than through any of the three homes above. Its data volume is named `hall9k-dev-pgdata`,
  deliberately distinct from the installed-mode volume's `hall9k-pgdata` (Decisions Log #83).
  Between that distinct naming and `h9k uninstall --purge-data` refusing to touch a
  `hall9k-postgres`-shaped volume it cannot confirm by inspecting a live `hall9k-postgres`
  container, purge-data cannot reach into the dev loop's own database — **once your dev loop has
  migrated to this split**. **If your dev loop predates this split**, its data is sitting in a
  volume still named `hall9k-pgdata` — the same literal `h9k install`'s compose file now pins as
  *its own* volume name. The next `dotnet run --project src/Hall9k.AppHost` will not find it,
  mount a fresh empty `hall9k-dev-pgdata` instead, and the board will look wiped even though
  nothing was deleted; **but if installed mode's `h9k doctor` or `h9k daemon start` runs first**,
  its bring-up sees that same `hall9k-pgdata` volume already sitting there, treats it as its own
  (§Provisioning's migration check exists precisely for the case where the pinned name already
  exists), and mounts the dev loop's real data straight into `hall9k-postgres` — after which
  `h9k uninstall --purge-data` inspects that live container, finds it genuinely mounts
  `hall9k-pgdata`, and destroys the dev loop's database exactly as designed for data that really
  is the install's own. The one-time migration below is not optional cleanup; do it (or confirm
  you never ran the pre-split dev loop) before ever bringing up installed-mode Postgres on this
  machine. Reconnect to the old data with `docker volume inspect hall9k-pgdata` to confirm it is
  still there, then either rename it (Docker has no rename; `docker run --rm -v
  hall9k-pgdata:/from -v hall9k-dev-pgdata:/to alpine sh -c 'cp -a /from/. /to/'` copies it
  forward) or point AppHost at the old name for one run to read it back out.

They bind the same port (`localhost:5432`), so run one at a time (Decisions Log #74; §15 row 28).

## What lands on disk

Everything hangs off `~/.hall9k` (or `HALL9K_HOME`):

```
~/.hall9k/
├── bin/                        h9k and h9kd release binaries (h9k install)
├── config.json                 the platform config file: connectionString (h9k doctor, §Postgres)
│                               and the "hall9k" section (h9k config set/show, §Configuration)
├── credentials/                file-kind secrets, one file per credential
├── h9kd.log                    the daemon log; h9k daemon status tails it
├── h9kd.pid, h9kd.lock         the local liveness probe
├── skills/                     the canonical skill set, published by h9k install
├── projects/<name>/            a project's home, unless the project records another location
│   ├── AGENTS.md               rendered from the project's facts; never hand-maintained
│   ├── repo/                   <name>.git (bare) · dev/ (primary branch) · wt-*/ (dispatch)
│   ├── ideas/<id>-<slug>/      idea.md (rendered) + workspace/, when captured with this home (below)
│   ├── tasks/<id>-<slug>/      task.md (rendered) + workspace/ for refinement material, plus:
│   │   └── runs/<run-id>/      every run this task ever dispatched — see the run shape below
│   ├── tasks/_archive/<id>-<slug>/  the same shape, moved here once the task is terminal (below)
│   ├── skills/                 symlinks into ~/.hall9k/skills, plus this project's own
│   └── .claude/skills/         symlinks into the line above: the Claude Code adapter
├── ideas/<idea-id>/workspace/  the fallback for an idea captured with no project, or a project
│                               with no home yet: permanent, never relocated after capture
├── postgres/docker-compose.yml Hall9k's own Postgres definition (h9k install writes it, §Postgres)
├── skills/<skill-name>/        the canonical skill set (h9k install / h9k update publish it)
└── runs/<run-id>/              the fallback for a run dispatched when its task's project had no
                                 home, and where every run's artifacts lived before backlog 49:
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

A task's directory moves into `tasks/_archive/` the moment it is terminal — true closeout (its
pull request merged and the closeout monitor observed it, not merely `Done` with a pull request
still under review) or abandoned — and moves back out once it is reopened and its current run is
no longer live, so a browse of `tasks/` in an editor shows only the drafts, published, and
in-flight work that still needs attention. The directory itself is unchanged by the move:
`task.md`, `workspace/`, and every `runs/<run-id>/` travel with it. The render sweep owns this,
the same sweep that keeps `task.md` current, so it happens without a human ever touching the
filesystem by hand. The liveness check on the way back out is deliberate, not an oversight: a
reopened task's follow-up run can already be writing into its directory (RunLauncher's
alternate-root search dispatches straight into `tasks/_archive/` when that is where the directory
still sits), so moving it back out from under an active writer would race that run. The sweep
defers the move until the run leaves its actively-running states, and every daemon-side reader
resolves the run's current directory dynamically rather than trusting the path `RunDispatched`
recorded, so a still-parked run keeps finding its own files once a later sweep does move it back.

A run's directory is resolved once, at dispatch, from its owning task's directory name at that
moment, and recorded on the run — the same discipline as the worktree path. That record is a
dispatch-time snapshot rather than a live pointer, though: the task's directory can move into
`tasks/_archive/` and back afterward, so every reader resolves the run's current location
through `RunPaths.ResolveCurrentDirectory` rather than trusting the recorded path verbatim, which
is what lets `h9k logs` keep working on a task the render sweep has since archived. An idea's
workspace location is resolved once too, but at capture instead: a
project gaining a home after an idea already exists there does not retroactively move that
idea's workspace, so two ideas under the same project can legitimately have their workspaces in
different places depending on when each was captured.

`credentials/` is the one directory here that holds a secret. A registered connection records a
*reference* rather than a value, and a `file:` reference names a file in that directory, which is
where `h9k connection add` puts a token you supply at the prompt or with `--token` (an `env:` or
`keychain:` reference keeps the secret out of `~/.hall9k` entirely). Nothing else on the platform
reads or writes it, so it is what you carry to a new machine along with the rest, and it is what
you exclude from anything you would not put a token in.

Transcripts are artifacts, not events. Event streams carry milestones only, and the bulky
material lives on disk and is referenced from the stream. When a review parks, the findings and
the fix session's counter-position are both on disk, and the park points at them.

A project's home is created by `h9k project add`, or by `h9k project init` for a project that
does not have one yet. Both are platform code end to end with no agent in them, and every step is
idempotent, so re-running either reports what was already there instead of starting over. The
location is a project setting (`--home`, or `h9k project set <name> --home <path>`); the shape
inside it is fixed, which is what lets a project look the same on every machine.

`skills/` in a project home is symlinks rather than copies on purpose: re-running `h9k install`
republishes `~/.hall9k/skills` and every project is already on the new content. A skill that is
specific to one project is an ordinary directory beside the links, and re-seeding never touches
it. A skill added to the install after a home was created reaches that home at its next
`h9k project init`. Republishing retires the skills the previous install published and this one
no longer ships, naming each one as it goes, and it leaves anything you wrote into
`~/.hall9k/skills` yourself alone: only what an install published is an install's to remove, and
only what an install published is an install's to overwrite. Write a skill of your own under a
name the platform also ships and yours is kept, shadowing the platform's, and the install says so
by name; delete yours and the next `h9k install` puts the platform's version back.

One home belongs to one project. The default location is derived from the project name through a
slug that lowercases it and collapses everything non-alphanumeric to dashes, so `My App` and
`my-app` resolve to the same directory, and registering the second is refused rather than allowed
to overwrite the first's bare clone and generated `AGENTS.md`. The same refusal covers the other
way a home gets recorded, `h9k project set <name> --home <path>` and `--repo <path>`. Pass
`--home <path>` to give it one of its own.

Worktrees are created as a **sibling of the project's registered repository path**, which for a
project with a home puts them under `<home>/repo/`. Each is named `wt-<task>-<run>` from the short
forms of the two ids, and it lives until the pull request completes, because a parked run's
worktree is the human's workspace. An observed merge removes it and deletes the branch locally, on
the remote, and in remote-tracking refs.

## Configuration

### Environment

| Variable | Effect |
|---|---|
| `HALL9K_CONNECTION_STRING` | The Postgres connection string — highest-precedence of the three homes in [§Postgres](#postgres); unset by default, and `h9k doctor` is what teaches the fix rather than a silent guess |
| `HALL9K_HOME` | Relocates the whole on-disk layout away from `~/.hall9k` |
| `HALL9K_CLAUDE_PATH` | Pins the `claude` binary instead of resolving it through `PATH` |

Daemon options bind from configuration in the usual .NET way, so each is also an environment
variable under the `Hall9k__` prefix — and, for the settings worth tuning per machine, from the
platform config file too (see [Daemon operating settings](#daemon-operating-settings) below). The
ones worth knowing:

| Option | Default | What it governs |
|---|---|---|
| `Hall9k__MaxConcurrentTaskRuns` | 1 | The node's ceiling, counted directly in **task runs** (Decisions Log #111) — every value is meaningful. Retires `Hall9k__MaxConcurrentAgentSessions`, still read as a fallback (see below). |
| `Hall9k__SessionCapPerRun` | 3 | How many agent sessions one run may hold simultaneously — a global default, overridable per task at any time with `h9k task set-session-cap`, even mid-run. A cap of 1 serializes the two review lenses instead of dispatching them together. |
| `Hall9k__LeaseTimeout` | 60s | How long a lease survives without a heartbeat before the sweep requeues it |
| `Hall9k__VerifyGateTimeout` | 15m | Per gate, and the same value sizes every headless dispatched session's own foreground command timeout (`ClaudeSettingsFile`), so raising this also raises how long a session's own `dotnet test`-shaped command may run before Claude Code's Bash tool would otherwise kill it. Three surfaces do not move with it: an interactive `h9k task work` claim, `h9k task verify` run against one, and `h9k task start` — all three are CLI commands, and nothing on the CLI side reads this option today, so each always runs its own gates against the fixed 15-minute default regardless of what this is set to. |
| `Hall9k__PullRequestPollInterval` | 3m | How often the closeout monitor polls an open pull request |
| `Hall9k__PullRequestPollBackoffMaxInterval` | 30m | The ceiling the poll interval backs off to when every attempted inspection in a sweep fails (e.g. `gh` rate-limited); resets on the next successful sweep |
| `Hall9k__MaxAutomaticCloseoutRuns` | 6 | The lifetime ceiling: automatic closeout actions a pull request may spend across every obstruction before closeout parks and asks for you, whatever it grants along the way |
| `Hall9k__MaxCloseoutLapsPerObstruction` | 2 | The progress cap: consecutive automatic laps closeout may spend on the SAME obstruction (the same failing check, the same unresolved threads) before parking — a lap that clears its obstruction resets this one, and a human re-engaging with the pull request grants one more lap past it |
| `Hall9k__MaxComplianceReviewCycles` | 3 | The conformance track's cap |
| `Hall9k__MaxAdversarialReviewCycles` | 10 | The adversarial track's cap |
| `Hall9k__AdversarialSeverityGateFromCycle` | 4 | From this cycle, only a `high` re-triggers the adversarial loop |
| `Hall9k__MaxFinalFullPassRounds` | 3 | The mandatory full-read pass immediately before settle's own cap: however many times it has run for this run, hitting this count without ever settling parks the run for a human |
| `Hall9k__LifetimeReviewCycleBudget` | 25 | The task-lifetime ceiling on review cycles, counted across every run and follow-up a task has had — immune to the per-run resets a stranding, retry, or follow-up round gives the three caps above; generous, so it only catches genuine pathology |
| `Hall9k__DefaultReviewRerequest` | disabled | Whether closeout asks reviewers for another pass after fixes push |
| `Hall9k__DefaultModel`, `Hall9k__ModelByRole__*` | | The node's model policy, per role (build, review, fix, synthesis, refinement, publication), plus `Hall9k__ModelByRole__ReviewVerify` — not a seventh role, but a narrower override for a Verify-shape review pass specifically, blank falling through to whatever review resolves |
| `Hall9k__SpendBudgetTokens` | unbudgeted | The node's periodic token-spend budget (backlog: spend-governor step three, Decisions Log #120) — once the current period's recorded spend reaches it, the dispatcher declines to claim further queued work until the period rolls; a non-negative whole number of tokens, or absent for no budget |
| `Hall9k__SpendPeriod` | week | The window `Hall9k__SpendBudgetTokens` resets on, `day` or `week` |

Before Decisions Log #111, the ceiling was set in agent sessions and spent in runs, so there was a
conversion between the number you configured and the number of tasks in flight. That conversion is
gone from admission itself: `Hall9k__MaxConcurrentTaskRuns` claims directly in runs, so every value
is meaningful — 1, 2, and 3 each admit one more run than the last, unlike the old setting, where 2
and 3 sessions both admitted exactly one run under a two-lens review cycle's peak.

The old `Hall9k__MaxConcurrentAgentSessions` key still works when the new one is absent — converted
`floor(sessions / 2)`, minimum 1, the "2" being the two review lenses a cycle dispatches together —
independently at each precedence level (an environment variable naming only the old key still
outranks a config-file value for the new one, the same way an environment variable always outranks
the config file). `h9k config show` and `h9k daemon status` both name the conversion when it
applies, and point at setting `--max-concurrent-task-runs` directly to stop relying on it:

| `Hall9k__MaxConcurrentAgentSessions` (legacy) | `Hall9k__MaxConcurrentTaskRuns` (converted) |
|---|---|
| 3 (the old default) | 1 |
| 4 | 2 |
| 6 | 3 |

That is why a fresh-from-the-old-key board says `waiting for a slot — 1 of 1 running` until you set
the new key directly. A second, independent knob, `Hall9k__SessionCapPerRun` (default 3), caps how
many agent sessions one run may hold simultaneously — the daemon knows exactly one activity that
can overlap within a run today (the two review lenses), so effective concurrency there is 2 by
construction and anything above 2 is inert headroom until a future coded activity actually overlaps
a third session. A cap of 1 serializes those two lenses — dispatching the second only once the
first's own result is recorded — which throttles the burn RATE, not the total tokens the identical
two passes spend. It is overridable per task at any time, including while the task's run is live,
with `h9k task set-session-cap <id> <cap>`; a change takes effect at the run's next session
dispatch — raising it lets the next phase fan out wider, lowering it never terminates a session
already running.

A queued row names the concurrency ceiling as its own reason only when the dispatcher recorded a
current measurement saying this node is full. With none, it says it is ready and stops, because a
queue that is not moving has many causes and a stopped daemon is the commonest. The spend budget is
not a per-row fact: `h9k status` names it once, on the Queued section's own heading, when the
current period's recorded spend has reached a budget the daemon has confirmed it is enforcing —
`h9k task show` and `h9k task list` never mention it at all.

### Daemon operating settings

The concurrency ceiling, the model-by-role policy, the four review-cycle caps, and the periodic
spend budget and its period are durable, not just environment variables (backlog 59): they also
load from the `"hall9k"` section of the platform config file
(`~/.hall9k/config.json`, the same file [§Postgres](#postgres) uses for `connectionString`),
deliberately outside `bin/` — an update replaces `bin/` wholesale, and these settings belong to
the machine, not the build. Precedence, highest first:

1. An environment variable under the `Hall9k__` prefix — this shell, this invocation.
2. The platform config file — a durable per-machine setting, written by hand or by `h9k config
   set`.
3. The built-in default (the values in the table above).

An environment variable therefore stays a one-off override rather than the only way to set
anything, which is what makes this durable for the case an environment variable structurally
cannot reach: a daemon started by autostart (a launchd `LaunchAgent` or a Windows Task Scheduler
logon task) has no operator shell to export anything into, so before backlog 59 it always ran on
built-in defaults no matter what the operator had configured by hand.

The same config file also carries `interactiveClaimStaleAfterDays` (backlog 59, Decisions Log
#103): how long an [interactive claim](#working-a-task-interactively) can sit untouched before
`h9k status` nudges about it, three days by default. It has no environment-variable tier and no
daemon-startup binding — there is no daemon-side reclaim to configure, ever, so `h9k status`
resolves it fresh from the config file on every render rather than off a process that started
once.

```bash
h9k config show                                             # every setting, and where it came from
h9k config set --max-concurrent-task-runs 2                 # the node's run ceiling
h9k config set --session-cap-per-run 1                      # the per-run session cap's global default
h9k task set-session-cap 28b19893 1                         # override the cap for one task, even mid-run
h9k config set --model-review sonnet --model-fix haiku      # per-role model overrides
h9k config set --model-review-verify sonnet                 # Verify-shape passes only; defaults to --model-review
h9k config set --interactive-claim-stale-after-days 5       # the interactive-claim nudge threshold
h9k config set --max-compliance-review-cycles 5 --lifetime-review-cycle-budget 40   # the node's review-cycle caps
h9k config set --spend-budget 5000000 --spend-period week   # the periodic token-spend budget and its window
h9k config set --spend-budget none                          # clear it back to unbudgeted
```

The four review-cycle caps (Decisions Log #112) — the conformance and adversarial track cycle
caps, the mandatory final-full-pass round cap, and the task-lifetime review-cycle budget — resolve
strictly `task > project > node > compiled default`, each independently of the other three: a
project or task that sets only one still inherits the rest from the levels above. `h9k config set`
is the node level; `h9k project set` carries the identical four options (`'default'` clears a
project override back to the node); `h9k task set-review-caps <ID>` is the task level, and is
deliberately different from every other task setting — it is settable at any time, including while
the task's run is live, so the daemon picks it up at the very next cap check. That is also the
documented takeover lever for a task observed grinding: setting a cap at or below the cycles that
track has run since its last human takeover grant (0, if it has never had one, which is also when
this count matches the absolute review cycle number `h9k status`/`h9k task show` print — a grant or
a track reactivation moves this count's own base forward, and only from there do the two numbers
diverge) parks the run the next time that cap is actually checked — a per-track cap at its next
fix-session dispatch, the final-full-pass cap at its next mandatory round — no new state or
command beyond the setting itself. It does not stop a run that converges clean before reaching
one of those checks; the lifetime budget is the one exception, checked at every settle point, so
setting it low parks a converging run too. `h9k task show` prints any per-task
override; `h9k project show` prints the project's own.

`h9k config set` writes to the config file; `h9k config show` resolves a setting the same way
`DaemonOptions` binds it at daemon startup — env, then config file, then default — and names the
origin of each, so a shell-quoting mistake or a typo in the file is diagnosable from one command
instead of a stale-log hunt. Hand-editing the file works just as well as the CLI: `h9k config set`
is the guided path, not the only one. A running daemon binds configuration once, at startup, so a
change — from either path — takes effect on the next `h9k daemon stop` / `h9k daemon start`, the
same as changing an environment variable would.

`h9k daemon status` prints the identical resolution, but it names what a daemon *started right
now* would pick up, not what the already-running process actually started with — and that gap
is not limited to the `(env: …)` origin. An autostarted daemon in particular never sees a
`Hall9k__` variable at all, since `DaemonEnvironment.InheritedVariables` carries only `PATH` and
the `HALL9K_*` redirection variables into the LaunchAgent, so an `(env: …)` origin there may not
match what it started with. The config-file and default tiers have the same gap for a different
reason: they are read from disk fresh on every invocation, which makes them fresh relative to the
*file*, not to the *running process* — a running daemon binds configuration once, at startup, so
if the file is created, edited, or removed afterwards, `h9k daemon status` shows the file's
current state while the daemon keeps running on whatever it read when it started.

`h9k update` never touches the config file. `h9k install` touches it in exactly one case — merging
in `connectionString` when nothing resolves yet anywhere in the [precedence chain](#postgres)
above and nothing is already listening on `localhost:5432` (Decisions Log #118) — and otherwise
leaves it alone the same as update does; a missing file is created (with defaults, and only the
settings you asked to change) the first time `h9k config set` needs it, and it says so.

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
h9k project set myproject --backlog github-issues
h9k project set myproject --rerequest-review on
h9k project set myproject --branch-template "{key}-{slug}"

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
| the pre-PR review loop's own park reason | The loop spent its automatic fixes, a fix session disputed a finding, or the task's lifetime review-cycle budget is spent (a park that can fire on a run that converged cleanly, spending no fix budget at all) | `h9k review resolve` |
| closeout's own park reason | The same obstruction survived its automatic-lap cap without clearing, or the pull request's lifetime automatic-closeout budget is spent | `h9k pr resolve` |
| the recorded dependency failure | A blocker died, so the dependent stays `Blocked` rather than silently unblocking | recover the blocker, as the recorded reason names |
| the agent asked a question and stopped | A run recorded a question and exited. `h9k ask` and `h9k answer` are Slice 2, so no command answers it | `h9k task show`, then decide it by hand |
| why the run failed, composed from what was recorded | The run itself failed | `h9k task retry` / `resolve` / `abandon` |
| the pull request is open and the task is unassigned | A follow-up reopened the task and it was then unassigned, so nothing will claim it | `h9k task assign` |
| no run record is watching it for a merge | The pull request is open and no run is left to observe the merge | the pull request itself |
| the run ended without a merge being observed | The run that owned an open pull request failed, or that pull request was closed unmerged | `h9k pr resolve`, or the pull request when it was closed unmerged |
| an interactive claim (`h9k task work`) last recorded activity — or was claimed and has not recorded a touch since — long enough ago | An [interactive claim](#working-a-task-interactively) — including a [start-it-mine claim](#a-deliberate-human-kick-off) made with `h9k task start`, which raises the identical row — has sat past `interactiveClaimStaleAfterDays` with no attached session observed on this machine; nothing reclaims it, this is only a nudge | `h9k task work <id>` if it is still yours, or `h9k task handback <id>` to finish it headlessly |

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

## Working a task interactively

`h9k task work <id> [--direct-launch] [--acknowledge-unmet-dependencies] | register-session |
verify | deliver | handback | release` (Decisions Log #103, #122, #124, #126, #127)

An operator can work a Published, Queued, or already-Blocked task in their own terminal instead of dispatching it
headless (Decisions Log #122). On a Published task assigned to nobody, `h9k task work` assigns it
to the operator's own owner and claims it interactively in one atomic event append, so the task is
never observably Queued for the dispatcher to claim in between. An unmet dependency — whether just
discovered here on a Published task, or already sitting Blocked from an ordinary `h9k task assign`
or a claim handed back or retried — warns rather than refuses (Decisions Log #128): the platform
names every open blocker, and `--acknowledge-unmet-dependencies` is the human's recorded override
to claim it anyway. Not needed twice: an acknowledgment this task already carries from an earlier
claim on the same still-open blockers is honored without asking again, and `h9k task show` names
whether a claim's own acknowledgment was given fresh or carried forward from an earlier one.
`h9k task assign` and `h9k task publish --assign` are unchanged and remain the headless dispatch
triggers — edges still gate automatic dispatch exactly as before; only this deliberate human claim
gets the warn-and-proceed path. Whichever state it entered from, the claim itself is held by the
human, not a process: there is no lease and no heartbeat reclaim, so
closing the terminal is a normal way to leave, and re-running `h9k task work <id>` re-enters the
same worktree and branch with a fresh prompt.

By default, `h9k task work` no longer launches anything itself (Decisions Log #126): it prints the
worktree path, the branch, and a starting prompt for the operator to paste into a Claude Code
session started anywhere. That pasted session's first act is `h9k task register-session <id>`,
which records its own process identity (read from `CLAUDE_PID`, Claude Code's own environment
variable) the way a direct launch's own launch-time recording always did — this is what lets the
double-booking and liveness guards below recognise it. A session that never registers gets the
same honest no-op every guard already had for a claim nobody ever recorded a session against:
nothing blocks a second entry, but nothing is silently overwritten either. `--direct-launch` keeps
the prior behavior for one release — a plain interactive Claude Code process launched and waited on
by `h9k task work` itself, recording its pid the moment it starts, and resuming the most recently
recorded session's own conversation on re-entry — falling back to a fresh session, announced rather
than silent, only when the recorded one cannot be resumed (Decisions Log #124) — refused on a
machine where Claude Code resolves to a Windows script shim (`.cmd`/`.bat`/`.ps1`), since the
shim's `cmd.exe` cannot carry the prompt's embedded newlines through its argv; the prompt-handoff
default is unaffected by that refusal, since the pasted prompt never travels through an argv at
all.

Nothing reclaims a quiet claim automatically, but a long-untouched one is easy to forget about:
once it has sat past `interactiveClaimStaleAfterDays` (three days by default, [configurable
above](#daemon-operating-settings)) with no attached session observed on this machine, `h9k
status` nudges it into needs-you asking whether it is still yours or ready to hand off — never a
reclaim, only a question (Decisions Log #103).

| Command | What it does |
|---|---|
| `h9k task work <id>` | Claims a Published, Queued, or already-Blocked task (assigning a Published one to your own owner in the same atomic event append), cuts the same branch and worktree headless dispatch would, assembles the prompt through the same code path (working rules swapped for an attached operator), and — by default — prints the worktree, the branch, and that prompt to paste into a session started elsewhere; `--direct-launch` opens a regular interactive Claude Code session itself instead, kept for one release. On a task you already hold, re-enters that same worktree and branch — by default with a fresh prompt, or, under `--direct-launch`, resuming the most recently recorded session's own conversation, falling back to a fresh session, announced rather than silent, only when the recorded one cannot be resumed. |
| `h9k task register-session <id>` | The pasted-in session's own first act: records its process identity (from `CLAUDE_PID`) against the claim, the way a direct launch's own launch-time recording always did. Refuses rather than guessing when `CLAUDE_PID` cannot be read. |
| `h9k task verify <id>` | Runs the project's verification gates on demand against the claim's worktree, recording the outcome on the run's own stream exactly as a headless gate pass would. |
| `h9k task deliver <id>` | Pushes the branch and hands the claim into the standard delivery pipeline — from here the run is indistinguishable from a headless one: gates, the pre-PR review loop, and the pull request all follow. |
| `h9k task handback <id>` | Releases the human claim and queues the task through normal dispatch, so a headless agent resumes the branch from wherever the operator left it. `--first` records the queue-first marker so the next free dispatch slot takes it regardless of assignment age; `--now` dispatches it immediately instead, ceiling-exempt, through the same mechanism `h9k task start` uses — refused together with `--first` (Decisions Log #127). |
| `h9k task release <id>` | Gives an untouched claim back to the dispatch queue. Refused once the worktree holds uncommitted files, or once the branch holds commits beyond the base branch — `handback` (to a headless agent) or `deliver` (yourself) is the lever once there is committed work. |

`work` (on re-entry), `verify`, `deliver`, `handback`, and `release` are all refused while the
claim's own interactive session is still attached in another terminal — exit it first (Ctrl+D or
`/exit`). One exception: `verify`, `deliver`, `handback`, or `release` run from inside that very
session (the one it is blocked waiting on, not racing — recognised either by a direct launch's own
injected marker, or by a self-registered session's `CLAUDE_PID` matching its own recorded process)
is allowed to act on its own worktree. `work` itself is never exempted, even from inside its own
session: re-entering spawns a second, concurrent session rather than blocking on the first, which
is exactly the collision this guard exists to prevent.

`work` (on re-entry), `verify`, `deliver`, `handback`, and `release` are also refused when the
claim's session was recorded on a machine other than the one running the command — this machine
cannot check whether it is still attached there. Confirm by hand on that machine that the session
has exited (or that the machine is simply gone for good), then re-run with `--force` to proceed
anyway.

## A deliberate human kick-off

`h9k task start <id> [--acknowledge-unmet-dependencies]` (Decisions Log #125)

A deliberate human kick-off dispatches a Published, Queued, or already-Blocked task on the spot, headless, instead
of dispatching it interactively (`h9k task work`, above) or waiting on the dispatch queue. `start`
reuses `work`'s own claim shape exactly — the same ceiling-exempt sentinel `NodeId`, so `verify`,
`deliver`, `handback`, `release`, the stale-claim nudge, and re-entering with `work` itself all
accept a start-it-mine claim on the identical terms an interactive one already gets — but launches
the agent headless and detached (`claude -p`, Claude Code's own completion mode) under the
`<task-shortid>-build` name, addressable on the session mesh (`claude agents --json`,
`SendMessage`) the moment it starts, rather than attached to the caller's own terminal, and
returns as soon as the process is confirmed alive without waiting for it to finish.

Shares `h9k task work`'s own warn-then-acknowledge shape for an unmet dependency, on a Published
task and on an already-Blocked one alike (Decisions Log #128): the platform names every open
blocker and advises, and `--acknowledge-unmet-dependencies` is the human's recorded override to
start anyway — recorded on the resulting claim and surfaced on `h9k task show` beside the blockers
it overrode. Not needed twice: an acknowledgment this task already carries from an earlier claim on
the same still-open blockers is honored without asking again, whichever of `start` or
`h9k task work` gave it. `start` refuses Draft (publish it first), a pr-review task, a reopened
task's follow-up branch, and any task that already carries a live claim — there is no re-entry path
the way `h9k task work` has one; a fresh claim is all `start` ever makes, and a fresh claim on an
already-Blocked task is exactly what its own Blocked entry is, not a re-entry.

Because nothing on this node is watching a start-it-mine run the way the daemon watches its own
dispatched runs, the row it leaves behind reads the same as an interactive claim's: an untouched
run sits at Working until a human runs `h9k task deliver`, `h9k task verify`, `h9k task work` (to
attach), `h9k task handback`, or `h9k task release`, and the same stale-claim nudge described above
fires once it has sat unattended past `interactiveClaimStaleAfterDays`. `h9k task deliver` is also
the only point anything on this node reads the session's own `stream.jsonl` back: it recovers the
agent's authored handoff for a dependent task and records the session's token usage against the
node's periodic spend budget, both of which nothing else would otherwise ever capture for a run
dispatched under the sentinel node id above.

Giving the claim back — `handback`, `release`, `retry`, or `pr resolve` reopening it — does not
land the task on Queued when the dependency snapshot the override acknowledged still names an
open blocker: only `h9k task assign` clears that snapshot, and claiming past a blocker
(`--acknowledge-unmet-dependencies`) never does, so the task lands Blocked instead until the
blocker actually closes out. Each command's own confirmation says which happened. The
acknowledgment itself stays on record for whichever command reclaims the task next — `h9k task
work` or `h9k task start` on the resulting Blocked task both honor it without asking again
(Decisions Log #128).

## The recovery levers

Five levers. Picking the wrong one loses work, and the question that separates them is *what
actually failed*.

| Lever | Use it when | What it does |
|---|---|---|
| `h9k task retry <id>` | The task is **Failed** and the machinery is what failed (a daemon bug, a dead process, a rejected push). The work has to run again. | Requeues the task. The failure stays on the stream. The new run resumes the failed run's branch when it survived, or starts clean from the base branch when the artifacts are gone. |
| `h9k task resolve <id> --reason "…"` | The task is **Failed** but the objective was met anyway: the work merged, or you finished it by hand, and only the bookkeeping died. | Ends the task Done on your attestation. `--reason` is required, because an attestation without a why is a guess. `--pr` records where the work landed, and, when it names a real pull request on the project's own repository, enrolls it in closeout's orphan sweep, so its later merge completes this task's closeout the same as any watched run — except on a pr-review task, whose `--pr` names the pull request it reviewed rather than one of its own and is never enrolled. |
| `h9k task abandon <id> --reason "…"` | You have stopped believing in the work. Reaches every non-terminal state, drafts and published tasks included. | Terminal. Releases any lease. Nothing is deleted: the reason is the record. |
| `h9k pr resolve <id> [--checks \| --rebase]` | The row reads **Delivered**, which is a pull request open with the merge not yet observed, and review feedback, failing CI, or a conflict with its base branch needs another pass. | Dispatches a follow-up run onto the existing branch and resets the monitor's automatic retry budget. |
| `h9k review resolve <id> --merge-ready [--reason "…"]` / `--needs-fixes "<why>"` | A run parked **before** its pull request, in the internal review loop, waiting on your verdict. | `--merge-ready` runs one mandatory full-scope verification gate over the fix unless this tip was already gated at full scope (nothing merges on scoped green alone), and proceeds to the pull request only if it passes. `--needs-fixes` dispatches a fix session with your reason as its findings and restores the fix budget. `--merge-ready` is refused when the park is a disputed rebase conflict (nothing has been rebased yet, so there is nothing ready to merge) — only `--needs-fixes` applies there. Either verdict's reason is recorded on the task and carried into every later review pass as a settled ruling — except on a thread-dispute park, which settles a disputed thread before any reviewer ever read the diff and so is not recorded as a review ruling — so pair `--merge-ready` with `--reason` when you dismiss a finding rather than leaving the next fresh-context reviewer to rediscover it. |

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
- **Two Postgres provisioning paths coexist by design** (the Aspire AppHost's and the one `h9k
  install` ships) and nothing reconciles them — resolved as deliberately separate rather than
  unified (Decisions Log #74; PLAN.md §15, row 28).
- **The stop-side mirror of the doctor's start-offer is not built.** `h9k daemon start` may start
  Hall9k's own Postgres container on your confirmation; `h9k daemon stop` does not offer to stop
  it, because nothing yet records that the offer (rather than something already running) is what
  started it. Named as a refinement in Decisions Log #73/#74.
- **Token exhaustion is not yet a distinct failure shape.** When the subscription window runs dry
  mid-flight, sessions die with a generic error and the board shows the text the machinery wrote
  rather than a category nobody observed. `backlog/40-token-exhaustion.md` is the fix.
- **There is no `h9k watch --notify`.** Nothing pushes a desktop notification, and no interactive
  session should promise to tell you when something finishes. `h9k status` is a window you look
  through, not an alarm.
