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
| `h9k task show <id>` | One task in full: contract, dependencies, external reference, conversation, every run and its outcome. The second command of any investigation. |
| `h9k logs <id>` | A run's transcript, rendered from its stream-json (`--raw` for the stream-json itself). The log dive `h9k status` is meant to save you. |

### Ideas: capture and discovery

`h9k idea add | list | show | revise | assign | promote | discard`

Capture is one command with one argument and an optional project. Revision has no ceremony,
because nothing dispatches from an idea and there is no promise an edit could break. `promote` is
the hinge to a draft task and is the only step that requires a project.

### Tasks: development and dispatch

`h9k task add | revise | publish | assign | unassign | draft | list | show`

`add` creates a Draft. `revise` is Draft-only. `publish` is the readiness gate. `assign` is the
dispatch trigger. The path back for an edit is `unassign → draft → revise → publish → assign`.

`h9k task add` also adopts existing external work: `--from-issue 42` (or `owner/repo#42`, or a
URL) and `--from-jira PROJ-1` (or a URL). Adoption is a one-time snapshot: the title seeds the
objective, the description becomes agent context, and the state read at import is recorded as an
observation of that moment and never re-checked. Acceptance criteria are never read out of a
description; supply them with `--criteria` or at the prompt.

`--file task.md` reads a whole task from a markdown file: a minimal `---` frontmatter block
(project, type, objective, criteria, an optional model, optional blocked-by) followed by a body
that becomes the agent context. It is deliberately not YAML, since a handful of known keys does
not warrant the dependency. The numbered [`backlog/`](../backlog) files are written in that
format; the `IDEA-` notes beside them are earlier-stage prose with no frontmatter, so they are
read and authored from rather than fed to `--file`.

### Recovery

`h9k task retry | resolve | abandon` · `h9k pr resolve` · `h9k review resolve`

Five levers, and picking the wrong one loses work. [operations.md](operations.md#the-recovery-levers)
is the decision table.

### Projects, owners, connections

`h9k project add | list | show | set` · `h9k owner show | set` · `h9k connection add jira | list`

`project set` is where the verification gates, the agent model, parallelism, commit style,
context links, skip-permissions, the Jira board binding, and the review re-request policy live.
Settings resolve most-specific-wins, and the exact chain differs per setting;
[operations.md](operations.md#per-project-and-per-owner) has the two that matter.

### Jira

`h9k task push-to-jira` · `h9k task link-jira` · `h9k connection add jira` ·
`h9k project set --jira PROJ`

`push-to-jira` dispatches an agent session that writes the card in the project's own repository,
because the platform never authors one. `link-jira` reads the key back through the registered
connection before recording anything, which is what makes it safe for an agent to call.

### Install and the daemon

`h9k install` · `h9k daemon start | stop | status` · `h9k daemon autostart enable | disable`

Covered in [operations.md](operations.md#the-daemon-lifecycle).

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

Agent-facing commands are observation gates. `h9k task link-jira` reads the key back through the
connection before recording it, so what gets recorded is what Jira answered rather than what the
agent claimed. When you add a command an agent will call, that is the shape to follow.

Two commands an agent might reach for do not exist yet: `h9k ask` and `h9k answer`. The design is
settled and the events are already on the task stream, but the commands are Slice 2. An agent
that needs a decision today makes the most reasonable call and records the assumption in its
handoff. See [scope.md](scope.md).
