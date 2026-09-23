# Getting started

Written for the agent session reading it right after [INSTALL.md](INSTALL.md), a human second.
Every setting below is named by its exact flag; every command sits in its own fenced block; each
item carries one sentence of reason. This is not a narrative walkthrough, it is a checklist of
settings to know about — but a fenced command that assigns a concrete value (a model, a
concurrency number, a spend figure, a review-cycle cap) is an example to adapt to the user's own
machine and project, not a step to run as typed: several of these settings are node-wide, apply
to every project sharing this machine, and some have no `default` word to clear them back once
set. Read each item's own reasoning before deciding whether, and to what value, to run it.

Last reconciled against the tree on 2026-09-23.

Assumes `h9k doctor` and `h9k daemon status` both read clean, and a project has been registered
against the user's own repository (never this one):

```bash
h9k project add --name <name> --repo-url <the-user's-own-repo-url>
```

## The first hour after install

- **Register a project.** Shown above. `h9k project add` records permission skipping on by
  default, so a headless build session never stalls on a permission prompt it has no way to
  answer; turn it back off with `h9k project set <name> --skip-permissions false` if the
  project's own risk profile calls for prompts left live. Everything else in this section is a
  follow-up `h9k project set` on that same project.
- **Know what registration writes to the user's repository.** `h9k project add` runs `h9k project
  join` behind the clone, which generates this node's signing key once under
  `~/.hall9k/keys/<node-id>/` (a secret, readable by the user's account alone) and pushes signed
  commits to new `refs/hall9k/` refs on the repository's own remote. They are not branches and an
  ordinary clone does not fetch them, but anyone who can read the repository can read them. It
  needs `gh` signed in: registration refuses without a confirmed GitHub account, and the join
  refuses, leaving the registration in place, when that account cannot push to the repository.
  Tell the user this before registering, since it is the first time Hall9k writes anywhere they
  did not put it.
- **Joining a project somebody else owns, or adding a second machine of the user's own.** When the
  project already has an owner, registration writes nothing to the remote, names the owner, and
  asks for an invite. The owner mints it on their own node: `h9k project invite <name>` for a
  teammate, who becomes a member, or `h9k node invite` for another of the owner's own machines,
  which joins the owner's **fleet**. Either way the newcomer finishes with the secret, and the
  minting node's daemon vouches them in within a minute or so, so the newcomer then needs to wait
  rather than run anything else. Never invent an invite; ask the user which case this is.

  ```bash
  h9k project join <name> --invite <secret>
  ```

- **Whether a teammate's node may take a task from this one without asking, `--take-policy auto|ask`.**
  When another member runs `h9k task take <id>` for a task this node holds, `auto` (the default)
  hands it over the moment no run of it is live here, and `ask` parks every request for a person to
  answer with `h9k task grant <id>` or `h9k task refuse <id> --reason "..."`. It is a project
  setting, and it decides whether unattended work can move to somebody else's machine without
  the user hearing about it first, so `ask` is the safer choice on a project with more than one
  member.

  ```bash
  h9k project set <name> --take-policy ask
  ```
- **`--backlog none|github-issues|jira`.** Under `github-issues` or `jira`, `h9k task publish`
  refuses a draft with no linked external item unless it is published `--no-existing-item` (mint
  one) or `--untracked` (deliberately skip tracking); leave `none` (the default) for a project
  whose work does not need an external item at all, where publish asks neither question.
  `github-issues` writes a deterministic issue from the objective and criteria; `jira` dispatches
  an agent-composed card through `h9k task write-jira`.

  ```bash
  h9k project set <name> --backlog github-issues
  ```

- **The verify gate, `--verify "name=command"`.** This is what a dispatched session's work has to
  pass before a pull request opens.

  ```bash
  h9k project set <name> --verify "build=dotnet build" --verify "test=dotnet test"
  ```

  Each gate runs once, right here, best-effort against the project's own checkout rather than a
  guaranteed-clean one: a missing checkout skips the run and records the gate anyway, and a gate
  that cannot even start or overruns its own timeout is recorded unvalidated rather than as a
  failure. A gate that actually runs and fails refuses the whole command, whether or not the
  checkout it ran against could be confirmed clean — a checkout not confirmed clean only adds a
  loud warning that the failure below may be that local state rather than the gate itself, it does
  not turn the failure into a warning. `--accept-broken-gate` records a real failure anyway with a
  loud warning, for a gate you already know is broken independent of any agent's work.
- **The project's own parallel cap, `--max-parallel-tasks <N|0|default>`.** It paces token spend
  and, on a machine sharing Docker memory across containers, keeps the project's own Testcontainer
  load from stacking with other work. `0` pauses the project outright; `default` uncaps it back to
  the node ceiling.

  ```bash
  h9k project set <name> --max-parallel-tasks 1
  ```

- **Pre-approval is a per-task choice, not a project setting, `--pre-approved [on|after-human-review]`.**
  It removes the owner as a synchronous gate at the merge; every other human waypoint (a failure,
  a review park, a cap trip) still stops the pipeline exactly as it would otherwise.

  ```bash
  h9k task publish <id> --pre-approved
  ```

- **Auto pr-review's default, `--auto-pr-review off|normal|first|now`.** A pull request GitHub
  requests a review of, from the install's own login, mints and starts a `pr-review` task on its
  own; `normal` is the shipped default for every project and joins the ordinary dispatch queue.

  ```bash
  h9k project set <name> --auto-pr-review normal
  ```

- **Review personas, `h9k owner set --persona engineer|qa|designer`.** A member's declared
  personas decide how a pull request assigned to them is reviewed: the `pr-review` task runs one
  review session per persona, `qa` reviewing through the lens of blast radius and running the
  project's end-to-end tests, `designer` answering seven design lenses. Declaring none reads as the
  engineer's review, and repeating the flag replaces the whole declaration
  (`--clear-personas` declares none).

  ```bash
  h9k owner set --persona engineer --persona qa
  ```

- **The two review-drive settings, `--design-review-drive on|off` and `--qa-review-drive on|off`.**
  These are consent to launch a real process on this machine: with a drive setting on, a persona's
  review of a pull request stands the project's product up from its run skill, drives it through
  browser automation, and tears it down again, on every pull request that persona reviews. The
  designer's setting ships `on` and the QA setting ships `off`; a project with no run skill drives
  nothing whatever either says, and `h9k task run-local <task>` is how a reviewer stands a reviewed
  branch up by hand. Set the designer's to `off` on a project whose product should not be launched
  unattended.

  ```bash
  h9k project set <name> --design-review-drive off
  h9k project set <name> --qa-review-drive on
  ```

- **Documentation-only work, `h9k task add --type content`.** A content task is for documentation,
  skill markdown, prompt templates, or other non-compiled work: it dispatches with a reduced,
  conformance-only review by default and refuses at the gates, naming every offending path, if its
  diff touches anything outside the project's non-executable-path set.

  ```bash
  h9k task add --project <name> --type content --objective "<outcome>" --criteria "<check>"
  ```

- **The non-executable-path set, `--non-executable-path <glob>|default`.** Before any gate runs,
  the daemon classifies every path a run's branch changed, and when all of them match this set the
  verification gates (build and test, for most projects) are skipped outright and the skip is
  recorded with each path and the rule it matched; this applies to any task type, not only
  `content`. The compiled set is `*.md` anywhere,
  `docs/`, `.claude/skills/`, and `.claude/commands/`, with `.claude/templates/`, `AGENTS.md`, and
  `PLAN.md` carved back out of the markdown rule. A project's own globs only widen it, are layered
  on top of the compiled set, and repeating the flag replaces the project's whole list of them;
  `default` clears them.

  ```bash
  h9k project set <name> --non-executable-path "assets/**/*.png"
  ```

- **A bounded question, `h9k task add --type spike`.** A spike answers one stated question and
  never opens a pull request. It needs `--kind research|experiment|prototype` (research and
  experiment run no gates; prototype runs them and pushes its branch as evidence) and an
  `--exit-criterion`, one checkable sentence its review (one pass and at most one fix lap) judges
  the findings against.
  Its budget is optional: `--max-turns`, `--max-tokens`, and `--max-wall-clock` (a .NET timespan
  such as `00:30:00`), where crossing one records the run as budget-exhausted rather than failed.

  ```bash
  h9k task add --project <name> --type spike --kind research \
      --objective "<the question>" --exit-criterion "<how the answer is judged>" --max-wall-clock 00:30:00
  ```

  The [concepts page](concepts.md#task-types) explains both task types, its section on
  [personas](concepts.md#reviewing-a-pull-request-that-is-not-yours-personas) covers the review
  personas and the drive settings, and [the CLI reference](cli.md#task-types-budgets-and-pre-approval)
  lists every `task add` option.

## Running more than one task

- **The node ceiling.** How many task runs may be live on this machine at once, counted directly
  in runs (the shipped default is 1). Raise it only once the Docker VM's own memory ceiling has
  headroom for that many Testcontainer-backed suites at once (see the memory item below); the
  example raises it to illustrate the flag, not as a recommended number for every machine.

  ```bash
  h9k config set --max-concurrent-task-runs 2
  ```

- **The per-project cap.** A ceiling within the node ceiling, never a reservation: a project
  capped above its own share simply fills whatever the node and other projects leave free.

  ```bash
  h9k project set <name> --max-parallel-tasks 2
  ```

- **Why Docker memory forces a cap.** Every Testcontainer-backed run shares one Docker VM's memory
  budget with every other container on the machine; two runs' test suites sharing that budget is
  what produces Postgres readiness failures, not a bug in the tests themselves. Cap the affected
  project to 1 first, and raise it only once the VM's own memory ceiling has headroom to spare.
- **Prefer letting a live verification gate finish before you restart the daemon.** Agents are
  detached processes by design, so `h9k daemon stop` does not stop them and the next
  `h9k daemon start` adopts whatever it finds still running
  ([operations.md](operations.md#the-daemon-lifecycle) has the full catch-up story). A gate is the
  one exception: it is not reattached, it is ended and re-run from the start, so `h9k daemon stop`
  now warns by name (run, task, gate, pid) whenever stopping would orphan one, and
  `h9k update --restart` / `h9k install --restart` wait for it to finish on its own — up to thirty
  minutes — before stopping the daemon, printing what they are waiting on and that `--now` skips
  the wait. That wait is what keeps two suites from ever sharing one `obj/`/`bin/` and one Docker
  memory budget, the same file-in-use and Postgres-readiness failures covered below under
  "Problems you will hit first"; `--now`, or a plain `h9k daemon stop`, still trades that
  protection for restarting sooner. Wait for `h9k status` to show no session alive on the run
  first if you want to restart without either wait.

  ```bash
  h9k status
  h9k daemon stop
  h9k daemon start
  ```

## Models by role and what it costs

- **Build, fix, review, synthesis, refinement, publication, and the feed courier are the seven
  model roles, each independently settable** (`--model-build`, `--model-fix`, `--model-review`,
  `--model-synthesis`, `--model-refinement`, `--model-publication`, `--model-courier`). The
  shipped default puts the same exact model id on every role but one, deliberately: an exact id
  (`claude-opus-5[1m]`), not a tier alias (`opus`), because an alias is re-pointed as new models
  ship and drifting silently is the whole problem this setting exists to close (Decisions Log
  #33). The courier's own field ships blank like every sibling role's — `h9k config show` prints
  it the same way — but its *resolution* is the one deliberate exception (idea 89471598, piece
  3): where a blank Build or Review falls through to the project or platform default, a blank
  courier bottoms out at `claude-sonnet-5` instead, since a short-lived session that only relays
  a project's own feed to a live orchestrator window has no business defaulting to the same tier
  a build or review session does. `fable` is the human-interactive tier for a session a person is
  actually in, not a silent-agent default for build, fix, or review. Leave the other six roles at the
  shipped default unless you have a specific reason to move one, and record that reason when you
  do. `--model-review-verify` and `--model-review-finalpass`
  are narrower knobs under `--model-review`, for a middle Verify-shape pass and the mandatory
  FinalFullPass respectively, each falling through to `--model-review` when unset:

  ```bash
  h9k config set --model-review-verify sonnet
  ```

  Resolution is task override, then this node's per-role default, then the project's own
  `--model`, then the node's `--default-model`, which is the platform fallback until you change it.
- **The platform default, `--default-model`, and the orchestrator window's own model,
  `--orchestrator-model`.** `--default-model` is the model every agent session runs on unless a
  more specific level says otherwise, and it is what the chain above bottoms out at (the platform
  fallback is `claude-opus-5[1m]`); `default` clears an override back to that fallback.
  `--orchestrator-model` is the model the orchestrator window itself runs on, deliberately
  independent of the dispatch model, so raising or lowering what dispatched agents run on never
  moves the window you are sitting in, and the reverse. It is set on the node with
  `h9k config set` and per project with `h9k project set`, where the project's own value wins and
  `default` clears it back to the node's resolution.

  ```bash
  h9k config set --default-model claude-opus-5
  h9k config set --orchestrator-model sonnet
  h9k project set <name> --orchestrator-model sonnet
  ```

- **The shape of spend.** Prints the current period's recorded spend, by model, whether or not a
  budget is set; a node-level budget paces dispatch rather than reducing total spend. `h9k status`
  prints a throughput block right beneath it, for the same period: tasks merged, median and p90
  claim-to-merge, first-pass merge share, laps per merged task, and the share of task time spent
  queued or waiting on a human — held back for a plain count under five merged tasks, rather than a
  median of two. `h9k project show` prints the same block scoped to its own project, current
  period beside the previous one.

  ```bash
  h9k config show
  h9k config set --spend-budget 5000000 --spend-period week
  h9k config set --spend-budget none
  ```

- **The review-cycle caps, four of them, each resolving task then project then node then compiled
  default.** `--max-compliance-review-cycles` (compiled default 3), `--max-adversarial-review-cycles`
  (compiled default 4), `--max-final-full-pass-rounds` (compiled default 2), and
  `--lifetime-review-cycle-budget` (compiled default 20, immune to per-run resets). The project and
  task levels take `default` to clear an override back to whatever the next level up resolves to;
  the node level, set through `h9k config set`, does not; once set there, the only way back is to
  set it again to the compiled default's own number or hand-edit `config.json`. Prefer the project
  or task level unless the change is genuinely meant for every project this node runs.

  ```bash
  h9k project set <name> --max-compliance-review-cycles 5
  h9k task set-review-caps <id> --lifetime-review-cycle-budget 40
  ```

- **Write down why a cap changed, the moment it changes.** A cap raised without a recorded reason
  is indistinguishable, a week later, from one nobody ever meant to change; note the reason
  wherever your own working notes live at the moment you run the command, not afterward.

## Problems you will hit first

- **`gh` must be signed in as the install's own login.** The daemon shells out to `gh` using
  whichever account is currently active; on a host also used for other GitHub work, check and
  switch back before an install-owned run misfires under the wrong identity.

  ```bash
  gh auth status
  gh auth switch
  ```

- **The verify-gate validation cap is 5 minutes.** `h9k project set --verify` runs each gate once
  against the project's own checkout, best-effort against a clean base branch rather than
  guaranteed one (see "The verify gate" above); a gate that legitimately takes longer than 5
  minutes (a full `dotnet test`, say) cannot be validated within that cap and is recorded
  unvalidated rather than refused. This is expected, not a failure — it is distinct from a gate
  that actually runs and fails, which refuses the command regardless of how long it took.
- **A Testcontainer readiness failure is memory pressure, not a broken test.** Every Postgres
  container a test spins up shares the same Docker VM memory budget; before treating a readiness
  failure as a regression, check the VM's own available memory and the number of containers
  currently held, and lower `--max-parallel-tasks` on the affected project rather than re-running
  blind.
- **A dead registered project is archived, not force-deleted.** `h9k project remove <name>
  --reason "<why>"` archives it, reversibly: the dispatcher stops claiming its tasks, the
  auto-pr-review and project-home render sweeps skip it, and `h9k project list` hides it by
  default (`h9k project reactivate` undoes it in place). Neither the repository nor the home
  directory on disk is touched. Add `--purge` to also schedule a permanent hard delete of the
  project's database footprint twenty-four hours out — its own stream, every task, run, idea, and
  epic stream it owns — cancellable any time before it fires with `h9k project cancel-purge`.
  `h9k project add` under a name that already names an archived project offers
  `--reactivate-archived` or `--rename-archived-to <name>` to free the name, both answerable
  non-interactively.

### Windows

- **PowerShell 7, never Git Bash, for every `h9k` command.** Git Bash's console codepage mangles
  the box-drawing output; `git`, `gh`, and ordinary shell tools are fine under either.
- **There is no `tail`.** For a human following the log by hand, use PowerShell's own equivalent:

  ```powershell
  Get-Content -Path 'C:\Users\<you>\.hall9k\h9kd.log' -Wait -Tail 0
  ```

  The orchestrator recipe itself never tails or polls the log at all: no recipe this platform
  generates arms `tail -F`, a byte-offset log waiter, or the `Monitor` tool for any watch, on
  Windows or anywhere else — the daemon's own feed courier delivers a project's undrained feed
  items straight into a live orchestrator window instead, so there is no follow pipeline for a
  Windows session to leave running or orphaned.
  Use a literal path, not `$HOME`, in that command: the Bash tool on this platform runs Git Bash
  even when the rest of the session targets PowerShell, and a path composed under Git Bash's own
  `$HOME` expands to a `/c/...`-style path that `pwsh` cannot open, and dies at once.
- **If you registered autostart before the launcher opened the daemon's log for it (Decisions Log
  #222), re-run `h9k daemon autostart enable` once.** The
  daemon's log used to reach it through a shell redirect, `cmd.exe /c "h9kd < NUL >> h9kd.log
  2>&1"`, and cmd.exe holds an append redirect's target with `FILE_SHARE_READ` only for the whole
  run: readers welcome, a second writer refused. So `h9kd` could never take its own log over with a
  rotation-safe append handle, and the log's 8 MB budget went unenforced while it ran. Both launch
  paths now open that handle for it and hand it over, and nothing holds the log but the daemon
  itself. The one thing that does not update itself is an autostart registration's launch script:
  no `h9k install` and no `h9k update` rewrites it, only `h9k daemon autostart enable` does. Until
  you run that, an autostarted daemon keeps launching the old way and logs a sharing-violation
  warning at start naming this remedy. Two things are worth knowing if you see that warning. Your
  own `Get-Content -Wait` reader is never the cause: cmd.exe admits readers, and so does the handle
  `h9kd` tries to open. And nothing is hidden from you by it: every line still lands in that same
  `h9kd.log`, and this is a printed warning rather than a silent switch to some other console.
  After any restart, confirm rather than trust a quiet pane:

  ```bash
  h9k daemon status
  h9k --version
  ```

