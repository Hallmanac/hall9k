# Hall9k — Agent & Contributor Guide

Canonical guidance for anyone (human or agent) working in this repo. `CLAUDE.md` defers here.

## What this is

Hall9k is a local-first agentic workflow platform: `h9k` CLI + `h9kd` daemon + Postgres,
orchestrating detached Claude Code agents. **An interactive session in this repo is the
orchestrator window**: read `ORCHESTRATOR-WINDOW.md` before anything else, because it is the role
you are in. A *headless* dispatched session is not one, never loads that file, and should read on
here instead. Then, in order of need:

- `PLAN.md` — vision, architecture, and the **v0 Decisions Log** (§16; binding decisions live there)
- `TASK-MODEL.md` — the domain model: streams, events, aggregates, type discipline
- `SLICE-1.md` — the current build breakdown and acceptance criteria
- `HALL9K-P2P-DESIGN.md` — the peer-to-peer layer: identity, discovery, NAT traversal (design only, nothing built; Decisions Log #38-#58)
- `README.md` + `docs/` — the newcomer's on-ramp (concepts, CLI map, operations, and `docs/scope.md`,
  which is the honest works-today / designed-but-unbuilt / never-doing inventory). Written from this
  file and the four above, so when behaviour changes here, `docs/` is downstream and needs the edit too.

## Build / test / run

```bash
dotnet build                 # full solution (Hall9k.slnx)
dotnet test                  # unit + integration tiers (integration needs Docker for Testcontainers)
dotnet run --project src/Hall9k.AppHost    # dev loop: Postgres + daemon + Aspire dashboard
docker compose up -d         # Postgres only (installed mode / manual runs)
./src/Hall9k.Cli/bin/Debug/net10.0/h9k     # the CLI binary after build
```

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
  `Commands/ Events/ Handlers/ Queries/ Projections/ Documents/` subfolders; tiny slices
  (Owner, Node, Connection, Idea) stay flat.
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

- **Commits are authored as the repo owner. No `Co-Authored-By` trailers, no bot attribution,
  no generated-with footers** (PLAN.md §6.6). This is a hard rule for agents.
- **Agents never START a review thread on a pull request; they only reply inside existing
  ones.** A thread's FIRST comment is always a reviewer's — including one where the author is the
  pull request's own owner leaving themselves a note — because every comment is authored under
  the human's login, and that is the only way the platform tells a reviewer's comment from an
  agent's. Open a new thread and the next run cannot tell your comment from feedback. Origin
  incident (2026-08-20): a human's own PR comment was structurally indistinguishable from an
  agent's reply under the same login. The honest long-term fix is node-signed authorship in the
  P2P identity layer (§16 #38-#58); until then, breaking this invariant breaks review handling
  (§16 #62). The one command that starts a thread, `h9k pr request-changes` (§16 #149), posts a
  review a human typed and ran; a lap's push guard denies an agent the ordinary route to it.
- **An agent never tells a *human* reviewer they are wrong on a standing review**: draft it, park, let them send it (§16 #152) — a plain thread comment instead gets an evidence-based decline reply, leaving only its resolve to them (§16 #159).
- **Feedback reaches the platform only when a review is submitted.** GitHub hides an unsubmitted
  (`PENDING`) review's comments from the API entirely, so a reviewer part-way through a draft is
  invisible to the closeout monitor and to any agent reading the PR. Never read silence as "the
  reviewer had nothing to say".
- **Merging by hand is a four-gate check, and every gate is a reason not to merge** — the same four
  the daemon's pre-approved merge reads (§16 #135): CI green, the review decision satisfied, **no
  outstanding requested reviewer**, every review thread resolved. The third is a gate, not a
  formality, and `h9k status` / `h9k task show` name who (§16 #150). Detail: ORCHESTRATOR-WINDOW.md.
- Branch naming: `task/<id>-<slug>` unless the project set its own convention
  (`h9k project set <project> --branch-template`), created off `origin/main` with `--no-track` —
  except a task declared `--stacked-on` another, whose branch is cut from that parent's branch head
  and whose pull request targets it (Decisions Log #144). **Every base-branch reference in a
  dispatched session's own prompt is already the right one for that session**, stacked or not:
  never substitute `origin/main` for what the prompt names, and never retarget or rebase a stacked
  branch onto main by hand — the daemon does both mechanically when the parent merges.
- `main` is only ever checked out in the `dev/` worktree; agent worktrees are siblings of `dev/`.
- **PR branches are authored history, not a diary.** No work-in-progress commits, no "address
  review feedback" commits. A fix that belongs to an existing commit folds into it:
  `git commit --fixup=<owning-commit>`, then
  `GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash origin/main` and
  `git push --force-with-lease`, verifying `git diff <old-tip> HEAD` is empty so a green test run
  carries over. Origin incident (2026-08-17, PR #6): a review-round fix first landed as its own
  commit and had to be rebuilt into the owning commits by hand.
- **An agent never pushes; the daemon pushes every branch — fresh or follow-up — with
  `git push --force-with-lease`, never plain `--force`.** Rewriting history per the rule above is
  safe: verify tree identity, finish, and let the platform push. The daemon's push runs an
  explicit ancestor-or-reflog check before pinning the lease's expected value, refusing outright
  rather than forcing over a tip it cannot account for (Decisions Log #26, #103, #104 — origin: a
  plain push once rejected two rebased follow-up branches and stranded completed work in 2026-08-17's
  first automatic follow-up runs).
- **Commit as you go during a fresh build session, then recompose once, right before you
  finish.** Checkpoint commits are crash protection, not authored history. Once the full suite is
  green and every checkpoint is committed, the session hunts its own diff for defects — a
  same-session adversarial self-review, capped at two rounds — then resets to the branch's fork
  point and recomposes the checkpoints into real history in one continuous step, verifying tree
  identity against the pre-reset tip before finishing. Every dispatched session's own prompt
  spells out the exact mechanics for that run (fork-point capture, the tree-identity check, the
  three mandatory self-review hunts); this is the standing rule behind it, not a substitute for
  it. Origin: Decisions Log #104, #113 — two full external review laps in one afternoon
  (2026-08-30) and three no-commit strandings in one night (2026-08-29) that this discipline now
  prevents.

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

- Slice 1 before anything shiny; check SLICE-1.md before inventing work.
- A **branch** appends its own decision to PLAN.md §16 (v0 Decisions Log) at the tail, under a
  placeholder from its task's own short id (`PLACEHOLDER-<shortid>`) rather than a hand-picked
  number (Decisions Log #162). The mechanical pre-final-pass rebase step assigns
  the true number, rewriting the entry, its note, and every citation — no agent renumbers it.
- **Standing rules carry their origin incident.** When a failure produces a new rule (in
  this file, the decisions log, or a skill), record the concrete incident that created it
  alongside the rule — so future readers know why it exists and when it might not apply.
  A rulebook is an accumulation of documented scars, not decrees.
- **Never guess at unobserved facts.** Audit fields, history, and identifiers record what
  was actually observed; the unobserved is represented as explicitly unknown (sentinels,
  nulls, honest labels like "purged per policy") — never plausibly filled in. An audit
  trail that guesses at provenance is worse than one that admits the gap.
- Every dependency or pattern choice gets a one-line "why" and a one-line "does this block the later vision?"
- A headless session runs its gates in the foreground, never behind `run_in_background`/`Monitor`/`ScheduleWakeup`, and never ends its turn with one still pending — it is killed the instant it finishes (PLAN.md §16 #163).
