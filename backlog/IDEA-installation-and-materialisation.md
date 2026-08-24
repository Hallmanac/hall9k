> **ARCHIVED 2026-08-23: absorbed into tasks 42 (release delivery) and 47 (project home foundations, which took 43's materialisation recipe). The sitting addendum below records the rulings; this file is the origin record.**

# IDEA: Installation, delivery, and repo materialisation

**Status:** Draft — funnel entry
**Origin:** Design session, 2026-08-22 (voice)
**Builds on:** `backlog/12-daemon-install.md` (S1-12), `backlog/00-windows-ci.md`,
`HALL9K-P2P-DESIGN.md` (lazy materialisation)
**Would become:** next entries in the Decisions Log (#61+)

## What this is

Hall9k has only ever run on one machine — Brian's MacBook — built from source and
published locally. The goal is a **second node on Windows**, dogfooding the platform as an
ordinary user of it would: no repo checked out by hand, no dotnet SDK assumed, no
insider knowledge of the layout.

That exposes three gaps, in order of dependency: getting the binaries onto the machine,
installing them, and getting the machine able to do work in a project's repo.

## What is already done

S1-12 covers more than expected and should not be rebuilt:

- binaries land in `~/.hall9k/bin`, with `h9k` on PATH
- **no background service is registered at install** — nothing autostarts as a side effect
- `h9k daemon start` / `stop` / `status`, detached properly, single-instance guarded
- `h9k daemon autostart enable` / `disable` as strictly opt-in
- re-running install after a merge republishes idempotently and offers the restart
- `h9k status` states plainly when the daemon is not running

**The Windows deferral in S1-12 is stale.** That file says the Windows startup task is
deferred to S1-14 with a not-yet message; it has since been implemented. `autostart enable`
registers a Windows startup task on Windows and a launchd LaunchAgent on macOS. Whoever
picks this up should not re-litigate it, and the S1-12 file should be corrected.

> **Correction (2026-08-23, backlog 42):** this claim did not hold up against the code.
> `DaemonAutostart.ForCurrentPlatform()` still returns a deferred, not-yet implementation on
> Windows, and `SLICE-1.md`'s S1-14 still lists a Windows daemon lifecycle as unbuilt,
> dispatched-later work. Backlog 42 left `backlog/12-daemon-install.md`'s deferral text as
> written rather than rewrite it to match this paragraph. See PLAN.md Decisions Log #75.

## Gap 1: delivery

What S1-12 assumes is that you are **building from source on the machine**. Correct for the
MacBook; wrong for a machine that should never hold the Hall9k repo.

So the missing half is CI. There is only `ci.yml` today — no release workflow.

- On a tag, build per-platform binaries for `h9k` and `h9kd` (macOS arm64, Windows x64 at
  minimum).
- Publish them as a GitHub release artefact.
- The install path **fetches** rather than builds.

Note the platform matrix is a build-and-publish concern, not a code concern — the autostart
work above already split the platform-specific behaviour.

## Gap 2: the install command on a bare machine

Install today is effectively "you already have the repo, so build and symlink." On a fresh
Windows machine there is a bootstrap problem: something has to place `h9k` before `h9k`
exists to place itself.

That is a small script — fetch the latest release for the platform, unpack to
`~/.hall9k/bin`, put it on PATH — after which the existing idempotent republish path takes
over for updates.

Docker Compose for Postgres works identically on both platforms, so that half needs
nothing new.

## Gap 3: repo materialisation

This is the one that actually unblocks the second machine doing work.

Today `h9k` assumes it is standing **in** the repo — the working directory is the project.
On a fresh machine you would clone by hand and point Hall9k at it. `HALL9K-P2P-DESIGN.md`
already names the better shape: a **materialisation step** ahead of work-tree creation. Do
I have this repo? No — clone it, then carry on as normal.

**This does not need P2P.** The project already knows its GitHub remote, so the clone is
just a clone. It stands alone and could be built now.

### The bare-clone refspec

Hall9k uses git worktrees, so the natural base is a bare clone. The trap is that a default
bare clone fetches branches straight into local refs — the repo behaves like a server node,
with remote branches appearing as if they were yours.

The fix is the **fetch refspec**: map remotes into `refs/remotes/origin/` as an ordinary
clone does. One line of config, after which `--no-track` branch creation, remote branch
fetching, and pushes all behave the way they do in a normal checkout.

So the recipe is: bare clone → correct the refspec → fetch → it is a proper worktree base.

### This is a recipe, not an agent

Considered and rejected: having an AI agent drive the setup to make the interaction
friendlier.

It does not earn its place. The only real input is "where do you want this," which is a
prompt with a sensible default — under `~/.hall9k/repos/`, keyed by project — not a
conversation. An agent makes something that should be instant and byte-identical every time
slower and less predictable.

The instinct behind it is right, though: it should not feel like a wall of git incantation.
That is good CLI design — a sensible default, clear `--help`, and one plain line about what
it did. **Save the agent for judgment; this one is a recipe.**

## Draft acceptance criteria

- A tagged commit produces a GitHub release carrying `h9k` and `h9kd` binaries for macOS
  arm64 and Windows x64.
- A one-line bootstrap install fetches the latest release for the current platform, places
  the binaries in `~/.hall9k/bin`, and puts `h9k` on PATH — on both platforms, with no repo
  and no dotnet SDK present.
- Installing registers no background service and no autostart, consistent with S1-12.
- After bootstrap, the existing S1-12 update path (`install` republishing idempotently)
  works against released artefacts rather than a local build.
- `h9k` materialises a project's repo on demand: bare clone from the project's configured
  remote into a default path under `~/.hall9k/repos/`, overridable by flag or prompt.
- The materialised clone's fetch refspec maps remote branches into `refs/remotes/origin/`;
  creating a branch from `origin/main`, fetching remote branches, and pushing all behave as
  in an ordinary checkout.
- Materialisation is idempotent — running it against an already-materialised project
  reports and exits rather than re-cloning.
- Worktree creation works against the materialised bare repo without further setup.
- `backlog/12-daemon-install.md` is corrected to remove the stale Windows autostart
  deferral.
- A second machine can go from nothing to claiming and completing a task using only
  documented commands.
- `dotnet build` and `dotnet test` pass.

## Open questions

- Does materialisation happen implicitly on first claim, or explicitly via a command? (P2P
  design implies lazy-on-claim; explicit is easier to debug first.)
- Private repos need credentials on the second machine. Does Hall9k assume an existing git
  credential helper, or does it own that?
- Windows path handling for worktrees — is the long-path limit a real risk under
  `~/.hall9k/repos/<project>/<worktree>`?

## Addendum (orchestrator sitting, 2026-08-22): the fourth gap, ruled

The doc's three gaps get binaries and a repo onto the machine; the load-bearing
fourth is WHICH EVENT STORE the second node talks to - without it there is no
task to claim. Ruled by Brian: **Option A - one shared Postgres, with the tower
as its host** once the tower is up (the always-on machine is the durable center;
the MacBook connects to it). The trade-off is decision #57's documented one: a
connecting node is not local-first while the host is away. P2P replication later
replaces the shared connection without changing the mental model. The mechanism
is backlog 24's connection-string precedence (env var, then platform config);
tower hosting lands as operations documentation when the tower arrives.
Recorded as a decisions-log entry with the task that builds it.

Resolutions to the doc's open questions (same sitting):
- Materialisation is an EXPLICIT command first; lazy-on-claim arrives with the
  P2P shape once the explicit path is debugged.
- Credentials: assume the git credential helper, set up via gh auth setup-git -
  gh carries the machine's own credentials, consistent with decision #60's stance.
- Windows long paths: low risk under ~/.hall9k/repos/; the materialise recipe
  sets core.longpaths and Windows CI proves it.

Also surfaced: the prerequisite list is bigger than git - gh AUTHENTICATED (the
daemon pushes and opens PRs through it), Docker, and the claude CLI logged into
the subscription (agents run claude -p in Subscription mode). The preflight that
checks each tool and its auth state belongs to backlog 24's doctor, not a new
surface. Skills travel with the repo clone, so materialisation covers each
project's own skills; the platform-tier skill gap stays with IDEA-skill-layer
tension 8.
