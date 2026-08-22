# Hall9k — Agent & Contributor Guide

Canonical guidance for anyone (human or agent) working in this repo. `CLAUDE.md` defers here.

## What this is

Hall9k is a local-first agentic workflow platform: `h9k` CLI + `h9kd` daemon + Postgres,
orchestrating detached Claude Code agents. Read in order of need:

- `PLAN.md` — vision, architecture, and the **v0 Decisions Log** (§16; binding decisions live there)
- `TASK-MODEL.md` — the domain model: streams, events, aggregates, type discipline
- `SLICE-1.md` — the current build breakdown and acceptance criteria
- `HALL9K-P2P-DESIGN.md` — the peer-to-peer layer: identity, discovery, NAT traversal (design only, nothing built; Decisions Log #38-#58)

## Build / test / run

```bash
dotnet build                 # full solution (Hall9k.slnx)
dotnet test                  # unit + integration tiers (integration needs Docker for Testcontainers)
dotnet run --project src/Hall9k.AppHost    # dev loop: Postgres + daemon + Aspire dashboard
docker compose up -d         # Postgres only (installed mode / manual runs)
./src/Hall9k.Cli/bin/Debug/net10.0/h9k     # the CLI binary after build
h9k install                  # publish release binaries to ~/.hall9k/bin (no service registered)
h9k daemon start|stop|status # the CLI-owned daemon lifecycle (Decisions Log #31)
h9k project list             # every project with its tasks counted by attention bucket
h9k project show <name>      # one project: registration, settings, rollup, newest tasks
h9k task list --project <name> --state <state>   # browse tasks, newest first (--all, --limit)
h9k status                   # the attention pane: state, phase, and attention on every row
h9k idea add "<text>"        # capture an idea; discovery starts, a project is optional
h9k connection list          # every external account this install can reach, and where its credential lives
```

The board answers four questions with three surfaces (Decisions Log #66). **State** is the
lifecycle in seven words: Draft, Published, Working, Delivered, Done, Failed, Archived.
**Delivered** is pushed-with-the-merge-not-yet-observed, and **Done** renders only at true
closeout, which is the same bar the dependency rule uses. **Phase** is the line under a live row,
composed from the run's records plus an observation of the recorded process - it never claims a
session is doing something without seeing the process, and says "liveness not observed here" when
it cannot. **Attention** is needs-you or not, with the one-line cause and the command that clears
it. The run vocabulary (Running, UnderReview, AwaitingReview, ChecksFailing, …) is the phase
line's material and never appears in the Status column; `--state` still selects on it (as
`run-failed` for the one word the lifecycle vocabulary already owns).

Ideas come before tasks (Decisions Log #35). An idea undergoes **discovery** (what is this?);
a draft task undergoes **refinement** (how does this become executable?). A task is an idea with
intent, and `h9k idea promote` is the hinge between the two.

```bash
h9k idea add "<text>"                             # capture: one command, one argument
h9k idea add "<text>" --project <name>            # --project is optional, at capture and after
h9k idea list                                     # what is still in discovery, newest first
h9k idea show <id>                                # note, project, workspace path, history, outcome
h9k idea revise <id> "<text>"                     # rewrite the note; every version stays on the stream
h9k idea assign <id> --project <name>             # set or change where it belongs
h9k idea promote <id> [--project <name>]          # becomes a draft task; needs a project
h9k idea discard <id> --reason "<why>"            # closed honestly, never deleted
```

Every idea owns a discovery workspace, `~/.hall9k/ideas/<idea-id>/workspace`, where research
notes, gathered files, and prototypes accumulate. The stream records milestones only, never file
contents, and promotion carries the workspace path forward as the draft's agent context.

Task development and task dispatch are separate lifecycles (Decisions Log #34): `h9k task add`
creates a **draft**, and nothing dispatches until a human publishes and assigns it.

```bash
h9k task add --project <name> --objective "…"     # creates a Draft (identity, not readiness)
h9k task add --project <name> --from-issue 42     # adopt a GitHub issue (number, owner/repo#42, or URL)
h9k task add --project <name> --from-jira PROJ-1  # adopt a Jira card (key or URL)
h9k task revise <id> --criteria "…" --blocked-by <id>   # Draft-only; each option replaces that part
h9k task publish <id> [--assign]                  # the readiness gate; --assign starts it too
h9k task assign <id> [<owner>]                    # the dispatch trigger — Queued, or Blocked on dependencies
h9k task unassign <id>                            # back to Published (refused while leased)
h9k task draft <id>                               # Published back to Draft, so it can be revised
```

The edit-after-the-fact path is `unassign → draft → revise → publish → assign`, each step an
explicit act. A dependency counts as met only at true closeout (the pull request merged and the
closeout monitor observed it); TASK-MODEL.md §2.3 has the whole picture.

`--from-issue` and `--from-jira` adopt existing external work (PLAN.md §3.1a, Decisions Log #60,
#65): the item's title seeds the objective, its description becomes agent context, and the item is
recorded as the task's `ExternalReference`. Acceptance criteria are never read out of a description;
supply them with `--criteria` or at the prompt. Import is a one-time snapshot, so the state read at
import is recorded as an observation of that moment and never re-checked. Every source goes through
`IWorkItemProvider` in `Hall9k.Connectors`, so a new one is a resolver rather than a new command.

Jira is connected as a **read** credential plus an agent-mediated pen (Decisions Log #65), because
reading Jira is configuration-agnostic and writing it is not:

```bash
h9k connection add jira --site https://your-org.atlassian.net --email you@example.com
h9k connection list                               # provider, account, site, credential reference
h9k project set <project> --jira PROJ             # bind the board; 'none' clears it
h9k task push-to-jira <task>                      # dispatch an agent run that writes the card
h9k task link-jira <task> PROJ-123                # record the card, verified against Jira first
```

The platform never authors a card: issue types, required fields and routing rules are the
organisation's configuration, so `push-to-jira` dispatches a session into the project's own
repository, where its card-authoring skills live, and that session finishes by calling
`link-jira`. **Agent-facing commands are observation gates**: `link-jira` reads the key back
through the registered connection and records what Jira answered, so an agent's claim is an
argument that gets checked rather than a fact that gets accepted. Registered credentials are
recorded as references (`env:`, `keychain:`, `file:`) and never as secrets. When a task carrying a
Jira reference merges, closeout comments the pull request on the card; it never transitions the
card, because which status a merge means is a team's workflow rather than a fact about software.

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
- One project, two tiers: unit (DB-free — aggregates via `Apply`, projections via a
  `FakeEvent<T>` stub) and integration (Testcontainers Postgres).
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
  ones.** This is the companion to the rule above, and the platform now depends on it: since
  every comment is authored under the human's login, the only way to tell a reviewer's
  comment from an earlier agent's is that a thread's FIRST comment is always a reviewer's,
  including when its author is the pull request's own owner leaving themselves a note. Open a
  new thread and the next run cannot tell your comment from feedback. Origin incident
  (2026-08-20): Brian commented on PR #20 and the machinery was structurally blind to it,
  because the closeout inspector filtered threads to Copilot authors and agent replies under
  his login were indistinguishable from his own. The honest long-term answer is node-signed
  authorship in the P2P identity layer (§16 #38-#58); until then, this invariant is what the
  discriminator rests on, so breaking it breaks review handling (§16 #62).
- **Feedback reaches the platform only when a review is submitted.** GitHub hides an
  unsubmitted (`PENDING`) review's comments from the API entirely, so a reviewer part-way
  through a draft is invisible to the closeout monitor and to any agent reading the PR. That
  is correct (nothing has been said yet), but it is why a pull request can look quiet while
  feedback is being written. Never read silence as "the reviewer had nothing to say".
- Branch naming for task work: `task/<id>-<slug>`, created off `origin/main` with `--no-track`.
- `main` is only ever checked out in the `dev/` worktree; agent worktrees are siblings of `dev/`.
- **PR branches are authored history, not a diary.** Commits read as a natural progression of
  the whole change: no work-in-progress commits, no "address review feedback" commits. A fix
  that belongs to an existing commit folds into it: `git commit --fixup=<owning-commit>` (map
  each fix to the commit that owns the files it changes), then
  `GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash origin/main` and
  `git push --force-with-lease`. Verify the rebased tree matches the tested tree
  (`git diff <old-tip> HEAD` is empty) so green test runs carry over. Origin incident
  (2026-08-17): PR #6's independent-review round first landed as a separate "harden closeout"
  commit and had to be rebuilt into the owning commits by hand.
- **Follow-up runs never push; the daemon pushes follow-up branches with
  `git push --force-with-lease`.** Rewriting history per the rule above is safe: verify
  tree identity, finish, and let the platform push. Origin incident (2026-08-17): the
  first two automatic follow-up runs rebased correctly, the daemon's then-plain push
  rejected both rebased branches ("failed to push some refs"), and both runs failed with
  completed, gated work stranded in the worktrees; this file briefly told agents to run
  the force push themselves as the workaround, until the daemon became force-aware
  (decision #26).

## Repo skills

Repo-resident Claude skills live in `.claude/skills/` and are available in every worktree:

- **absorb-review-fixes** — fold review-feedback fixes into their owning commits (fixup +
  autosquash + tree-identity check) so the PR branch stays authored history
- **commit-plan** — organize the working tree into cohesive, buildable commits ordered for PR review
- **resolve-review-threads** - triage every unresolved review thread on an existing PR,
  whoever opened it (Copilot, a teammate, or the author's own self-review): fix, reply
  in-thread, resolve. Supersedes the Copilot-only `resolve-copilot-reviews` skill (§16 #62)
- **pr-summary** — generate a PR title/description from the branch's commits (text only — the
  daemon opens PRs; agents never do)

There is deliberately no create-pr skill: PRs are opened by the daemon (`PullRequestOpener`),
never by agents.

## Working agreements

- Slice 1 before anything shiny; check SLICE-1.md before inventing work.
- Decisions get appended to PLAN.md §16 (v0 Decisions Log) as they're made.
- **Standing rules carry their origin incident.** When a failure produces a new rule (in
  this file, the decisions log, or a skill), record the concrete incident that created it
  alongside the rule — so future readers know why it exists and when it might not apply.
  A rulebook is an accumulation of documented scars, not decrees.
- **Never guess at unobserved facts.** Audit fields, history, and identifiers record what
  was actually observed; the unobserved is represented as explicitly unknown (sentinels,
  nulls, honest labels like "purged per policy") — never plausibly filled in. An audit
  trail that guesses at provenance is worse than one that admits the gap.
- Every dependency or pattern choice gets a one-line "why" and a one-line "does this block
  the later vision?"
