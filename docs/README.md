# Hall9k documentation

The on-ramp. Five pages, each answering a different question, none of them trying to be the deep
reference: where a subject has a deep document already, these pages point into it.

Start at the [repository README](../README.md) for the pitch, a real session, and installation.
Then:

| Page | Answers |
|---|---|
| [concepts.md](concepts.md) | What are the moving parts? Tasks, runs, the lifecycle and the words the board shows, leases, verification, the pre-PR review loop, closeout. |
| [cli.md](cli.md) | What can `h9k` do, and where is the authoritative list? (The `--help` tree, and this page is the map to it.) |
| [operations.md](operations.md) | How do I run this? The daemon's lifecycle, configuration, what lands on disk, what `needs you` means, and the eight recovery levers. |
| [scope.md](scope.md) | What actually works, what is designed but unbuilt, and what this project will not do. |
| [INSTALL.md](INSTALL.md) | How does a bare machine, with no repo checkout and no .NET SDK, get `h9k` installed and kept current? |
| [getting-started.md](getting-started.md) | What do the first hour, the first week, and the first problems actually look like, once a project is registered? |

## The documents these point into

| Document | What it owns |
|---|---|
| [PLAN.md](../PLAN.md) | The vision and the architecture. Its §16 Decisions Log became platform data (idea d805fd8b): every entry was imported into the decision store keeping its own number as a legacy citation, and the section now points at the rendered `decisions.md`. |
| `decisions.md` | Not a file in this repository: the daemon renders it from the decision store into the project home and into every dispatched worktree. Every binding decision lives there, including the git and working rules AGENTS.md used to carry. `h9k decide list` reads the same records from anywhere. |
| [TASK-MODEL.md](../TASK-MODEL.md) | The domain reference: event streams, aggregates, projections, the state machines, and the value-object type discipline. |
| [AGENTS.md](../AGENTS.md) | The contributor and agent guide: what the project is, build/test/run, the coding standards, and the CLI command standards, kept short so a dispatched session's context stays cheap. Its git rules and working agreements are decisions in the store now, and the two sections point there. `CLAUDE.md` defers to it. |
| [ORCHESTRATOR-WINDOW.md](../ORCHESTRATOR-WINDOW.md) | The orchestrator-window role: the review rhythm, the recovery levers, the needs-you relay. Only an interactive session loads it; a headless dispatched session never does. |
| [SLICE-1.md](../SLICE-1.md) | The current build breakdown with acceptance criteria per slice. |
| [HALL9K-P2P-DESIGN.md](../HALL9K-P2P-DESIGN.md) | The peer-to-peer layer: identity, discovery, NAT traversal. Design only; nothing is built. |
| [backlog/](../backlog) | The dogfood-era archive: one file per pre-cutover piece of work. The numbered ones carry an objective and acceptance criteria in the frontmatter `h9k task add --file` reads; the `IDEA-` notes beside them are earlier-stage prose. New work is captured with `h9k idea add` / `h9k task add` and renders into the project home instead (backlog 48). |

## A note on how these are written

Written from practice, not intent. Command output in these pages is copied from real runs, and
where a rule exists because something went wrong, the incident is recorded alongside it. That is
a house convention rather than a flourish: a rulebook is an accumulation of documented scars, and
a reader who knows the scar knows when the rule stops applying.

If one of these pages disagrees with the code, the code is right and the page is a bug. If it
disagrees with a decision in `decisions.md`, the decision is right.
