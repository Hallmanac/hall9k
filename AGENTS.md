# Hall9k — Agent & Contributor Guide

Canonical guidance for anyone (human or agent) working in this repo. `CLAUDE.md` defers here.

## What this is

Hall9k is a local-first agentic workflow platform: `h9k` CLI + `h9kd` daemon + Postgres,
orchestrating detached Claude Code agents. **An interactive session in this repo is the
orchestrator window**: read `ORCHESTRATOR-WINDOW.md` before anything else, because it is the role
you are in. A *headless* dispatched session is not one, never loads that file, and should read on
here instead. Then, in order of need:

- `decisions.md` — **the binding decisions**, including the git and working rules this file used to
  carry, rendered from the decision store into this project's home and into every dispatched
  worktree (`h9k decide list` reads the same thing from anywhere). `lessons.md` is its companion,
  holding what runs have learned. Neither is ever edited or committed
- `PLAN.md` — vision and architecture (§16, the hand-maintained decisions log, became the store above)
- `TASK-MODEL.md` — the domain model: streams, events, aggregates, type discipline
- `SLICE-1.md` — the current build breakdown and acceptance criteria
- `HALL9K-P2P-DESIGN.md` — the peer-to-peer layer: identity, discovery, NAT traversal. The network half (mDNS, hole punching, a relay, QUIC) is design only; the identity half shipped instead, over the existing git ledger (Decisions Log #38-#58)
- `README.md` + `docs/` — the newcomer's on-ramp (concepts, CLI map, operations, and `docs/scope.md`,
  which is the honest works-today / designed-but-unbuilt / never-doing inventory). Written from this
  file and the five above, so when behaviour changes here, `docs/` is downstream and needs the edit too.
