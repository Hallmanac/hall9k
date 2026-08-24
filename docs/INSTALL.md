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
   writes Hall9k's own Postgres definition (not started), publishes the skill set, and puts
   `h9k` on your `PATH`.
5. Run **`h9k doctor`** — so the bootstrap ends by telling you exactly what still needs
   attention (usually: a Postgres connection string) rather than declaring victory silently.

Pass `--yes` (bash) or `-Yes` (PowerShell) to skip the consent prompt — the shape an agent
driving this file end to end should use:

```bash
curl -fsSL https://raw.githubusercontent.com/Hallmanac/hall9k/main/scripts/install.sh | bash -s -- --yes
```

## What an agent should do differently from a human

Nothing, mechanically — the bootstrap script is the same either way. The two differences:

- **Use `--yes` / `-Yes`.** There is no human at a `curl | bash` pipe's terminal to answer a
  prompt, and the scripts read from `/dev/tty` when one exists but fall back to failing
  closed otherwise (they never guess consent).
- **Read what `h9k doctor` says at the end, and act on it or report it.** A fresh machine
  almost always ends bootstrap with "no connection string configured" — that is expected,
  not a failure of install. Setting up Postgres is a separate, deliberate step (see below);
  install stays boring on purpose (Decisions Log #58).

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

`h9k` needs a Postgres connection string; nothing is provisioned automatically, and nothing
is guessed (Decisions Log #57, #58). Run `h9k doctor` any time — it is the same check,
forever, and it teaches the fix at the moment you can act on it: is a connection string
configured, is it reachable, is the schema there, and (if nothing is configured) what is
available on this machine to point at, including a stopped `hall9k-postgres` container from
an earlier session. See `docs/operations.md` for the full precedence chain and the two
provisioning paths.

## Everyday commands, once installed

```bash
h9k doctor                 # diagnose the database situation, on demand
h9k daemon start            # launch h9kd, detached, on demand
h9k daemon status           # running or not, pid, uptime, recent log lines
h9k status                  # the attention pane
h9k update                  # fetch and install the latest release
```

The full command surface is discoverable from `h9k --help` at every level — every command
carries a worked example, and a wrong invocation prints its own help back.

## Known platform gaps

- **Windows daemon lifecycle is not yet built.** `h9k install` / `h9k update` place the
  binaries on Windows today, but `h9k daemon start` / `stop` / `autostart enable` all
  refuse there with a named not-yet message — running `h9kd` on Windows is future work
  (`SLICE-1.md`'s S1-14). macOS and Linux both run the daemon on demand today.
- **Start-at-login (`h9k daemon autostart enable`) is macOS-only** even on Linux, where the
  daemon otherwise runs fine on demand — a systemd unit is unbuilt. Start it by hand with
  `h9k daemon start` there for now.
