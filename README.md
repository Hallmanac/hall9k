# Hall9k

**Local-first agentic task orchestration.** You write a piece of work down, publish it, and
assign it. A daemon on your own machine claims it, prepares an isolated git worktree, spawns a
detached Claude Code session to do the work, runs your build and test gates, puts the diff
through an independent review loop, and opens a pull request. Then it watches that pull request:
it dispatches follow-up sessions to answer review threads and fix failing CI, and it tells you
the one thing left that only you can do. Nothing is hosted. `h9k` is a CLI on your PATH, `h9kd`
is a background process on your machine, and a Postgres container holds every fact. The point is
not that agents write code unattended; the point is that you stop being the message bus between
ten terminals and get to engage on your own schedule.

The name is the thesis. Hall (the owner) plus HAL 9000: HAL is the AI that escaped supervision,
and Hall9k exists to keep the human in the loop. The platform never merges a pull request, never
guesses when a decision is owed to a person, and never claims to have observed something it did
not.

Hall9k is built by Hall9k. Since the pipeline first ran end to end, every platform feature has
been dispatched as a Hall9k task, which is why the documentation below is written from practice
rather than from intent.

**What it is not.** Not a work tracker: Jira and GitHub already do that, and once work is
published the external item is the source of truth for its content. Not an agent runtime: Claude
Code already does that, and Hall9k spawns it rather than reimplementing it. Not a hosted service:
local-first is the identity, not a stopgap on the way to SaaS.

---

## What it looks like to use

### Write the work down

Creation asks for identity, not readiness: a project and an objective. Acceptance criteria are
what the readiness gate will demand, so it is worth writing them here.

```
$ h9k task add --project demo \
    --objective "Add rate limiting to the auth endpoints" \
    --criteria "A sixth login attempt inside a minute is refused with 429" \
    --criteria "dotnet build and dotnet test pass"

Draft created in 'demo': Add rate limiting to the auth endpoints
(01a02edb-2c55-7030-b4dd-4a9e2088f4fc)
Next: h9k task publish 2088f4fc (a draft never dispatches; publishing then
assigning is what starts it)
```

A draft is invisible to the dispatcher. You can revise it as many times as you like, add
dependencies with `--blocked-by`, or throw it away. Every command takes that short form, or any
unambiguous fragment from either end of the full id.

### Publish it, then assign it

Publishing is the readiness gate. It enforces the contract (an outcome-phrased objective and at
least one checkable acceptance criterion) and refuses a dependency cycle by naming it hop by hop.
Assigning is the separate, explicit act that makes the work dispatchable.

```
$ h9k task publish 2088f4fc --assign

Task 2088f4fc published: Add rate limiting to the auth endpoints
Task 2088f4fc assigned to Brian Hall — queued; the next dispatch cycle on one of
their nodes claims it.
```

### Watch the pipeline

`h9k status` is the attention pane. It answers what needs you, what has gone quiet, and what is
running, and it is bounded on purpose: browsing lives under `h9k task list` and `h9k project
list`. This is a real board mid-build, trimmed to three rows:

```
$ h9k status

Working — a run owns it and has not pushed yet
0ac5a6d2  Working  hall9k  Brian Hall  Replace the work-in-prog…    just now
    ↳ building · session alive

Delivered — pushed; the merge has not been observed
983ee6ec  Delivered  hall9k  Brian Hall  The platform recognizes…  needs you
added 24h ago  #31
    ↳ watching PR #31 — waiting on your merge · no finding recorded; its checks
may still be reporting
    ↳ nothing has been recorded against this pull request — read its checks,
then the merge is yours → https://github.com/Hallmanac/hall9k/pull/31

Queued — the node is at its concurrency ceiling; each of these starts as a run
finishes. Raise Hall9k__MaxConcurrentAgentSessions and restart the daemon to run
more at once — it is counted in agent sessions, and a run under review holds one
per review lens
81d8bca0  Published  hall9k  Brian Hall  The closeout sweep obse…    added 24m
ago
    ↳ assigned and ready; the dispatcher has not claimed it yet · waiting for a
slot — 1 of 1 running
```

Three things are worth reading off that. The row's **state** is the lifecycle in one word. The
line under it is the **phase**, composed from the run's records plus an observation of the
recorded process: "building · session alive" means a process was actually seen, and a phase that
cannot see one says "liveness not observed here" rather than guessing. The **needs you** marker
is followed by the cause and the command that clears it, both quoted from something the platform
recorded.