- **Doctrine:** Hall9k is prescriptive about the lifecycle and permissive about judgment; the template layer (prompt prose as shipped markdown templates) and the skill layer are the judgment side, the parsed contracts stay in code. Detail: [docs/concepts.md](docs/concepts.md#the-judgment-layer-templates-and-skills).

## Build / test / run

```bash
dotnet build                 # full solution (Hall9k.slnx)
dotnet test                  # unit + integration tiers (integration needs Docker for Testcontainers)
dotnet run --project src/Hall9k.AppHost    # dev loop: Postgres + daemon + Aspire dashboard
docker compose up -d         # Postgres only (installed mode / manual runs)
./src/Hall9k.Cli/bin/Debug/net10.0/h9k     # the CLI binary after build
```

`dotnet test` with no filter is this project's own host-coupled gate: it runs only in the
daemon's own serialized host gate, never as a bare command a session runs directly here — the
one exception is a single touched test class, scoped by name (e.g. `dotnet test --filter
"FullyQualifiedName~ThatClass"`), never the gate's own full command.

Every other `h9k`/`h9kd` command — install/update/uninstall, project/task/idea/epic lifecycle,
Jira/GitHub backlog integration, branch templates, auto-pr-review — is documented in the
`hall9k-cli-reference` skill; load it on demand rather than assuming a dispatched session was
pre-briefed on the whole CLI surface.

CI runs build + test on ubuntu and windows for every push/PR to main.

## Coding standards

**Types**
- **Seal by default.** Every class and record is `sealed` unless something genuinely inherits
  from it. Applies to test classes too.
- **Value objects over primitives and enums** — the full discipline with anatomy is
  TASK-MODEL.md §8. Closed vocabularies are sealed records with static instances and an
  `Unknown` sentinel; enums only for unpersisted in-process outcomes.
- Events: `public sealed record`, past-tense `NounVerbed`, one per file, positional style.
- Aggregates: `public sealed class`, `Guid Id`, private setters, state changes only via
  `Apply(Event @event)`; no business logic in the aggregate (deciders own it).
- IDs: UUIDv7 via `Uuid.NewDatabaseFriendly(Database.PostgreSql)` (UUIDNext). Never `Guid.NewGuid()`.
- Timestamps: `DateTimeOffset`, carried explicitly on events.
- **Spell out acronyms** in type/method/property names (`PullRequestOpened`, not `PrOpened`);
  ubiquitous ones (`Api`, `Url`, `Id`) are fine. Parameters may abbreviate.

**Style**
- File-scoped namespaces; namespaces mirror folders exactly.
- Explicit types when the right-hand side is a method call; `var` only when the type is apparent.
- `switch` expressions and pattern matching over `if`/`else if` chains.
- Every new async method takes a `CancellationToken` (last parameter); always pass one when a
  method accepts one.
- No null-forgiving `!` where a prior check or contract guarantees non-null; no unused usings.

**Layout**
- Vertical slices: `Hall9k.Domain/Features/{Feature}/`. Big slices (Task, Run, Project) use
  `Commands/ Events/ Handlers/ Queries/ Projections/ Documents/` subfolders; tiny slices (Owner, Node, Connection, Idea) stay flat.
- Reference graph: `Cli → Domain + Connectors` · `Daemon → Domain + Connectors + ServiceDefaults`
  · `Connectors → Domain`. Domain references no Hall9k project. The CLI never hosts Wolverine.
- Packages: pinned centrally in `Directory.Packages.props` (transitive pinning on). Add
  versions there, never in a csproj.

**Tests**
- Two tiers in the main project (`tests/Hall9k.Tests`): unit (DB-free — aggregates via `Apply`,
  projections via a `FakeEvent<T>` stub) and integration (Testcontainers Postgres). A second,
  minimal project, `Hall9k.Tests.LockHolder`, exists solely as a standalone executable
  `CrossProcessContainerGateTests` can launch and kill to prove permit reclaim against a real
  process death (Decisions Log #132) — it carries no tests of its own and is not a third tier.
- xUnit + FluentAssertions.

## CLI command standards

The `--help` tree is how agents (and humans) discover what h9k can do — treat it as a
first-class interface, always, for every command:

- Every command gets `.WithDescription(...)` and every option gets a `[Description]` that
  speaks the domain language (point at the readiness contract, PLAN.md sections, etc. —
  the help should *teach*, not just label).
- Every command gets at least one `.WithExample(...)` showing a realistic invocation.
- Failures print *why* on stderr with the relevant rule quoted (see the DomainException →
  exit-code mapping in Program.cs) — an agent must be able to self-correct from the message.

## Git rules

The git rules this section carried are decisions in the store now (idea d805fd8b): commit
authorship and the no-trailer rule, what an agent may and may not post into a review thread,
branch naming and stacked branches, the four-gate check before a hand merge, PR branches as
authored history, and who pushes. Read them in **`decisions.md`**, rendered into this project's
home and into every worktree at dispatch, or with `h9k decide list` from anywhere. Record a new
one with `h9k decide "<one claim>"` rather than editing this file, and give it the concrete
incident behind it with `--origin`.

## Repo skills

Repo-resident Claude skills live in `.claude/skills/` and are available in every worktree:

- **absorb-review-fixes** — fold review-feedback fixes into their owning commits (fixup +
  autosquash + tree-identity check) so the PR branch stays authored history
- **commit-plan** — organize the working tree into cohesive, buildable commits ordered for PR review
- **resolve-review-threads** - triage every unresolved review thread on an existing PR,
  whoever opened it, before any fix: fix, decline with evidence, or route; reply and
  resolve per disposition and author kind (§16 #62, #159)
- **rebase-onto-main** — bring a PR branch conflicting with its base current: replay its own
  commits onto the moved base, resolve conflicts with judgment, never leave a conflict marker,
  re-run the gates against the rebased tree. The inverse of absorb-review-fixes (backlog 44)
- **pr-summary** — write the `PR SUMMARY:` block a build session closes with, which the daemon puts
  verbatim into its pull request (§16 #163); a repo's own rule wins for the prose
- **walk-pr-review-findings** — walk a pr-review task's findings report with the owner, finding by
  finding, and post only what they direct (a batched GitHub review or a plain comment) on their
  explicit go, under their own login. Use once such a task (§16 #99) parks NeedsHuman with one; an
  owner reviewing the pull request themselves runs `h9k pr review` (§16 #149)
- **hall9k-cli-reference** — the full `h9k`/`h9kd` command surface and platform domain semantics
  (task/idea/epic lifecycle, Jira/GitHub backlog integration, branch templates, auto-pr-review);
  load on demand rather than assuming a session was pre-briefed on the whole CLI surface
- **orchestrator-recipe-generator** — write or regenerate a node's or project's orchestrator
  recipe (`recipes/orchestrator.md` and the scoped-session recipes) against the platform's own
  contract; the platform alone renders `recipes/launch-anchor.md` and `recipes/settings.json`,
  always overwritten, and the skill never writes either one (Decisions Log #147, #155)

There is deliberately no create-pr skill: PRs are opened by the daemon (`PullRequestOpener`),
never by agents.

Skills sit in three tiers, least specific first (Decisions Log #76). The **install** owns the
canonical set at `~/.hall9k/skills`, published from this directory by `h9k install --repo`, or from
a release payload's bundled `skills/` by `h9k install --from-release` and `h9k update` — the same
publish step, fed by whichever source ran. A **project home**'s `skills/` is symlinked into that
set, with project-specific skills beside the links. A
**repository**'s own `.claude/skills/`, which is what this section lists, is the tier for things
genuinely coupled to the code, and it wins over a home skill of the same name. The dispatcher
names the applicable tiers in the agent's prompt, so a dispatched session is handed the paths
rather than left to discover them.

## Working agreements

The standing rules this section carried are decisions in the store now, alongside the git rules
above: what gets built before anything shiny, that a rule carries the incident that created it,
that an unobserved fact is recorded as unknown rather than guessed at, that a dependency choice
states its own why, how a headless session runs its gates, and what a session may never do to
this host to chase a flaky test. Read them in **`decisions.md`** or with `h9k decide list`, and
record a new one with `h9k decide "<one claim>"`.

A run-earned lesson is the other half of the same store and a different act: `h9k learn "<what
this run learned>"` records one, and `lessons.md` renders them beside the decisions. A dispatched
agent records lessons; decisions are a human's to record.
