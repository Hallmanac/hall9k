# Hall9k documentation

The on-ramp. Four pages, each answering a different question, none of them trying to be the deep
reference: where a subject has a deep document already, these pages point into it.

Start at the [repository README](../README.md) for the pitch, a real session, and installation.
Then:

| Page | Answers |
|---|---|
| [concepts.md](concepts.md) | What are the moving parts? Tasks, runs, the lifecycle and the words the board shows, leases, verification, the pre-PR review loop, closeout. |
| [cli.md](cli.md) | What can `h9k` do, and where is the authoritative list? (The `--help` tree, and this page is the map to it.) |
| [operations.md](operations.md) | How do I run this? The daemon's lifecycle, configuration, what lands on disk, what `needs you` means, and the five recovery levers. |
| [scope.md](scope.md) | What actually works, what is designed but unbuilt, and what this project will not do. |

## The documents these point into

| Document | What it owns |
|---|---|
| [PLAN.md](../PLAN.md) | The vision, the architecture, and the **v0 Decisions Log** in §16. Every numbered decision cited anywhere in this repository lives there with its reasoning. |
| [TASK-MODEL.md](../TASK-MODEL.md) | The domain reference: event streams, aggregates, projections, the state machines, and the value-object type discipline. |
| [AGENTS.md](../AGENTS.md) | The contributor and agent guide: coding standards, git rules, the review rhythm, and the orchestrator-window role. `CLAUDE.md` defers to it. |
| [SLICE-1.md](../SLICE-1.md) | The current build breakdown with acceptance criteria per slice. |
| [HALL9K-P2P-DESIGN.md](../HALL9K-P2P-DESIGN.md) | The peer-to-peer layer: identity, discovery, NAT traversal. Design only; nothing is built. |
| [backlog/](../backlog) | One file per unstarted piece of work. The numbered ones carry an objective and acceptance criteria in the frontmatter `h9k task add --file` reads; the `IDEA-` notes beside them are earlier-stage prose. What tasks are authored from. |

## A note on how these are written

Written from practice, not intent. Command output in these pages is copied from real runs, and
where a rule exists because something went wrong, the incident is recorded alongside it. That is
a house convention rather than a flourish: a rulebook is an accumulation of documented scars, and
a reader who knows the scar knows when the rule stops applying.

If one of these pages disagrees with the code, the code is right and the page is a bug. If it
disagrees with `PLAN.md` §16, the decision log is right.
