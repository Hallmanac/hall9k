# Installing Hall9k

This doc is written to be followed end to end by an AI coding agent with shell access, as
much as by a human — "tell your session to install Hall9k" should work from this file alone,
on a machine that has never seen this repository (backlog 42). It is hosted at a stable raw
URL so it can be fetched without a checkout:

```
https://raw.githubusercontent.com/Hallmanac/hall9k/main/docs/INSTALL.md
```

## What you get

`h9k` (the CLI) and `h9kd` (the daemon it drives) as native binaries in `~/.hall9k/bin`, on
your `PATH`, plus the canonical Claude skill set in `~/.hall9k/skills`. **Nothing is started
and nothing is registered as a background service or login item** — the daemon runs on
demand (`h9k daemon start` / `stop`), and start-at-login is a separate, explicit opt-in
(`h9k daemon autostart enable`).

## Prerequisites

- **`gh`, the GitHub CLI, authenticated** (`gh auth login`) — Hall9k's releases live in this
  repository, and reading them (a private repo especially) needs a logged-in `gh`. Nothing
  else is required: no repo checkout, no .NET SDK, no Docker (Docker is only needed later,
  for Postgres, and `h9k doctor` teaches that at the moment it matters).
- macOS (arm64), Windows (x64), or Linux (x64). Other platforms are not built by `release.yml`.

## One-line bootstrap

**macOS / Linux:**

```bash
curl -fsSL https://raw.githubusercontent.com/Hallmanac/hall9k/main/scripts/install.sh | bash
```

**Windows (PowerShell):**

```powershell
iwr https://raw.githubusercontent.com/Hallmanac/hall9k/main/scripts/install.ps1 | iex
```

Both scripts do the same five things, in order:

1. Fetch the latest GitHub release's archive for your platform via `gh release download`.
2. **Verify its checksum** against the release's own `checksums.txt` — refusing to install a
   payload that does not match what was published.
3. **Ask consent** before changing anything on the machine (skip with `--yes` / `-Yes` for a
   non-interactive run, e.g. an agent-driven install).
4. Unpack the release and run the release's own `h9k install --from-release <payload>` —
   the same idempotent publish-and-refresh `h9k install` has always done, just fed from a
   downloaded archive instead of a local `dotnet publish`. This is what places the binaries,
   writes Hall9k's own Postgres definition (not started), writes the matching connection
   string to `config.json` when nothing else resolved and nothing is already listening on
   `localhost:5432`, publishes the skill set, and puts `h9k` on your `PATH`.
