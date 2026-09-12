# Getting started

Written for the agent session reading it right after [INSTALL.md](INSTALL.md), a human second.
Every setting below is named by its exact flag; every command sits in its own fenced block; each
item carries one sentence of reason. This is not a narrative walkthrough, it is a checklist of
settings to know about — but a fenced command that assigns a concrete value (a model, a
concurrency number, a spend figure, a review-cycle cap) is an example to adapt to the user's own
machine and project, not a step to run as typed: several of these settings are node-wide, apply
to every project sharing this machine, and some have no `default` word to clear them back once
set. Read each item's own reasoning before deciding whether, and to what value, to run it.

Last reconciled against the tree on 2026-09-12.

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
- **Never restart the daemon under a live build or fix session.** Agents are detached processes by
  design, so `h9k daemon stop` does not stop them and the next `h9k daemon start` adopts whatever
  it finds still running ([operations.md](operations.md#the-daemon-lifecycle) has the full
  catch-up story), but a verify gate the stop interrupted is not one of the things adopted: a
  Testcontainer-backed `dotnet test` the daemon started as its own child process keeps running
  after the stop, uncancelled, and the resumed pipeline runs the gate again from scratch after the
  restart. Two suites then share one `obj/`/`bin/` and one Docker memory budget, producing the
  same file-in-use and Postgres-readiness failures covered below under "Problems you will hit
  first", except this time on an agent's otherwise-clean work. Wait for `h9k status` to show no
  session alive on the run before stopping the daemon.

  ```bash
  h9k status
  h9k daemon stop
  h9k daemon start
  ```

## Models by role and what it costs

- **Build, fix, review, synthesis, refinement, and publication are the six model roles, each
  independently settable** (`--model-build`, `--model-fix`, `--model-review`,
  `--model-synthesis`, `--model-refinement`, `--model-publication`). The shipped default puts the
  same exact model id on every role, deliberately: an exact id (`claude-opus-5[1m]`), not a tier
  alias (`opus`), because an alias is re-pointed as new models ship and drifting silently is the
  whole problem this setting exists to close (Decisions Log #33). `fable` is the human-interactive
  tier for a session a person is actually in, not a silent-agent default for build, fix, or
  review. Leave the six roles at the shipped default unless you have a specific reason to move
  one, and record that reason when you do. `--model-review-verify` and `--model-review-finalpass`
  are narrower knobs under `--model-review`, for a middle Verify-shape pass and the mandatory
  FinalFullPass respectively, each falling through to `--model-review` when unset:

  ```bash
  h9k config set --model-review-verify sonnet
  ```

  Resolution is task override, then this node's per-role default, then the project's own
  `--model`, then the platform fallback.
- **The shape of spend.** Prints the current period's recorded spend, by model, whether or not a
  budget is set; a node-level budget paces dispatch rather than reducing total spend.

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
- **A dead registered project makes the sweep warn on every tick.** The auto-pr-review sweep
  resolves every registered project's repository unconditionally, on every cycle, before anything
  that branches on that project's own setting runs; a project whose repository no longer exists
  makes that resolution fail and log a warning each time, harmless but noisy. `--auto-pr-review
  off` does not silence it: the resolution that fails happens before the setting is ever
  consulted, not after. There is no dedicated deregistration command yet, and no lever quiets a
  repository that is genuinely gone or moved: `project set --repo` only updates the daemon's local
  clone path, not the GitHub URL recorded at registration, and nothing mutates that URL
  afterward. Re-registering under the same name is refused (`project add` rejects a name that
  already exists), and registering the same repository under a new name leaves the dead project
  still registered, still warning every tick — there is no way through today short of editing the
  event store by hand, which is outside what this guide covers.

### Windows

- **PowerShell 7, never Git Bash, for every `h9k` command.** Git Bash's console codepage mangles
  the box-drawing output; `git`, `gh`, and ordinary shell tools are fine under either.
- **There is no `tail`.** Use PowerShell's own equivalent:

  ```powershell
  Get-Content -Path 'C:\Users\<you>\.hall9k\h9kd.log' -Wait -Tail 0
  ```

  Use a literal path, not `$HOME`: a monitor armed from a Git Bash shell expands `$HOME` to a
  `/c/...`-style path that `pwsh` cannot open, and it dies at once.
- **The log-handoff warning at restart is expected.** A freshly restarted `h9kd` can fail to open
  its own append-only handle onto `h9kd.log`; when that happens it prints why to the inherited
  handle and keeps logging to the same `h9kd.log` through it, just without the rotation-safe
  handle (the log may come back padded with NULs after the next rotation) — this is a printed
  warning, not a silent fallback, and not a switch to a separate console. After any restart,
  confirm rather than trust a quiet pane:

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

When a command here fails, start with `h9k status`: the attention pane names the cause underneath
the row and the exact command that clears it. [docs/operations.md](operations.md#the-recovery-levers)
is the fuller reference for the seven recovery levers and the two review-lap verdict commands, and
[operations.md#what-needs-you-means](operations.md#what-needs-you-means) is what every cause line
on the pane actually means.
