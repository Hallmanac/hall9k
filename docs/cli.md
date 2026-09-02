# The `h9k` command surface

**The `--help` tree is the reference, and this page is the map.** Nothing here duplicates an
option list, on purpose: a duplicated one goes stale, and the copy in the terminal is the one
that is true.

```bash
h9k --help
h9k task --help
h9k task publish --help
```

- [Why help is the source of truth](#why-help-is-the-source-of-truth)
- [The map](#the-map)
- [Identifiers](#identifiers)
- [When a command line is wrong](#when-a-command-line-is-wrong)
- [Exit codes](#exit-codes)
- [Calling h9k from an agent](#calling-h9k-from-an-agent)

---

## Why help is the source of truth

The tree lives in one place, `src/Hall9k.Cli/Infrastructure/CliCommandTree.cs`, and it is treated
as a first-class interface rather than a by-product of `Main`. Every command carries a
description written in the domain language, and every command carries at least one worked
example that is a real invocation.

That is enforced, not aspirational. `CommandTreeHelpTests` walks the shipped tree and fails the
build when a command has no description, has no example, has an example that does not invoke the
command it documents, or has an example with unbalanced quotes. Descriptions cite the decision
that produced the behaviour (`PLAN.md §4`, `Decisions Log #34`), so the help teaches rather than
labels.

There is a second reason the examples have to be real: they are what a caller who got the
invocation wrong reads back. A command line that never reaches a command is answered with the
failure plus that command's own help, so one bad invocation costs one command rather than a
search.

For an agent, this means: **read the help rather than guessing at a flag.**

## The map

One line per branch. Ask `--help` for the rest.

### The daily loop

| Command | What it is for |
|---|---|
| `h9k status` | The attention pane: what needs you, what has gone quiet, what is running. Bounded on purpose. |
| `h9k task show <id>` | One task in full: contract, dependencies, external reference, conversation, every run and its outcome, each run's own gate wall-clock durations, and a flag when one materially exceeds the project's recent recorded average for that gate. The second command of any investigation. |
| `h9k logs <id>` | A run's transcript, rendered from its stream-json (`--raw` for the stream-json itself). The log dive `h9k status` is meant to save you. |

### Ideas: capture and discovery

`h9k idea add | list | show | revise | assign | promote | discard`

Capture is one command with one argument and an optional project. Revision has no ceremony,
because nothing dispatches from an idea and there is no promise an edit could break. `promote` is
the hinge to a draft task and is the only step that requires a project.

### Tasks: development and dispatch

`h9k task add | revise | set-session-cap | set-review-caps | publish | assign | unassign | draft | list | show`

`add` creates a Draft. `revise` is Draft-only. `publish` is the readiness gate. `assign` is the
dispatch trigger. The path back for an edit is `unassign → draft → revise → publish → assign`.
`set-session-cap <id> <cap>` overrides how many agent sessions this task's own run may hold
simultaneously — settable any time, even mid-run — in place of the node's global default.
`set-review-caps` overrides the node's compiled review-cycle-cap defaults for one task —
settable at any time, even while the task's run is live, so it doubles as the takeover lever
for a grinding run. See [operations.md](operations.md#daemon-operating-settings).

`h9k task add` also adopts existing external work: `--from-issue 42` (or `owner/repo#42`, or a
URL), `--from-jira PROJ-1` (or a URL), and `--from-pr` (a pull request number, `owner/repo#42`, or
a URL) — the last of which adopts a pull request to review rather than build, always as a
`pr-review` task (§ below). Adoption is a one-time snapshot: the title seeds the objective, the
description becomes agent context, and the state read at import is recorded as an observation of
that moment and never re-checked. Acceptance criteria are never read out of a description; supply
them with `--criteria` or at the prompt.

`--file task.md` reads a whole task from a markdown file: a minimal `---` frontmatter block
(project, type, objective, criteria, an optional model, optional blocked-by, optional epic)
followed by a body that becomes the agent context. It is deliberately not YAML, since a handful
of known keys does not warrant the dependency. The numbered [`backlog/`](../backlog) files are
written in that format; the `IDEA-` notes beside them are earlier-stage prose with no
frontmatter, so they are read and authored from rather than fed to `--file`.

### Pull-request review

`--from-pr` creates a read-only `pr-review` task: the node pulls the pull request into a detached
worktree, runs an adversarial-weighted independent review over it (never a build or a fix), and
parks a findings report for the owner to walk — `h9k review resolve <id> --merge-ready` once every
finding has been directed, since a pr-review task has no diff of its own for `--needs-fixes` to
act on. Nothing is ever posted to the pull request without an explicit human go: the
`walk-pr-review-findings` skill is what walks the report and posts on direction, always under the
owner's own login. The task completes without any merge ever being observed — there is no pull
request of this task's own to merge.

### Epics: naming a family of tasks

`h9k epic add | list | show | link-jira | close`

An epic is a first-class named grouping of tasks (Decisions Log #100): its own id, title, and
Open/Closed state. Membership is optional and no-ceremony — a task joins or leaves at
`h9k task add --epic` / `h9k task revise --epic`/`--clear-epic` rather than through an epic
command — and must belong to the same project as the epic and target an Open one; a closed or
another project's epic is refused. `close` is the only way an epic ends, always an explicit human
act with a reason; there is no `reopen` yet, and nothing closes an epic automatically, including
its last member task closing out. `link-jira` is identity-only: a key or URL stored verbatim,
never read from or written to Jira — unlike a task's own `link-jira`, which reads the key back
through the registered connection before recording it. `h9k task list --epic <id>` filters to one
epic's member tasks; `h9k epic show <id>` answers the same give-me-all-tasks-in-this-epic question
with each member's state already composed.

### Working a task interactively

`h9k task work | verify | deliver | handback | release`

An operator can work a Queued task in their own terminal instead of dispatching it headless:
`work` claims it, cuts the same branch and worktree headless dispatch would, assembles the prompt
through the same code path (its working rules swapped for an attached operator), and opens a
regular interactive Claude Code session — the claim is held by the human, not a process, so
closing the terminal is a normal way to leave and re-running `work` re-enters the same worktree.
From there, `verify` runs the project's gates on demand, `deliver` pushes the branch and hands the
claim into the standard delivery pipeline, `handback` releases the claim to a headless agent
partway through (resuming the branch), and `release` gives an untouched claim back to the queue.

### Recovery

`h9k task retry | resolve | abandon` · `h9k pr resolve` · `h9k review resolve`

Five levers, and picking the wrong one loses work. [operations.md](operations.md#the-recovery-levers)
is the decision table.

### Projects, owners, connections

`h9k project add | init | list | show | set` · `h9k owner show | set` ·
`h9k connection add jira | list`

`project add` registers a project **and creates its home directory**; `project init` is the same
recipe for a project that has none yet, and the repair path for one that is incomplete. See
[the project home](#the-project-home) below.

`project set` is where the verification gates, the agent model, parallelism, commit style,
context links, skip-permissions, the Jira board binding, the backlog policy (`--backlog
none|github-issues|jira`) and its routing guidance, the review re-request policy, the
project-level review-cycle-cap overrides, and the home's location live.
Settings resolve most-specific-wins, and the exact chain differs per setting;
[operations.md](operations.md#per-project-and-per-owner) has the two that matter.

### The project home

Every project owns a directory on disk, `~/.hall9k/projects/<name>` unless the project says
otherwise, in the same shape on every machine:

```
<home>/
├── AGENTS.md   generated from the project's facts; never hand-maintained
├── repo/       <name>.git (bare clone) · dev/ (a worktree on the primary branch) · wt-*/
├── ideas/
├── tasks/      _archive/ holds terminal tasks (closed out or abandoned); moved back if reopened
├── skills/     plain markdown skill docs, seeded from the install's canonical set
└── .claude/    generated Claude Code plumbing: skills/ symlinked, never copied
```

Creating it is platform code end to end: the directories, the bare clone with its fetch refspec
corrected, the `dev/` worktree, the skill seeding, and the rendered `AGENTS.md`. There is no agent
in it, and every step is idempotent, so re-running reports what was already there rather than
starting over. Point an editor at the home and you browse the code, the worktrees, the tasks and
the ideas together; start a session there and its `AGENTS.md` tells it the rest.

Every task gets its own directory under `tasks/` (`<shortid>-<slug>/`, named from the objective),
holding `task.md` — the same frontmatter-plus-context format `h9k task add/revise --file` reads —
and a `workspace/` for whatever refinement material accumulates. An idea with a project renders
the same way under `ideas/`. Drafts render exactly like published tasks: the file is where the
thinking lives, from the first keystroke. The daemon keeps every file in sync with the store on
its own sweep — nothing needs to be told to re-render — and the render is one-way: edit the file,
then apply the edit with `h9k task revise <id> --file <path>` (`h9k idea revise <id> "<text>"` for
an idea, which has no `--file` form). A direct edit that is never applied is silently overwritten
the next time the daemon sweeps, which the file's own header line says.

The same sweep moves a task's whole directory into `tasks/_archive/` the moment it goes
terminal — true closeout (merged, and the closeout monitor observed it) or abandoned — and moves
it back out if it is ever reopened. `_archive`'s leading underscore sorts it to the top of an
editor's file explorer, ahead of every live task, so the one folder everything finished sorts
into is out of the way at a glance rather than interleaved with what still needs attention.

Going from nothing to a working project directory on a second machine:

```bash
h9k install                                              # binaries, PATH, canonical skills
h9k project add --name <name> --repo-url <git-url>       # register and materialise
h9k project show <name>                                  # the home is the first row
```

For a project this database already knows about:

```bash
h9k project init <name>                     # create (or repair) the home at the default location
h9k project init <name> --home <path>       # …or somewhere you choose
```

`project init` always materialises `repo/` fresh from the recorded remote. A clone elsewhere on
the machine is inconsequential (git is distributed, including across one disk), and once the
clone is in place the project is re-pointed at it so dispatch cuts worktrees there.
`--keep-repo-path` holds that off while work is still live under the old path.

It is also how `repo/dev` catches up. That worktree is what a person reads code in and what the
platform spawns a reading session into, so running `project init` again fetches and fast-forwards
it and reports what it found: up to date, moved forward by so many commits, or left exactly as it
is because local changes or a diverged branch blocked the fast-forward. It is never a reset;
whatever is uncommitted there stays, and the step names the commit the checkout is serving so a
stale one is something you were told about rather than something you find out from a card written
by last quarter's rules.

The location is a setting (`--home`, or `h9k project set <name> --home <path>`); the shape inside
it is the contract, which is what lets a dispatched agent be handed paths rather than sent
hunting for them.

### Backlog tracking

`h9k project set --backlog none|github-issues|jira` · `h9k project set --backlog-routing "<TEXT>"` ·
`h9k task link-issue <task> <issue>`

Every published task is tracked in the project's backlog automatically, per this setting — but
`h9k task publish` checks the policy before it ever creates anything: a draft that carries no
linked item yet, and has no publication already pending, is refused (exit code 70) until a human
or orchestrator links an existing item (`h9k task link-issue` / `h9k task link-jira`), attests
none exists and proceeds with `h9k task publish <id> --no-existing-item`, or attests that this
task should skip tracking altogether with `h9k task publish <id> --untracked` — for internal
chores and platform tasks that should not pollute a team's tracker — so a duplicate is never
minted from a search the platform itself cannot perform. The two attestations are refused
together as contradictory; `--untracked` under backlog policy `none`, or any policy this build
doesn't recognize, is refused as meaningless; and `--untracked` on a task with a publication
request already outstanding (`h9k task push-to-jira`, run by hand while still a Draft) is refused
too, since that session mints its card regardless of the flag. Once that gate is past, `github-issues`
is deterministic: `h9k task publish` runs `gh issue create` itself — no agent involved, because an
issue's shape (title, body, labels) is uniform enough for the platform to author on its own —
reads the created issue straight back the same way `--from-issue` does, and records it through
`link-issue`, the same observation-gate pattern `link-jira` uses. `--backlog-routing` is read as a
comma-separated label list under `github-issues`. `jira` is agent-mediated (below); `none` is
today's default, unchanged behavior. A task adopted with `--from-issue`/`--from-jira` already
carries its reference, so the gate never fires for it.

### Jira

`h9k task push-to-jira` · `h9k task write-jira` · `h9k task link-jira` · `h9k connection add jira` ·
`h9k project set --jira PROJ`

`push-to-jira` dispatches an agent session that composes the card payload in the project's own
repository, because the platform never authors a card's *content* — issue types, required fields,
and routing rules are the organisation's configuration. The session runs in `<home>/repo/dev`,
since the recorded repository path of a project with a home names the bare clone and a bare clone
has no files to read; a project registered before homes existed still points at an ordinary
checkout, and that is where its session runs. That session makes no Jira call itself: it submits
the composed payload through `write-jira`, which is the sole executor of every Jira write
(Decisions Log #102). `write-jira` validates the payload (a transition or a close is refused
regardless of who composed it), records the intent before anything is sent, executes it through
the Atlassian CLI (`twg`), and verifies by reading the item back before recording the outcome — the
same observation-gate pattern `link-jira` uses for a pre-existing card. A project set to `--backlog
jira` makes the publication request automatically at publish — once the dedup gate above lets the
publish through — so `push-to-jira` becomes the manual retry lever for a project that had no Jira
connection registered yet when it was first published, and also the way to get a card started by
hand before publishing, which the gate recognizes as a publication already pending rather than
demanding an attestation for it.

### Install and the daemon

`h9k install` · `h9k update` · `h9k uninstall [--purge-data]` · `h9k daemon start | stop | status` ·
`h9k daemon autostart enable | disable`

`uninstall` takes the platform off the machine — binaries, PATH link, autostart, and everything
else `install` itself wrote under `~/.hall9k` — but leaves a registered project's home,
credentials, and the `hall9k-postgres` Docker container's data volume untouched by default, so a
later `install` reconnects to it. `--purge-data` is the only path that destroys the volume too,
and it asks first.

Covered in [operations.md](operations.md#the-daemon-lifecycle) and [INSTALL.md](INSTALL.md).

### The daemon's operating settings

`h9k config show` · `h9k config set`

The node ceiling (`--max-concurrent-task-runs`, counted directly in task runs), the per-run
session cap's global default (`--session-cap-per-run`, overridable per task with
`h9k task set-session-cap` even mid-run), the per-role model overrides, the interactive-claim
staleness threshold, the node-level review-cycle caps (compliance, adversarial, final-full-pass,
and the task-lifetime budget — each overridable per project, and per task too via
`h9k task set-review-caps`), and a periodic token-spend budget (`--spend-budget <tokens|none>`
paired with `--spend-period <day|week>`, backlog: spend-governor step three, Decisions Log #113) —
once the current period's recorded spend reaches the budget, the dispatcher declines to claim
further queued work until the period rolls, gating claims only and never touching work already
claimed; `--spend-budget none` clears it back to unbudgeted, the only one of these settings with a
real way back once set, since "no budget" has no compiled default number the way the review caps
do. Every one of these is durable in the platform config file so a fresh machine or an autostarted
daemon runs with the operator's settings without an environment variable ritual, and every one
except the interactive-claim staleness threshold takes effect only on the daemon's next start —
`h9k status`'s own Queued section names a stopped concurrency or spend gate honestly, but only for
whatever a running daemon last confirmed, so raising a spent budget still needs a restart before
the queue moves again. `show` resolves and names each setting's origin (environment variable,
config file, or built-in default); `set` merges a change into the file. See
[operations.md](operations.md#daemon-operating-settings).

## Identifiers

**Tasks and ideas** take the full identifier **or an unambiguous fragment of it**. A fragment is
matched against either *end* of the identifier, its leading characters or its trailing ones, and
never against the middle: ids are UUIDv7, so they share a time-ordered prefix and it is the tails
that tell them apart. In practice you paste the eight characters the board printed, which are the
last eight:

```bash
h9k task show 28b19893
```

An ambiguous fragment is refused rather than resolved by guess, and the refusal says how many
things it matched so you know to use more characters.

**Projects and owners** are named, not fragment-matched on their id. An id has to be the whole
thing, because it is parsed as a UUID rather than compared as text, so `h9k project show 4a9e2088`
is refused even when exactly one project's id ends that way; paste the full id, or use the name.
The name resolves exact first and fragment second: `h9k project show hall9k` is never ambiguous
with a `hall9k-docs` registered alongside it, because the exact name wins outright. The fragment
pass only runs when nothing matched exactly, and it is a plain substring match, so with both of
those registered `h9k project show hall` matches neither exactly, matches both as a fragment, and
is refused as ambiguous. An owner matches on email as well as name, and `h9k owner show` with no
argument at all is correct on an install with one owner.

## When a command line is wrong

A wrong invocation does not produce a stack trace. It produces the failure, on stderr, followed
by that command's own help, at exit 64. Failures inside a command follow the same rule: they say
*why* on stderr and quote the relevant rule, so a caller (or an agent) can self-correct from the
message alone.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Something else went wrong (including an unreachable Postgres, which says so and names the fix) |
| 64 | The command line was wrong, or a value failed validation |
| 66 | Not found |
| 69 | A conflict, such as an optimistic-concurrency loss |
| 70 | A business rule refused the operation |

64, 66, 69, and 70 are sysexits values, and each is carried by its own domain exception type
rather than inferred from a message.

## Calling h9k from an agent

The CLI is designed to be called by headless sessions mid-run, which is why it stays thin: it
opens a lightweight database session, does its work, and exits. There is no Wolverine host to
cold-start on every invocation.

Two things follow for anything scripting it. First, `h9k` works while the daemon is down: every
command lands in Postgres, and reads never need the daemon at all. Second, there is no
synchronous request and response with the daemon by design, so a command that triggers work
returns once the work is *recorded*, not once it is *done*.

Agent-facing commands are observation gates. `h9k task write-jira`, the sole executor of every
Jira write, verifies by reading the item back with its own follow-up `twg` call before recording
the outcome, so what gets recorded is what twg answered rather than what the agent's own claim
was — it loads the registered Jira connection to tell every `twg` call which tenant to target
explicitly, the same strict lookup `h9k doctor` uses, refusing rather than guessing when more
than one is registered.
`h9k task link-jira` reads the key back through the connection before recording it, so what gets
recorded is what Jira answered rather than what the agent claimed. `h9k task link-issue` is the
same gate for GitHub — read back through `gh` before recording — and the platform's own
`gh issue create` claim goes through it exactly as an agent's would. When you add a command an
agent will call, that is the shape to follow.

Two commands an agent might reach for do not exist yet: `h9k ask` and `h9k answer`. The design is
settled and the events are already on the task stream, but the commands are Slice 2. An agent
that needs a decision today makes the most reasonable call and records the assumption in its
handoff. See [scope.md](scope.md).