- **A reboot orphans live runs.** With autostart off, a reboot leaves agent processes dead and the
  daemon down; the next `h9k daemon start` fails those runs as orphaned rather than resuming them.
  Expect to retry, resolve, or abandon each one afterward.

  ```bash
  h9k task retry <id> --reason "…"
  h9k task resolve <id> --reason "…"
  h9k task abandon <id> --reason "…"
  ```

## A normal day

```bash
h9k task add --project <name> --from-issue <n>
h9k task publish <id> --pre-approved
h9k task assign <id>
h9k task show <id>
h9k task retry <id> --reason "<why the machinery failed, not the work>"
h9k review resolve <id> --merge-ready --reason "<why the finding is dismissed>"
h9k review resolve <id> --needs-fixes "<what the finding actually is>"
h9k task resolve <id> --reason "<why the objective is met anyway>" --pr <url>
h9k status
```

Adopt from an issue, publish it pre-approved, assign it, and `h9k status` is the one command worth
running on a loop after that: it is the attention pane, bounded and glanceable, naming the cause
underneath every row and the exact command that clears it, rather than a state to scan for.

## Supervising it from an agent window

An orchestrator window is the standing, disposable session that reads this board on your behalf
between the moments you sit down at it yourself. See the README's own [Orchestrator
windows](../README.md#orchestrator-windows) section for what it is and how to start one; nothing
here repeats it.

A live window is kept current by the **feed courier**: a short-lived, cheap-model session the
daemon spawns to deliver a project's undrained feed items into it, urgent ones at once and routine
ones batched, so nothing polls the daemon's log or the feed. The window's own start-up step,
`h9k orchestrator feed --project <name> --drain`, still catches it up on whatever happened while
none was live. The README's [feed courier
passage](../README.md#orchestrator-windows) says when one is spawned and how to tune it
(`h9k project set <name> --courier-max-wait`, `h9k config set --model-courier`), and
[concepts.md](concepts.md#the-feed-courier) has the delivery model.

When a command here fails, start with `h9k status`: the attention pane names the cause underneath
the row and the exact command that clears it. [docs/operations.md](operations.md#the-recovery-levers)
is the fuller reference for the nine recovery levers and the two review-lap verdict commands, and
[operations.md#what-needs-you-means](operations.md#what-needs-you-means) is what every cause line
on the pane actually means.