The "1 of 1 running" on that last row is the shipped default, not a stalled node: the ceiling is
configured in agent sessions and spent in runs, and a run is charged the two review sessions a
cycle dispatches together, so the default of 3 sessions buys one run at a time.
[docs/operations.md](docs/operations.md#configuration) has the conversion.

`h9k task show <id>` is the second command of any investigation, and `h9k logs <id>` renders the
session transcript when the first two have already named the task worth digging into.

### Review the pull request

The daemon opens the pull request; agents never do. The task reads **Delivered** from that moment
until a merge is observed, which is the honest word for pushed-but-not-landed. While the pull
request is open, the closeout monitor polls it: unresolved review threads from any reviewer and
failing CI each dispatch a follow-up session onto the existing branch, bounded by a retry budget.
When the budget runs out, the row lands in **needs you** with `h9k pr resolve` as the lever.

You merge. The platform never does. The observed merge is true closeout: it is the moment the run
completes, dependents unblock, and the worktree is removed.

---

## Install

**A machine with no repo checkout and no .NET SDK:** run the bootstrap script — it fetches the
latest release for your platform (macOS arm64, Windows x64, Linux x64), verifies its checksum,
asks consent, and finishes with `h9k doctor`. See [docs/INSTALL.md](docs/INSTALL.md) for the full
walkthrough, including the agent-driven, non-interactive form.

```bash
curl -fsSL https://raw.githubusercontent.com/Hallmanac/hall9k/main/scripts/install.sh | bash   # macOS / Linux
```

```powershell
iwr https://raw.githubusercontent.com/Hallmanac/hall9k/main/scripts/install.ps1 | iex           # Windows
```

Once installed, `h9k update` is the one-command path to stay current — no repo checkout needed
there either.

**Working on Hall9k itself:** clone the repo and build locally (below). The daemon's full
lifecycle (`h9k daemon start` / `stop` / `autostart enable`) runs on macOS and Linux; Windows
builds and tests in CI and `h9k install`/`h9k update` place the binaries there, but running
`h9kd` on Windows is future work (`SLICE-1.md`'s S1-14). Start-at-login (`autostart enable`) is
macOS-only even on Linux, where the daemon otherwise runs fine started by hand.

### Prerequisites

| What | Why |
|---|---|
| .NET 10 SDK | Building the solution and publishing the release binaries |
| Docker | Postgres runs in a container; the integration test tier uses Testcontainers |
| `git` | Worktrees, branches, and every commit an agent makes |
| `gh`, authenticated | Pull requests, issue adoption, review threads, and check results |
| Claude Code CLI (`claude`), logged in | The executor: every agent session is a detached `claude -p` |

The daemon resolves `claude`, `gh`, and `git` through `PATH`, so they have to be reachable from
the environment the daemon starts in. `h9k daemon autostart enable` snapshots the enabling
shell's `PATH` and warns at enable time about any of the three it cannot resolve, because an
autostarted daemon missing one starts fine and then fails every run.

### Stock the CLI toolbelt

Install more command-line tools than you strictly need, and keep them on `PATH`. **CLIs are how
an agent reaches a capability on demand**: they cost nothing while idle, they need no
configuration to be discoverable, and every platform session to date has worked entirely through
them rather than through an MCP server. That is also the direction of travel, since dispatched
sessions are moving to a slim profile where MCP servers are declared per task rather than
inherited wholesale ([`backlog/29-slim-agent-profile.md`](backlog/29-slim-agent-profile.md)).

Beyond the three above, the ones that have earned their place are Atlassian's Teamwork Graph CLI
(`twg`) for Jira and Confluence work, and whatever your own projects' workflows lean on. The rule
of thumb: if you would reach for a tool at the terminal to answer a question, an agent will too,
and a well-stocked PATH is what makes a lean agent a capable one.

### Steps

```bash
git clone git@github.com:Hallmanac/hall9k.git
cd hall9k

docker compose up -d          # Postgres on localhost:5432
dotnet build                  # the whole solution

./src/Hall9k.Cli/bin/Debug/net10.0/h9k install
```

`h9k install` publishes release binaries of `h9k` and `h9kd` into `~/.hall9k/bin`, links `h9k`
onto your PATH, and publishes the canonical skill set into `~/.hall9k/skills`. It registers no
background service and no login item, deliberately: the daemon has a CLI-owned lifecycle. Re-run
it after a merge to refresh the binaries, and it will offer to restart a running daemon onto them.
On a machine that already has `h9k` installed (from source or from a release), `h9k update` does
the same idempotent refresh from the latest GitHub release instead, no repo checkout needed.

```bash
h9k daemon start              # detached; survives shell exit; logs to ~/.hall9k/h9kd.log
h9k daemon status             # running or not, pid, uptime, autostart posture, recent log

h9k project add --name myproject --repo-url git@github.com:you/myproject.git
```

First use registers your owner record (from `git config user.name` / `user.email`), this machine
as a node, and a GitHub connection pointing at your `gh` login. Nothing to configure; it is
idempotent.

`project add` also creates the project's **home directory**, `~/.hall9k/projects/myproject`,
which is the same shape on every machine: a generated `AGENTS.md`, `repo/` (a bare clone with a
`dev/` worktree on the primary branch, and the task worktrees dispatch cuts beside it), `ideas/`,
`tasks/`, `skills/` seeded from the install's set, and a generated `.claude/` adapter. Point an
editor at it and you browse the code, the worktrees and the work together; start a Claude session
in it and its `AGENTS.md` tells it the rest. For a project this database already knows about,
`h9k project init <name>` creates or repairs the same shape. Both are idempotent, both are
platform code with no agent in them, and `--home <path>` puts the directory wherever you want it.

Then the loop from the section above: `h9k task add`, `h9k task publish --assign`, `h9k status`.

Start-at-login is a separate opt-in that is never implied by anything else:

```bash
h9k daemon autostart enable   # macOS launchd LaunchAgent
h9k daemon autostart disable
```

### The dev loop

For working on Hall9k itself, the Aspire AppHost brings up Postgres, the daemon, and the
dashboard together, and manages its own Postgres container separately from `docker-compose.yml`:

```bash
dotnet run --project src/Hall9k.AppHost
dotnet test                   # unit and integration tiers; integration needs Docker
```

### Pointing at a different database

Nothing is configured by default — install stays boring on purpose (Decisions Log #58). If a
command needs a database and cannot reach one, it runs `h9k doctor` for you instead of failing
raw; run it yourself any time to see the same diagnosis. `HALL9K_CONNECTION_STRING` is the
highest-precedence of the three places a connection string can live (see
[docs/operations.md](docs/operations.md#postgres) for the full precedence and the doctor check),
and `HALL9K_HOME` relocates the whole on-disk layout away from `~/.hall9k`. One caveat that bites:
`h9k status` probes this machine's pid file, so it answers "is a daemon alive here" rather than
"is a daemon serving this database".

---

## Where the deeper docs live

Start here, in this order:

- **[docs/concepts.md](docs/concepts.md)** is the layer under the README: tasks, runs, the
  lifecycle and the words the board shows, leases, the review loop, and closeout.
- **[docs/cli.md](docs/cli.md)** maps the command surface and explains why the `--help` tree,
  not a page in this repository, is its source of truth.
- **[docs/operations.md](docs/operations.md)** is running the thing: the daemon's lifecycle,
  configuration, what lands on disk, what `needs you` means, and the five recovery levers.
- **[docs/scope.md](docs/scope.md)** is the honest inventory: what works today, what is designed
  but unbuilt, and what the project deliberately will not do.

Below that sit the documents the new docs point into rather than replace:

- **[PLAN.md](PLAN.md)** is the vision, the architecture, and the v0 Decisions Log in §16. Every
  binding decision lives there, numbered, with its reasoning. When a doc here cites "#24" or
  "log #66", that is where to look.
- **[TASK-MODEL.md](TASK-MODEL.md)** is the domain reference: event streams, aggregates,
  projections, the state machines, and the type discipline.
- **[AGENTS.md](AGENTS.md)** is the contributor and agent guide: coding standards, git rules, the
  review rhythm, and the orchestrator-window role an interactive session takes on in this repo.
  `CLAUDE.md` defers to it so every agent runtime shares one source of truth.
- **[SLICE-1.md](SLICE-1.md)** is the current build breakdown and its acceptance criteria.
- **[HALL9K-P2P-DESIGN.md](HALL9K-P2P-DESIGN.md)** is the peer-to-peer layer: identity,
  discovery, NAT traversal. Design only; nothing is built.
- **[backlog/](backlog/)** holds one file per unstarted piece of work. The numbered ones carry an
  objective and acceptance criteria in the frontmatter `h9k task add --file` reads; the `IDEA-`
  notes beside them are earlier-stage prose. It is what tasks get authored from, and it is where
  [docs/scope.md](docs/scope.md) points when it says something is designed but unbuilt.

---

## Scope, briefly

**Working today:** the whole dispatch pipeline (draft, publish, assign, claim, worktree, detached
agent, verification gates, two-lens pre-PR review, pull request, closeout monitoring, merge
observation); the task dependency graph with context routing along its edges; ideas and
promotion; GitHub issue and Jira card adoption; per-project and per-owner settings; failed-task
recovery; the attention pane.

**Designed but not built:** the mid-run question loop (`h9k ask` / `h9k answer`, Slice 2: the
events are on the stream and the commands are not, so an agent that needs a decision today makes
the most reasonable call and records the assumption); `h9k watch --notify`; Windows support;
multi-node and peer-to-peer; formal triage and discovery flows.

**Deliberately not doing:** hosted SaaS, a kanban UI, two-way content sync with Jira or GitHub,
bulk backlog mirroring, and merging your pull requests.

[docs/scope.md](docs/scope.md) has the full inventory with backlog pointers.

---

## Contributing

Read [AGENTS.md](AGENTS.md) first; it is the contract for humans and agents alike. The short
version: `dotnet build` and `dotnet test` are the gate, commits are authored as the repository
owner with no bot attribution, agents reply inside review threads and never open one, and in this
repository all new work enters through `h9k task add`.