5. Run **`h9k doctor`** — so the bootstrap ends by telling you exactly what still needs
   attention (usually: starting Hall9k's own Postgres) rather than declaring victory silently.

Pass `--yes` (bash) or `-Yes` (PowerShell) to skip the consent prompt — the shape an agent
driving this file end to end should use. `iwr | iex` runs the downloaded script with no way
to pass it a parameter, so the non-interactive Windows form downloads the script into a
scriptblock first and invokes that with `-Yes` instead:

```bash
curl -fsSL https://raw.githubusercontent.com/Hallmanac/hall9k/main/scripts/install.sh | bash -s -- --yes
```

```powershell
& ([scriptblock]::Create((iwr https://raw.githubusercontent.com/Hallmanac/hall9k/main/scripts/install.ps1).Content)) -Yes
```

## What an agent should do differently from a human

Nothing, mechanically — the bootstrap script is the same either way. The two differences:

- **Use `--yes` / `-Yes`.** There is no human at a `curl | bash` pipe's terminal to answer a
  prompt, and the scripts read from `/dev/tty` when one exists but fall back to failing
  closed otherwise (they never guess consent). On Windows, `-Yes` cannot reach a script run
  with `iwr | iex` — use the scriptblock form shown above instead.
- **Read what `h9k doctor` says at the end, and act on it or report it.** Install now writes
  the matching connection string to `config.json` when nothing else resolved and nothing is
  listening on `localhost:5432`, so a fresh machine's bootstrap ends with `h9k doctor`
  reporting a configured-but-unreachable database ("Configured (from the platform config file
  …) to connect to localhost:5432, but nothing is listening there") rather than "no connection
  string configured" — that is still expected, not a failure of install. Follow it with
  `h9k doctor --yes` to remediate non-interactively (starts Hall9k's own Postgres via the
  generated compose file and creates the schema, when Docker is running) instead of the
  interactive prompts a human would answer by hand. The one machine that ends up somewhere
  else is one already running a Postgres on that port: install says what it skipped and
  writes nothing, doctor reports "no connection string configured" exactly as it always did,
  and the decision of which server to use is a human's. See *Connecting a database* below
  (Decisions Log #99).

## After bootstrap: staying current

A machine that already has `h9k` never needs the bootstrap script again — the same binary
updates itself:

```bash
h9k update
```

This is `h9k install --from-release`'s download half, wired to the same idempotent finish:
it fetches the latest release for your platform via `gh`, verifies the checksum, republishes
the binaries and the canonical skill set, and offers to restart a running daemon onto the
fresh binaries — no repo checkout, no .NET SDK, on the machine that runs it. `h9k update
--restart` skips the restart prompt.

## Connecting a database

`h9k` needs a Postgres connection string; nothing is *started* automatically, and nothing
is guessed (Decisions Log #57, #58, #99). If nothing resolved before you ran the installer,
install already wrote the one connection string that matches the compose file it just wrote,
so there is usually nothing left to configure by hand. The one case it deliberately leaves
alone is a machine with something already listening on `localhost:5432`, your own native
Postgres, say, which is a supported way to run this (Decisions Log #57 takes no position on
where Postgres runs). Install cannot tell whose server that is, so rather than write its own
compose credentials against it and turn doctor's "something is already listening" into a
confusing authentication failure, it prints what it skipped and leaves the machine
unconfigured: point `HALL9K_CONNECTION_STRING` or `~/.hall9k/config.json` at your own server,
or stop that Postgres if you want Hall9k's. Run `h9k doctor` any time — it is the
same check, forever, and it teaches the fix at the moment you can act on it: is a connection
string configured, is it reachable, is the schema there, and (if nothing is configured) what is
available on this machine to point at, including a stopped `hall9k-postgres` container from
an earlier session. `h9k doctor --yes` runs the same check and remediates without asking —
starts Hall9k's own Postgres via the generated compose file and creates the schema — so a
fresh install on a machine with Docker running reaches a verified install in three commands:
`h9k doctor --yes`, then a plain `h9k daemon start`, then `h9k daemon status` to confirm `h9kd`
is running. See `docs/operations.md` for the full precedence chain and the two provisioning
paths.

## Daemon operating settings

The concurrency ceiling and the model-by-role policy are durable per-machine settings, not just
environment variables (backlog 59): they load from `~/.hall9k/config.json` — the same file the
connection string above lives in, deliberately outside `bin/` so an update never touches it —
with precedence environment variable, then this file, then the built-in default. This is what
lets a daemon started by autostart (no operator shell to export anything into) still run with the
operator's own settings instead of silently falling back to defaults.

```bash
h9k config show                              # every setting, and where it came from
h9k config set --max-concurrent-task-runs 2  # the node's run ceiling (Decisions Log #111)
```

Hand-editing the file works just as well as `h9k config set`; a missing file is created (with only
what you asked to change) the first time it is needed, and it says so. See
`docs/operations.md`'s [Daemon operating settings](operations.md#daemon-operating-settings) for
the full precedence chain and every setting.

## Everyday commands, once installed

```bash
h9k doctor                 # diagnose the database situation, on demand
h9k daemon start            # launch h9kd, detached, on demand
h9k daemon status           # running or not, pid, uptime, recent log lines, and the effective settings
h9k config show             # the daemon's operating settings, and where each one came from
h9k status                  # the attention pane
h9k update                  # fetch and install the latest release
```

The full command surface is discoverable from `h9k --help` at every level — every command
carries a worked example, and a wrong invocation prints its own help back.

## Taking it off a machine

```bash
h9k uninstall                 # default: the platform goes, your data survives
h9k uninstall --purge-data    # the only path that destroys the database too
```

`h9k uninstall` takes the platform off a machine without taking the work with it. It stops
a running daemon, unregisters autostart (a macOS LaunchAgent, or a Windows logon task),
removes the PATH link, and removes everything under `~/.hall9k` that `h9k
install` itself ever wrote — `bin/`, the skill set, the Postgres compose file, the daemon's
log and pid files — and deletes `~/.hall9k` itself once that leaves it empty. `config.json`
(an operator, `h9k install` itself when nothing was configured yet, or `h9k doctor`'s
start-offer may have written it; uninstall keeps it regardless, since it is what lets a later
`h9k install` reconnect to the surviving database instead of finding nothing configured all over
again) is the one exception, and it is why a plain install-then-uninstall on a genuinely fresh
machine usually does **not** delete the whole home: install itself wrote a `config.json` naming
the default connection string, so `~/.hall9k` survives, empty of everything else install owns.
The whole home is removed only when no `config.json` ever existed there at all: a machine where
something already resolved before install ran (the environment variable or a per-project
override file, not the platform config file itself), or where Postgres was already listening on
`localhost:5432`, so install's own write never ran. A registered project's home
(`~/.hall9k/projects/<name>`, real git clones and worktrees), your credentials, and anything else
you or another tool (`h9k install` included) put there are left alone too — none of that is the
uninstall's to remove, and this command never guesses otherwise.

**Your database survives by default.** The `hall9k-postgres` Docker container is stopped,
never removed, and its data volume is never touched — the data lives in Docker, not in the
home this command deletes. Every task, run, and idea you have recorded is exactly where you
left it. Run `h9k install` again on the same machine and it reconnects to that same
database, same as it always did (`h9k doctor`'s detect-and-start flow finds the stopped
container and offers to start it, exactly as it would after a reboot).

`h9k uninstall --purge-data` is the one path that destroys the container **and** its volume
— every task, run, and idea recorded there goes with it, permanently. It names what is about
to die and asks for confirmation before doing anything; in a non-interactive session (no
terminal to answer the prompt) it refuses unless `--yes` is also given. It is the only
uninstall that destroys your recorded data — a plain `h9k uninstall` followed by a fresh
`h9k install` reconnects to the data still waiting in Docker; `--purge-data` leaves nothing
there to reconnect to. `config.json`, though, is never `--purge-data`'s to remove either: if
it exists, it survives every uninstall tier, and after a purge it may still name the
database that was just destroyed — check it before your next `h9k install` if you had one.

## Windows notes

The daemon lifecycle works the same way on Windows as on macOS — `h9k daemon start` / `stop` /
`status`, and `h9k daemon autostart enable` / `disable` — with three Windows-specific mechanics
worth knowing:

- **Autostart is a Task Scheduler logon task, never a Windows service** (Decisions Log #3): a
  service runs as a different account by default and would lose your Claude Code, git, and `gh`
  credentials, the exact problem the logon task's `InteractiveToken` principal avoids by running
  as you, the signed-in user. `h9k daemon autostart enable` registers it (`\Hall9k\h9kd` in Task
  Scheduler's library); `disable` fully unregisters it. Nothing is registered by `h9k install` or
  `h9k update` — autostart is the same explicit, separate opt-in on every platform.
- **The registration never carries `HALL9K_CONNECTION_STRING`, even when your shell has it set.**
  Every other captured variable (`PATH`, `HALL9K_CLAUDE_PATH`) travels into the registration; the
  connection string is deliberately left out, because a durable copy belongs in the platform
  config file — `h9k install` writes its own guessed default there whenever nothing else resolves
  (Decisions Log #99), and `h9k doctor`'s start-offer writes one too, but only after actually
  confirming a database came up — rather than a second, weaker plaintext copy in the launch
  script. If you configured Postgres purely by exporting the variable and `config.json`'s own
  value does not currently answer, `enable` warns you at enable time — an autostarted daemon would
  otherwise exit immediately at every logon unless `config.json`'s value comes up first. Whether
  `h9k doctor --yes` helps depends on what the variable names: doctor resolves and probes that
  value, not `config.json`'s, so it fixes this only when the variable happens to name the same
  unreachable Postgres `config.json` does — naming something else leaves `config.json` untouched
  either way. Bring up whatever `config.json` names by hand, or edit it to point at a Postgres
  that is already reachable, if doctor does not apply here. (If `config.json` has no connection
  string at all yet, doctor will not help either — the variable already resolves ahead of
  `config.json` in `Hall9kDatabase.Resolve()`, so doctor reports healthy without ever touching
  `config.json`; add `{"connectionString": "…"}` there by hand instead.)
- **`h9k daemon stop` asks gracefully rather than sending a signal**, because Windows has no
  SIGTERM for an arbitrary process the way Unix does: it writes a small stop-request file the
  running `h9kd` polls for and acts on itself. The effect is identical either way — in-flight
  event appends finish before the process exits — this is purely how the request travels.

**Docker Desktop for Postgres.** `h9k doctor` diagnoses the database exactly as it does on macOS
and Linux, offering to start Hall9k's own `docker compose` definition when nothing is reachable —
on Windows that means [Docker Desktop](https://www.docker.com/products/docker-desktop/) installed
and running (WSL 2 backend). If Docker Desktop is not installed, `h9k doctor` says so and names
the fix rather than guessing; nothing here is Windows-specific beyond having Docker Desktop itself
in place instead of Docker Engine.

## Known platform gaps

- **Start-at-login (`h9k daemon autostart enable`) is macOS- and Windows-only.** Linux otherwise
  runs the daemon fine on demand — a systemd user unit is unbuilt. Start it by hand with
  `h9k daemon start` there for now.
