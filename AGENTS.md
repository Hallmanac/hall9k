# Hall9k — Agent & Contributor Guide

Canonical guidance for anyone (human or agent) working in this repo. `CLAUDE.md` defers here.

## What this is

Hall9k is a local-first agentic workflow platform: `h9k` CLI + `h9kd` daemon + Postgres,
orchestrating detached Claude Code agents. **An interactive session in this repo is the
orchestrator window**: read that section below before anything else, because it is the role you
are in. Then, in order of need:

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
h9k install                  # publish release binaries to ~/.hall9k/bin (no service registered)
h9k update                   # refresh an already-installed machine from the latest GitHub release, no repo/SDK needed
h9k uninstall [--purge-data] # take the platform off the machine; the database survives unless --purge-data (Decisions Log #83)
h9k daemon start|stop|status # the CLI-owned daemon lifecycle (Decisions Log #31)
h9k config show|set          # the daemon's durable operating settings: node ceiling (--max-concurrent-task-runs), the per-run session cap default (--session-cap-per-run), model-by-role, interactive-claim-stale-after-days, review-cycle caps, the review stage composition (--review-stage-composition, --accept-reduced-review to degrade it), a periodic token-spend budget (--spend-budget, --spend-period; backlog 59, Decisions Log #103, #111, #112, #120, #129)
h9k doctor [--yes]           # diagnose the database situation and what to do about it; --yes remediates non-interactively, for scripts and dispatched agents (Decisions Log #73, #74, #118)
h9k project add --name <n> --repo-url <url>   # register a project and create its home directory
h9k project init <name>      # create, repair or refresh a project's home; idempotent
h9k project list             # every project with its tasks counted by attention bucket
h9k project show <name>      # one project: home, registration, settings, rollup, newest tasks
h9k project set <name> --branch-template "{key}-{slug}"   # the team's branch convention; 'none' restores task/{shortid}-{slug} (Decisions Log #121)
h9k project set <name> --review-stage-composition <VALUE|default>   # which pre-PR review stages a run gets: full-pipeline (default), adversarial-only, conformance-only, skip-final-pass, none — also settable at node (h9k config set) and task (h9k task add/revise), task > project > node > default, frozen at each run's own dispatch; a value that removes a guarantee needs --accept-reduced-review (Decisions Log #129)
h9k task list --project <name> --state <state>   # browse live and done tasks, newest first (--all, --limit, --include-archived, --epic)
h9k status                   # the attention pane: state, phase, and attention on every row
h9k idea add "<text>"        # capture an idea; discovery starts, a project is optional
h9k epic add --project <name> --title "<name>"    # name a first-class grouping of tasks (Decisions Log #100)
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

**Every project owns a home directory** (Decisions Log #76), `~/.hall9k/projects/<name>` unless
the project says otherwise, in the same shape on every machine:

```
<home>/
├── AGENTS.md   generated from the project's facts; a render, never hand-maintained
├── repo/       <name>.git (bare clone) · dev/ (a worktree on the primary branch) · wt-*/
├── ideas/
├── tasks/      _archive/ holds terminal tasks (closed out or abandoned); moved back if reopened
├── skills/     plain markdown skill docs, seeded from the install's canonical set
└── .claude/    generated Claude Code plumbing: skills/ symlinked into skills/, never copied
```

Creating it is a recipe, not an agent: `h9k project add` and `h9k project init` do the
directories, the bare clone with its fetch refspec corrected, the `dev/` worktree, the skill
seeding and the render, all in platform code and all idempotent. Re-running it is also how a
home catches up: `repo/dev` is fetched and fast-forwarded, and the step says whether it moved,
was already current, or was left alone because local changes blocked it. `h9k install` publishes
the canonical skill set to `~/.hall9k/skills`, and a project's `skills/` links into it, so updating
the platform updates every project's platform skills in one move. **The location is a setting;
the shape is the contract**, which is what lets the dispatcher hand a session paths instead of
sending it hunting.

Every task owns a directory under `tasks/` named `<shortid>-<slug>` from its objective, holding
`task.md` (the readiness contract — frontmatter plus the agent context as prose, the same format
`h9k task add/revise --file` reads) and a `workspace/` for whatever refinement accumulates beside
it; an idea with a project renders the same shape under `ideas/` (backlog 48). Drafts render
exactly like published tasks — the browsing surface is where the thinking lives. **The render is
one-way and daemon-driven**, a sweep over the store's current state rather than a per-event
handler (the same shape `DispatchLoop` and `CardPublicationLoop` already use): the daemon rewrites
a file only when its rendered content actually changed, and the first sweep after start is the
same reconciliation pass that backfills a home created after tasks existed. A human edits the file
and applies it back through the existing gate — `h9k task revise <id> --file <path>`, or
`h9k idea revise <id> "<text>"` (ideas have no `--file` form) — the store decides what happened,
never the file. An idea with no project yet has nowhere to render into, so it stays in its global
discovery workspace until assigned. Where that workspace lives is decided once, at capture, and
never changes after (backlog 49): an idea captured with a project whose home already exists gets
its workspace under that home from the start; an idea captured with no project, or with a project
that has no home yet, keeps its workspace at the global location permanently, even once it is
later assigned to a project — assignment never retroactively relocates an already-materialised
workspace. The hall9k project's own move into its default home landed as that cutover chore
(backlog 52): the project home at `~/.hall9k/projects/hall9k` is canonical, and this repository is
worked from its `repo/dev` worktree.

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

Every idea owns a discovery workspace, where research notes, gathered files, and prototypes
accumulate: `~/.hall9k/ideas/<idea-id>/workspace` for an idea whose capture-time project had no
home yet (or had none at all), or `<home>/ideas/<shortid>-<slug>/workspace` when it did (backlog
49). The stream records milestones only, never file contents, and promotion carries the current
workspace path forward as the draft's agent context.

Task development and task dispatch are separate lifecycles (Decisions Log #34): `h9k task add`
creates a **draft**, and nothing dispatches until a human publishes and assigns it.

```bash
h9k task add --project <name> --objective "…"     # creates a Draft (identity, not readiness)
h9k task add --project <name> --from-issue 42     # adopt a GitHub issue (number, owner/repo#42, or URL)
h9k task add --project <name> --from-jira PROJ-1  # adopt a Jira card (key or URL)
h9k task add --project <name> --from-pr 42        # adopt a pull request to review (always pr-review)
h9k task revise <id> --criteria "…" --blocked-by <id>   # Draft-only; each option replaces that part
h9k task revise <id> --queue-first                # the one revision Draft-only doesn't gate: marks the task-level queue-first fact (Decisions Log #127), settable in any live state; --clear-queue-first removes it
h9k task revise <id> --review-stage-composition <VALUE|default>   # Draft-only, unlike the review caps below — a live change reaches only the task's next run (Decisions Log #129)
h9k task set-review-caps <id> --max-compliance-review-cycles <N>   # a task-level review-cycle-cap override, settable at any time — even while the run is live (Decisions Log #112)
h9k task publish <id> [--assign]                  # the readiness gate; --assign starts it too
h9k task publish <id> --no-existing-item          # required if a tracking backlog policy finds no linked item yet and has no publication already pending
h9k task publish <id> --untracked                 # the same gate's other exit: deliberately skip tracking for this task, attested on the stream
h9k task assign <id> [<owner>]                    # the dispatch trigger — Queued, or Blocked on dependencies
h9k task set-session-cap <id> <cap>               # override how many agent sessions this task's run may hold at once; settable any time, even mid-run (Decisions Log #111)
h9k task unassign <id>                            # back to Published (refused while leased)
h9k task draft <id>                               # Published back to Draft, so it can be revised
```

The edit-after-the-fact path is `unassign → draft → revise → publish → assign`, each step an
explicit act. A dependency counts as met only at true closeout (the pull request merged and the
closeout monitor observed it); TASK-MODEL.md §2.3 has the whole picture.

An epic is a first-class named grouping of tasks (Decisions Log #100): its own id, title, and
Open/Closed state, event-sourced like everything else. Membership is optional and no-ceremony —
it rides the task's own stream, so the flat task model is undisturbed for everything ungrouped —
and a task joins or leaves at `h9k task add --epic` or `h9k task revise --epic`/`--clear-epic`,
must belong to the same project as the epic, and only an Open epic accepts new members. An epic
closes only by explicit human act with a reason, never automatically, not even when its last
member task closes out; there is no `h9k epic reopen` yet.

```bash
h9k epic add --project <name> --title "<name>"    # name a new epic: a project and a title
h9k epic list [--project <name>] [--state <state>]   # every epic with a member-task rollup; open|closed|all
h9k epic show <id>                                # one epic: title, state, Jira link, every member task
h9k epic link-jira <id> <key-or-url>              # record the Jira item this one corresponds to, an epic or a card it tracks on a team's behalf; identity only
h9k epic close <id> --reason "<why>"              # the only way an epic ends
```

**One Jira card that needs many pull requests does not distribute across sibling tasks the way
it looks like it should.** A task can adopt an external item at all only if no other task already
carries it, unless that holder has since been abandoned: Done still holds the reference, so a
finished-but-not-abandoned holder still blocks a second adoption. The one further exception is a
Done `pr-review` task: its completed review does not hold its pull request hostage the way adopted
work holds its issue, so `h9k task add --from-pr` re-adopts it for a second pass, exactly as
`TaskDecider.Reopen` sends the owner here to do. `--from-issue`/`--from-jira` refuse a second
adoption of a card another live task holds, and `h9k task link-jira` and `h9k task link-issue`
enforce the identical rule on the same field's other doors
(`TaskAddCommand.RefuseSecondAdoptionAsync`), one card, one owning task, so a card never ends up
with two sets of runs and two closeout comments. A card that genuinely needs several tasks working
toward it (ARX-5510: one Jira card, eight pull requests) runs straight into that refusal if every
task tries to adopt it, and the refusal is the guard working as designed, not a bug to route
around; the pattern below is what the arx team found by trial and error against it. The epic
carries the card (`h9k epic link-jira <epic> <key-or-url>`, whose own `--help` still calls the
argument "the Jira epic's key": it stores whatever key or URL it is given verbatim and unverified,
epic or card alike, so this pattern's use of it for a card is not a misuse), exactly one member
task formally adopts it (`--from-jira` at creation, or `h9k task link-jira` after), and every
sibling task carries the same card only in its agent context: free text naming the epic and the
card, never a second `ExternalReference`, so eight tasks can share one card's work without eight
of them fighting over who owns it. On a project whose backlog policy is `jira` or `github-issues`,
that means every sibling task must publish with `h9k task publish <id> --untracked`: a sibling
carries no `ExternalReference` of its own, so without `--untracked` the pre-publish gate (below)
either refuses it outright or, taken the other way out with `--no-existing-item`, lets the
post-publish auto-tracking step mint a brand-new card or issue for it: exactly the second-card,
second-closeout problem this pattern exists to avoid. A sibling published `--untracked` this way
never carries an `ExternalReference` at all, permanently, so on a project whose branch template
(below) uses `{key}` every sibling branch renders it `no-key`: for ARX-5510's eight tasks that is
one branch named for the owning task's card and seven named `no-key-<slug>`, distinguished only by
slug and by `ResolveBranchNameAsync`'s own collision retry when two slugs coincide. Nothing about
that is broken, but it is worth knowing before dispatch rather than discovering it there.

An operator can work a Published, Queued, or already-Blocked task interactively instead of dispatching it headless
(Decisions Log #122). On a Published task assigned to nobody, `h9k task work` assigns it to the
operator's own owner and claims it interactively in one atomic event append: the task is never
observably Queued in between, so the dispatcher, woken within moments by the doorbell notification
a plain `h9k task assign` would have sent, can never win the race to it. An unmet dependency —
whether just discovered here on a Published task, or already sitting Blocked from an ordinary
`h9k task assign` or a claim handed back or retried — warns rather than refuses outright (Decisions
Log #128): the platform names every open blocker, and `--acknowledge-unmet-dependencies` is the
human's recorded override to claim it anyway, the same bar `h9k task assign` itself holds an
assignment to. Not needed twice: an acknowledgment this task already carries from an earlier claim
on the same still-open blockers is honored without asking again, and `h9k task show` names whether
a claim's own acknowledgment was given fresh or carried forward from an earlier one. `h9k task
assign` and `h9k task publish --assign` are both unchanged and remain the headless dispatch
triggers; there is no `--interactive` flag on `assign` — edges still gate automatic dispatch
exactly as before, and only this deliberate human claim gets the warn-and-proceed path. Whichever
state it entered from, the claim itself is held by the human, not a process, so there is no lease
and no heartbeat reclaim; closing the terminal is a normal way to leave, and running
`h9k task work` again re-enters the same worktree (Decisions Log #103).

By default `h9k task work` claims and cuts as above, then prints the worktree path, the branch,
and a starting prompt (assembled through `WorkPromptBuilder`, the same code every path already
uses) for the operator to paste into a Claude Code session started anywhere — it no longer
launches or waits on the session itself (Decisions Log #126). The pasted session's own first act
is the new agent-facing observation gate `h9k task register-session <id>`, which records its
process identity (read from `CLAUDE_PID`, Claude Code's own environment variable) the way a direct
launch's own launch-time recording always did; the double-booking and liveness guards below key
off that record, and a session that never registers degrades honestly to a no-op rather than a
false block, exactly as a claim nobody ever recorded a session against always has. `--direct-launch`
keeps the prior behavior for one release — `h9k task work` itself launches a plain interactive
Claude Code process and waits on it, resuming the most recently recorded session's own
conversation rather than starting a fresh one (falling back to a fresh session, announced, only
when the recorded one cannot be resumed — Decisions Log #124) — and is the only path the Windows
script-shim refusal still gates, since a pasted prompt travels through no argv:

```bash
h9k task work <id>                   # claim a Published, Queued, or already-Blocked task, cut the same branch/worktree headless dispatch would, print the worktree/branch/starting prompt
h9k task work <id> --direct-launch   # the prior behavior for one release: launch and wait on the session here instead of printing a prompt
h9k task work <id> --acknowledge-unmet-dependencies   # claim a task anyway, despite named open blockers
h9k task register-session <id>       # the pasted session's own first act: register its process identity against the claim
h9k task verify <id>                 # run the project's gates on demand against the claim's worktree
h9k task deliver <id>                # push and hand the claim into the standard delivery pipeline
h9k task handback <id>               # release the claim to a headless agent partway through, resuming the branch
h9k task handback <id> --first   # same release, plus the queue-first marker: the next free slot takes it regardless of age (Decisions Log #127)
h9k task handback <id> --now     # same release, dispatched immediately, ceiling-exempt, through h9k task start's own mechanism — refused together with --first
h9k task release <id>                # give an untouched claim back to the dispatch queue
```

A deliberate human kick-off dispatches a Published, Queued, or already-Blocked task on the spot, headless, instead
of dispatching it interactively or waiting on the queue (Decisions Log #125, "Take the Wheel"
epic 9272e514's start-it-mine mode): `h9k task start <id>` reuses `h9k task work`'s own claim
shape exactly (the same ceiling-exempt sentinel, so every existing lever above — deliver, verify,
handback, release, the stale-claim nudge, and re-entering via `h9k task work` itself — accepts a
start-it-mine claim on the identical terms an interactive one already gets) and the same atomic
Published entry, but launches the
agent headless (`claude -p`, Claude Code's own completion mode) and detached rather than attached
to this terminal, under the slice-1 `<task-shortid>-build` name, addressable on the session mesh
the moment it starts. Shares `h9k task work`'s own warn-then-acknowledge shape for an unmet
dependency, on a Published task and on an already-Blocked one alike (Decisions Log #128, closing
the gap #125 deliberately left open — that gap was about re-entering a live claim, which
`h9k task start` still never does, not about withholding a carried-forward acknowledgment from a
fresh claim on a Blocked task): the platform names every open blocker and advises, and
`--acknowledge-unmet-dependencies` is the human's recorded override to start anyway (the epic's own
ruling: "the platform advises, the human overrides, and the acknowledgment is recorded") —
recorded on the resulting `TaskClaimed` and surfaced on `h9k task show` beside the blockers it
overrode. Not needed twice: an acknowledgment this task already carries from an earlier claim on
the same still-open blockers is honored without asking again, whichever of `h9k task start` or
`h9k task work` gave it. `h9k task start` refuses Draft (publish it first), a pr-review task, a
reopened task's follow-up branch, and any task that already carries a live claim — there is no
re-entry branch the way `h9k task work` has one; a fresh claim is all this command ever makes, and
a fresh claim on an already-Blocked task is exactly what its own Blocked entry is, not a re-entry.
Giving such a claim back before it finishes — `h9k task handback`, `h9k task release`,
`h9k task retry`, or `h9k pr resolve`'s own reopen — lands the task on Blocked rather than Queued
whenever the acknowledged dependency is still open, since claiming never clears it, only assigning
does; each of those commands names the still-open blocker(s) rather than claiming a run that will
not in fact dispatch, and the acknowledgment itself stays on record for whichever command reclaims
the task next. `h9k task deliver` recovers a start-it-mine session's own handoff and token
usage from its `stream.jsonl` at delivery time — the only point anything on this node reads that
file back, since a run dispatched under the sentinel node id above is never adopted to do it any
other way.

```bash
h9k task start <id>                              # dispatch a Published, Queued, or already-Blocked task headless, on the spot, ceiling-exempt
h9k task start <id> --acknowledge-unmet-dependencies   # start a task anyway, despite named open blockers
```

`--from-issue` and `--from-jira` adopt existing external work (PLAN.md §3.1a, Decisions Log #60,
#65): the item's title seeds the objective, its description becomes agent context, and the item is
recorded as the task's `ExternalReference`. Acceptance criteria are never read out of a description;
supply them with `--criteria` or at the prompt. Import is a one-time snapshot, so the state read at
import is recorded as an observation of that moment and never re-checked. Every source goes through
`IWorkItemProvider` in `Hall9k.Connectors`, so a new one is a resolver rather than a new command.

Jira is connected as a **read** credential plus a compose/execute write path (Decisions Log #65,
#102), because reading Jira is configuration-agnostic and writing it is not, but writing it also
has to be deterministic and auditable rather than left entirely to an agent's own judgment:

```bash
h9k connection add jira --site https://your-org.atlassian.net --email you@example.com
h9k connection list                               # provider, account, site, credential reference
h9k project set <project> --jira PROJ             # bind the board; 'none' clears it
h9k task push-to-jira <task>                      # dispatch an agent run that composes the card
h9k task write-jira <task> --op create --file <payload.json>  # hall9k executes a composed write
h9k task link-jira <task> PROJ-123                # record a pre-existing card, verified against Jira first
```

The platform never authors a card's *content*: issue types, required fields and routing rules are
the organisation's configuration, so `push-to-jira` dispatches a session into the project's own
repository, where its card-authoring skills live, to work out what the card should look like — but
that session makes no Jira call itself. It composes a payload and submits it through
`write-jira`, which is the sole executor of every Jira write (Decisions Log #102, #114): hall9k
validates the payload (a transition or a close is refused regardless of who composed it — that is
a team's workflow, done in Jira directly, never a write hall9k performs), records the intent with
the full payload before anything is sent, executes it against the Jira Cloud v2 REST API — the
same authenticated client the read side already uses — verifies the outcome by reading the item
back rather than trusting Jira's own success response alone, and records the outcome including the
returned key. A retried or replayed create narrows the window for a duplicate rather than closing
it outright: `write-jira` searches for a marker an earlier attempt's card would carry before
creating anything new, but Jira's search index updates asynchronously, so a retry inside that
index-lag window can still find nothing even though the card genuinely exists. **Agent-facing
commands are observation gates**: `write-jira` and `link-jira` both read the key back through Jira
before recording anything, so an agent's or an operator's claim is an argument that gets checked
rather than a fact that gets accepted. Registered credentials are recorded as references (`env:`,
`keychain:`, `file:`) and never as secrets — the one registered connection's API token now covers
both directions, reading and writing alike, and it expires only the way any API token does (an
organisation revoking or rotating it), which is rarer and less routine than the machine-wide
browser login (`twg login`) the write path used before #114: a rejected credential is still a
handled state, not a crash. A write that hits one is recorded pending on the task, surfaces as a
needs-you row telling the operator to refresh the connection (`h9k connection add jira`), and the
daemon retries the identical write automatically once that succeeds — covering both an operator's
own `write-jira` and a daemon-dispatched write such as closeout's own merge comment. When a task
carrying a Jira reference merges, closeout comments the pull request on the card through this same
write surface; it never transitions the card, because which status a merge means is a team's
workflow rather than a fact about software. `h9k doctor` probes the registered connection's
credential whenever a project's backlog policy is `jira`, distinguishing no connection registered
from a rejected credential and teaching `h9k connection add jira` as the fix for the latter — the
Atlassian CLI (`twg`) this write path used before #114 is no longer required for any `h9k` or
`h9kd` operation.

**Every published task is tracked automatically**, per a project setting (backlog: track every
published task), and GitHub gets a write path of its own — unlike Jira, an issue's shape (title,
body, labels) is uniform enough for the platform to author deterministically, with no agent
needed:

```bash
h9k project set <project> --backlog none|github-issues|jira   # default none — today's behavior
h9k project set <project> --backlog-routing "<TEXT>"          # free text: verbatim to the jira agent; a label list for github-issues
h9k task link-issue <task> 123                                # record a GitHub issue, verified against gh first
```

`h9k task publish` checks the policy twice. First, before publishing: a tracking policy (`jira` or
`github-issues`) on a task that carries no linked item yet and has no publication already pending
refuses the publish outright, naming the three ways forward — link an existing item
(`h9k task link-jira` / `h9k task link-issue`), attest none exists and proceed with
`h9k task publish <id> --no-existing-item`, or attest that this task should skip tracking
altogether with `h9k task publish <id> --untracked`, for internal chores and platform tasks that
should not pollute a team's tracker — so a human or orchestrator confirms no existing item covers
the objective, or deliberately opts out of tracking, before a duplicate can be minted.
`--no-existing-item` and `--untracked` say opposite things and are refused together as
contradictory; `--untracked` on a project whose backlog policy is `none`, or any policy this build
doesn't recognize, is refused as meaningless — there is no tracking there to skip; and
`--untracked` on a task that already has a publication request outstanding
(`h9k task push-to-jira`, run by hand while still a Draft) is refused too, since that session
mints its card regardless of the flag — link it or wait for it instead. Both attestations land on the same
`TaskPublished` event, recording who chose which and when; `h9k task show` renders an
untracked-by-choice task honestly, distinct from one that predates the policy or was published
under policy `none` (both leave the attestation unset rather than defaulting to a look-alike
state). Then, after publishing: `jira` appends the
same request `push-to-jira` does (so a project with no Jira connection registered yet is told once,
at publish, rather than refused — `push-to-jira` remains the manual retry once one exists);
`github-issues` runs `gh issue create` itself, reads the created issue straight back the same way
`--from-issue` does, and records it through `link-issue` — the platform's own creation claim gets
the identical observation gate an agent's does. A task adopted with `--from-issue` or `--from-jira`
already carries its reference, so the pre-publish gate never fires and publishing it creates
nothing a second time. Closeout comments a merged pull request onto a linked GitHub issue exactly
as it does a linked Jira card — never a transition, same reasoning as above.

**A team's branch convention is a project setting, not a fork of the platform** (Decisions Log
#121). `h9k project set <project> --branch-template "<TEXT>"` names a task's branch out of three
tokens — `{shortid}` (the task's short id), `{slug}` (its objective, hyphenated, capped at 30
characters) and `{key}` (the linked Jira key or GitHub issue number) — with everything else
literal, so `--branch-template "{key}-{slug}"` cuts `ARX-14-add-rate-limiting`. The default is
`task/{shortid}-{slug}`, exactly what the platform cut before the setting existed, so a project
that sets nothing sees no change at all; `none` restores it. Two rules make it safe rather than
merely convenient. The template is rendered and checked as a legal git ref at `project set` time,
so a name git would refuse is refused where a human can still fix it. And every token is fixed at
or before dispatch, because the rendered name is recorded on the run and pushed verbatim when the
pull request opens much later: the id cannot change, the objective cannot be revised once the task
leaves Draft, and an external item cannot be relinked to a different one. A branch name that could
drift between those two moments is the same failure a hand-renamed branch caused on the Windows
node on 2026-08-31, where the push hit a refspec that no longer existed and the task parked Failed.
A task carrying no linked item renders `{key}` as `no-key` — nothing was observed, said out loud,
rather than an empty segment or an invented card number.

CI runs build + test on ubuntu and windows for every push/PR to main.

## The orchestrator window

**Who this section is for.** An *interactive* Claude Code session in this repo is the
**orchestrator window** (PLAN.md §2, §12): the conversational surface over `h9k`, driving the
platform on a human's behalf. Since the cutover (backlog 52) landed, "this repo" is the project's
own home, opened at `~/.hall9k/projects/hall9k/repo/dev` — the same `repo/dev` worktree shape
every project's home takes, not a standalone checkout. A *headless* session the daemon dispatched
is not one. If you arrived with a task id, a worktree and acceptance criteria, this section is not
your job; read it only to know what the window above you is doing, and go to the coding standards
below.

Everything here has been done live, by one interactive session, through the whole v0 build. It is
a record of what proved out, not a proposal.

### The role: a window, not an alarm

> An interactive Claude session is a *window you look through, not an alarm*: it checks when
> prompted. (PLAN.md §12)

Concretely: run `h9k status` when the human asks how things are going, when they come back to the
terminal, and after you dispatch something. Do not sit in a polling loop, do not sleep-and-check,
and do not promise to tell them when something finishes. Desktop notification is `h9k watch
--notify`'s job (unbuilt), never a session's.

The window is **stateless and disposable**. Every fact lives in Postgres, so nothing is lost when
the session ends, and nothing you remember is authoritative if the database disagrees. Re-read
rather than recall: `h9k status`, then `h9k task show <id>`, then `h9k logs <id>` for the run
transcript when the first two have already named the task worth digging into.

### The law: all new work enters through `h9k task add`

The flip is live (Decisions Log #17): Hall9k builds Hall9k. An orchestrator session **never
implements a platform feature directly**, however small the change looks and however much faster
it would be to just do it. It drafts a task, publishes it, assigns it, and lets a dispatched agent
do the work.

```bash
h9k idea add "The attention pane should teach the next command"     # not sure yet what it is
h9k task add --project hall9k --objective "…" --criteria "…"        # a Draft: identity, not readiness
h9k task publish <id> --assign                                      # the gate, and the go signal
```

Three things are outside the law, because they are not platform features:

- **The planning docs.** Appending a decision to PLAN.md §16, amending SLICE-1.md: this is the
  window's own work product, and it is what a task is authored *from*. The in-tree `backlog/` is
  a dogfood-era archive (see `backlog/README.md`) rather than a live target — a new backlog-shaped
  item goes through `h9k idea add` / `h9k task add` and renders into the project home instead
  (backlog 48).
- **Reading anything.** Inspecting the tree, the streams, the logs, a PR diff.
- **Unbreaking the platform when the platform is what is broken.** A daemon that will not start
  cannot dispatch the task that fixes it. Do the smallest thing that restores dispatch, then task
  the real fix.

Everything else is a task. When the human says "just quickly add X", the answer is a draft, and
`h9k task publish --assign` is how fast looks around here.

### The command surface

The full surface is in *Build / test / run* above. These are the ones the window lives in:

```bash
h9k status                                   # the attention pane: needs-you, stalled, running, blocked
h9k task list --state needs-you              # the pane bounds each section; this is the rest of one
h9k task list --project hall9k --state draft # what is written but not yet gated
h9k task show 28b19893                       # one task: contract, dependencies, runs and their worktree/branch/sessions, PR, conversation
h9k logs 28b19893                            # that task's newest run transcript (--raw for stream-json)
h9k project list                             # every project with its tasks counted by attention bucket
h9k daemon status                            # a quiet queue is usually this
```

`h9k status` leads with a red line when no daemon is running, because a stopped daemon queues work
without dispatching it and a silent board is otherwise indistinguishable from a calm one. Check
that line first when nothing is moving. Know its limit, though: it probes this machine's pid file,
so it answers "is a daemon alive here", not "is a daemon serving this database". Point the CLI at
a second database (`HALL9K_CONNECTION_STRING`) while a daemon runs against the first and the pane
reads healthy while nothing will ever claim the queue (found by the S1-13 verification session,
2026-08-22). On the default install there is one database and one daemon, and the line is exact.

Everything the CLI can do is discoverable from `--help`, and every command carries a worked
example (see *CLI command standards*). Read the help rather than guessing at a flag: a wrong call
prints the command's own help back at you, so one bad invocation costs one command, not a search.

### The judgment the window owns: sequencing the ready set

The dispatcher is deliberately mechanical. It takes queued tasks in order, up to the node's
run ceiling (`--max-concurrent-task-runs`, #64, #111) and its periodic token-spend budget
(`--spend-budget`, #120) — a motionless queue can be either gate holding, not just the ceiling —
and it has **no idea whether two of them collide**. `--blocked-by` enforces
sequencing, but only as declared: the graph is enforced, never inferred. Inferring it is the
window's job, and it is real work.

Before assigning a batch, estimate each task's likely file footprint and decide:

- **Run in parallel** when the footprints are disjoint. Two tasks in different vertical slices
  (`Features/Idea/` and `Features/Run/`) genuinely do not see each other.
- **Serialize with `--blocked-by`** when they would rewrite the same file. The recurring shape in
  this repo is the **shared append point**: a new project setting touches the same six-file chain
  every time (`ProjectSettingsChanged`, `ProjectDecider`, `ProjectAggregate`, `ProjectDetails`,
  `ProjectSetCommand`, the CLI registration), two tasks appending a decision both claim the same
  PLAN.md §16 number, and every task that teaches something appends to AGENTS.md. Those conflicts
  are mechanical to resolve and expensive to discover at merge.
- **Run alone** for a wide rewrite that touches a layer rather than a slice.

A collision guess costs latency; a miss costs a rebase conflict. Both are survivable, so prefer
latency only where the collision is real rather than serializing the queue by reflex.

State the reasoning when you assign. "13 is held behind 09 because both rewrite the dispatch loop"
is the sentence a human needs in order to overrule you.

This judgment is documented as a gap, not as a permanent human duty:
`backlog/IDEA-coordinator-agent.md` is its eventual automation, a coordinator agent that reads the
ready set, estimates footprints, and authors `--blocked-by` edges with a recorded why per edge.
The graph it would write to already exists (#34); what it still waits on is enough dogfooded manual
edges to know what a good one looks like. Until then, the edges in the graph are the ones you put
there.

### Questions and answers: the relay

`h9k status` is where the platform asks for a human. Its **needs-you** section is the whole point
of the pane, and today a row lands there for one of six reasons:

| Row says | What happened | The lever |
|---|---|---|
| `NeedsHuman`, review parked | The pre-PR review loop spent its automatic fixes, hit a disputed finding (#24, #63), or the task's lifetime review-cycle budget is spent (#112) — that last one can fire on a run that just converged cleanly, since the budget counts every run and follow-up the task has ever had and nothing resets it; a `--needs-fixes` grant there earns one more cycle but re-parks at the next settle point unless the budget itself is raised with `h9k task set-review-caps` | `h9k review resolve` |
| `NeedsHuman`, closeout parked | The same obstruction survived its automatic-lap cap without clearing, or the pull request's lifetime automatic-closeout budget is spent (#22, #80) | `h9k pr resolve` |
| `NeedsHuman`, dependency failed | A blocker died, so the dependent stays Blocked rather than silently unblocking (#34, #61) | recover the blocker |
| needs-you, Jira write pending, Status unchanged | A Jira write (an operator's own `write-jira`, or a daemon-dispatched one such as closeout's own merge comment) is stuck on a rejected credential (#102, #114) — the write carries no lifecycle state of its own, so the row's Status stays whatever it already was (Working, Delivered, or Done) | `h9k connection add jira` |
| needs-you, "an interactive claim (h9k task work) last recorded activity …" | An `h9k task work` claim has sat untouched past the configured threshold (default 3 days) — closing the terminal is a normal way to leave an interactive claim (#103), so nothing reclaims it automatically; this is only a nudge asking whether it is still yours | `h9k task work <id>` if you're still on it, or `h9k task handback <id>` to finish it headlessly |
| `Failed` | The run itself failed | `h9k task retry` / `resolve` / `abandon` |

Once `h9k connection add jira` records a working credential again, the daemon's retry sweep
resubmits the identical pending write on its own; nothing needs recomposing.

The window's job at each of these is the same: read the reason (`h9k task show`, then `h9k logs`
if the reason is not already sufficient), put the decision to the human in a sentence, and record
their answer through the lever. **Relay, do not decide.** These rows exist precisely because the
platform refused to guess (#11, never loop on judgment), and a window that guesses on the human's
behalf has re-introduced the thing the park prevented.

**Mid-run questions are Slice 2.** The design is settled (#5: the agent calls `h9k ask` and
*exits*; `h9k answer` resumes the session with the answer injected, so a run parks for hours
without holding a process open) and the `QuestionAsked` / `AnswerProvided` events are already on
the task stream. The `ask` and `answer` commands are not built yet, so an agent that needs a
decision today has to make the most reasonable call and record the assumption in its handoff.
Do not tell a human they can answer a running agent; they cannot, yet.

### The recovery levers

Five levers, and picking the wrong one loses work. The question that separates them is *what
actually failed*.

| Lever | Use it when | What it does |
|---|---|---|
| `h9k task retry <id>` | The task is **Failed** and the machinery is what failed (a daemon bug, a dead process, a push that was rejected). The work has to run again. | Requeues the task. The failure stays on the stream. The new run resumes the failed run's branch when it survived, or starts clean from the base branch when the artifacts are gone (#25). |
| `h9k task resolve <id> --reason "…"` | The task is **Failed** but the objective was met anyway: the work merged, or you finished it by hand, and only the bookkeeping died. | Ends the task Done on your attestation. `--reason` is required (an attestation without a why is a guess) and `--pr` records where the work landed and, when it names a real pull request on the project's own repository, enrolls that pull request in closeout's orphan sweep too, so its later merge completes this task's closeout exactly as it would for any watched run (#27, #116) — except on a **pr-review** task, whose `--pr` names the pull request it reviewed rather than one of its own, and is never enrolled. |
| `h9k task abandon <id> --reason "…"` | You have stopped believing in the work. Reaches every non-terminal state, drafts and published tasks included. | Terminal. Releases any lease. Nothing is deleted: the reason is the record. |
| `h9k pr resolve <id> [--checks \| --rebase]` | The task is **Done**, its pull request is open, and review feedback, failing CI, or a conflict with its base branch needs another pass, either because the monitor spent its budget or because you want one now (`--rebase` is for when you spot the conflict before the monitor's next inspection does, backlog 44). | Dispatches a follow-up run onto the existing PR branch and resets the monitor's automatic retry budget (#20, #22). |
| `h9k review resolve <id> --merge-ready [--reason "…"]` / `--needs-fixes "<why>"` | A run parked **before** its PR, in the internal review loop, and is waiting on your verdict. | `--merge-ready` runs one mandatory full-scope verification gate over the fix unless this tip was already gated at full scope (#98: nothing merges on scoped green alone) and proceeds to the pull request if it passes; `--needs-fixes` dispatches a fix session with your reason as its findings and restores the fix budget (#24). `--merge-ready` is refused when the park is a disputed rebase conflict (nothing has been rebased yet, so there is nothing ready to merge) — only `--needs-fixes` applies there. Either verdict's reason is recorded on the task and carried into every later review pass as a settled ruling (#88) — except on a thread-dispute park (#62), which settles a disputed thread before any reviewer ever read the diff and so is not recorded as a review ruling — so pair `--merge-ready` with `--reason` when you dismiss a finding — e.g. the evidence that dismissed it — rather than leaving the next fresh-context reviewer to rediscover it. A **pr-review** task's own park (§16 #99) refuses `--needs-fixes` outright — it has no diff of its own for a fix session to apply — and takes only `--merge-ready`, once you have walked the findings report and directed each one by hand (`walk-pr-review-findings`); that verdict never opens a pull request, it closes the task Done directly. |

Two distinctions worth keeping straight, because they are the ones that get confused:

- **`review resolve` is pre-PR; `pr resolve` is post-PR.** If there is no pull request yet, the
  park is the internal reviewer's and `review resolve` is the lever. If there is one, it is
  closeout's and `pr resolve` is.
- **`task retry` re-runs the work; `task resolve` declares it already done.** Retry when the
  objective is unmet, resolve when it is met and the run merely failed to say so. Retrying
  finished work rebuilds it; resolving unfinished work loses it.

Failed is a waypoint, not an end (#27): a failed state means there is an unsolved problem, and an
unsolved problem is not an outcome. Exactly one of retry, resolve, or abandon closes it, and all
three are human-only on purpose.

### The review rhythm

The checkpoints, in the order the window sees them:

1. **Agents build.** The dispatched run does the work in its own worktree, on
   `task/<id>-<slug>` — or on whatever the project's own branch template renders (Decisions Log
   #121), which is the same name either way for a project that never set one.
2. **Gates run.** Build, test, lint, per the project's verify settings. A fix cycle's `dotnet
   test`-shaped gate is scoped to the tests reachable from that cycle's own touched commits
   (#98) whenever `TestScopeResolver` can map every touched file with confidence; it falls back
   to the full suite on anything it cannot read or map, and the run's first gate pass plus the
   mandatory `FinalFullPass` immediately before the pull request (#92) always run full regardless
   — nothing merges on scoped green alone.
3. **The internal reviewer checks the diff before the PR exists** (#24, #59, #63): two lenses,
   conformance and adversarial, dispatched together as **tracks**. Each track carries its own
   cycle count and its own cap, and each ends when its own rule says so, so a clean conformance
   track goes dormant at cycle 2 while the adversarial one keeps finding things alone at cycle 5.
   Differing cycle counts on one run are the design rather than a fault, and that is the sentence
   a human needs when they ask why. A finding the loop cannot settle parks the run, which is where
   `review resolve` comes in. A needs-fixes verdict that names no finding is recorded the same as
   a missing verdict, not accepted as a real answer (Decisions Log #86): it gets the cycle's one
   same-session re-prompt before parking, exactly like a pass that ended with no `VERDICT:` line
   at all. A needs-fixes verdict earns a fix-and-re-review cycle only when a finding is graded
   medium or high (Decisions Log #87) — narrower still on the mandatory FinalFullPass immediately
   before the pull request opens, where only a High earns one (Decisions Log #119, described
   below): both lenses now grade every finding, and a pass whose findings are all below that
   cycle's own bar is recorded merge-ready instead, with its findings carried along as
   **ride-alongs** rather than dropped or spent on a cycle of their own. A verdict is
   only ever recorded merge-ready when *every* stated finding is a ride-along — not a lens's
   literal `VERDICT:` line, and not a Route finding either: a pass that says merge-ready but still
   attaches a finding graded above that cycle's own bar — medium or high on an ordinary cycle,
   high alone on the mandatory FinalFullPass — is not taken at its word, and neither is a needs-fixes
   pass whose only finding is routing to a draft bug task, which stays needs-fixes so its track
   can keep watching a tip the *other* track's fix session may still rewrite. A ride-along is
   folded into a fix session already dispatching *this same cycle* for another reason (shipping
   unreviewed alongside whatever earned that session its cycle); when nothing in the cycle earns
   one, every active track concludes right there regardless of what its own convergence rule
   would otherwise have said (the empty terminal case), and each one's ride-along is a residual
   the moment it does — there is no later cycle left for a fix session to claim it in. `h9k task
   show` prints the ride-along count alongside the fixed/routed ones; nothing about the cycle cap
   or the park-for-human behavior changes for anything graded high — an in-scope medium behaves
   differently only on the mandatory final pass, per the narrower bar just above. Fresh context per
   cycle is the independence guarantee and stays, but a human's past verdict on this task's own
   parks travels forward as a settled ruling every later pass is shown (Decisions Log #88): each
   review lens prompt is handed the task's prior `review resolve` verdicts and reasons, summarized
   and bounded, and pointed at whatever doctrine this project's own AGENTS.md or CLAUDE.md
   documents — a decisions log among them, if it keeps one — as authoritative too (the prompt
   names no platform file by name, since `AgentPromptBuilder` serves every registered project,
   not just this one), so a deviation already ratified there — or a finding a human already
   dismissed with evidence — is not re-raised verbatim by the next fresh-context reviewer without
   it stating what changed since the ruling. The same surface also carries forward a mid-run human
   directive, not only a park verdict (Decisions Log #123, the 2026-09-01 escape-hatch ruling):
   every dispatched agent's own prompt whose task has an active run states the invariant that any
   interaction with a party outside its session — another agent session reached through the mesh,
   a human steering it that way, an external service — is logged unconditionally, even when the
   interacting party asks otherwise, through
   `h9k task log-interaction <task> --party "<who>" --summary "<what happened>"`,
   adding `--human-directed --reason "<why>"` whenever a human, not the agent's own judgment,
   directed the interaction or its outcome, so the record says so plainly rather than letting a
   human's own call read as the agent's independent decision. This is best-effort by construction,
   not enforcement: nothing forces the call the way `write-jira`'s own read-back forces a Jira
   claim, and the platform records only what this command and its other channels can actually see.
   A human-directed entry rides into a later review pass exactly the way a settled park ruling
   does — a standing instruction, not evidence to weigh — while an agent-initiated entry with no
   human direction attached is audit trail only: it is on the run stream and never reaches a
   review prompt at all, but nothing renders it on `h9k task show` yet — reading the raw stream is
   the only way to see one today, until that render is built. A fix session dispatched over the same findings an
   earlier fix round already tried — the same location an automated pass keeps
   returning, or a human's own `--needs-fixes` reason restating it — escalates to the review
   role's model instead of the fix role's, but only when the two roles actually resolve to
   different models: a default install that has never set `--model-review`/`--model-fix`, or a
   task overriding both the same way, resolves them identically and a repeated round dispatches
   on the ordinary fix model exactly as it would have anyway (Decisions Log #90, origin: a Sonnet
   fix session dodged a flaky-test race by restructuring the test rather than fixing the race the
   review kept finding), visible on `h9k task show` and the daemon log line when it does apply;
   de-escalation is automatic the moment a later round moves on to a genuinely different finding.
   A fix session's own work ends with a mandatory self-check phase before it hands back (#113),
   scaled down from the build session's own adversarial self-review above to the size a fix round
   actually is (one pass, not a loop): for every finding it fixed, it enumerates every other site
   sharing that defect's shape — inside this branch's own changes or pre-existing on the base —
   and fixes or clears each one inside the branch's own changes, naming rather than fixing a
   pre-existing sibling outside them — unless that sibling itself carries a "fix in its own
   commit" disposition, in which case it is fixed in that same separate commit instead of merely
   named. An explicit disposition on the sibling always wins, so a sibling separately marked "do
   not fix here" stays routed away rather than being pulled into that commit. When the sibling
   carries no disposition of its own, the finding whose sweep surfaced it decides instead: a sweep
   surfaced by a "fix in its own commit" finding fixes that undispositioned sibling in that same
   separate commit too, rather than merely naming it. The phase also states, for every replaced
   behavior, what the old code did that the new code no longer does and confirms the difference
   is intended; and it runs the touched tests in the foreground and waits for them to finish,
   rather than backgrounding them and trusting the platform's own re-verify to catch what it left
   behind. An out-of-scope finding
   this pull request is not fixing still has to land somewhere (#63): a
   Medium or higher mints a draft bug task of its own, unchanged, while a Low instead folds into
   the project's one standing sweep draft — the board shows it as `Sweep: consolidated
   out-of-scope review findings` (Decisions Log #117) — so eight one-line pre-existing defects cost
   one build-gate-review-PR pipeline instead of eight. Its footprint is wide by construction (it
   touches as many unrelated files as it has items), so it is groomed and published by a human and
   assigned alone, with no parallel siblings queued beside it, exactly as *the judgment the window
   owns* above prescribes for any wide-footprint task.
   Only cycle 1 pays full two-lens discovery (Decisions Log #92, origin: 576M input tokens in one
   day re-reading 12k-line diffs with two lenses to judge 40-line fixes): a middle cycle instead
   dispatches one **Verify** reviewer, handed the prior cycle's own findings, each finding's fix
   position, and the commits added since that cycle, whose job is to confirm the fix and check its
   blast radius rather than rediscover the diff — its rounds count against the same per-track caps
   a full cycle's would, and a dispute or cap-out parks exactly as before. A Verify pass resolves
   its own model, `--model-review-verify` (Decisions Log #105), separately from the plain Review
   model Discovery keeps resolving — the knob defaults to whatever Review itself
   resolves to (no behavior change until set), and the standard install points it at the fix
   model, since Verify's confirm-the-fix-and-check-blast-radius job is the most mechanical review
   shape in the loop. Fix-round escalation (#90) still compares the plain Review and Fix models
   only, never this knob, so a repeat finding a Verify pass itself reported escalates exactly as
   it would have from Discovery. Immediately before the
   run may settle, one mandatory **FinalFullPass** runs both lenses fresh, whether or not a track
   had already gone dormant, so nothing reaches the remote on delta-green alone; a track it
   reawakens with a real finding is recorded reactivated rather than left stuck at an old
   conclusion, and a run that converges clean at cycle 1 pays no extra pass at all. That mandatory
   pass resolves its own model too, `--model-review-finalpass` (Decisions Log #130, completing the
   per-stage model set #105 started for Verify) — the expensive read at 43 percent of all review
   input tokens per the 2026-09-01 architecture review's own measurement — defaulting to whatever
   Review itself resolves to exactly like Verify's own knob, and independent of it: setting one
   never touches the other. Fix-round escalation (#90) carries the identical carve-out here too,
   never this knob either. "Fresh" is
   about context, not diff range: a FinalFullPass whose run already paid for an earlier full-scope
   read (this run's own opening Discovery, or an earlier FinalFullPass) reads only the commits
   since that read's own head, not the whole branch again (Decisions Log #115) — falling back to
   the full diff whenever no such boundary is on record or it no longer resolves against HEAD
   (a history rewrite between cycles), never a guessed one. Every full-scope read still starts
   exactly where the previous one left off, so #92's own rule still holds: no commit reaches the
   remote unread at full scope by a fresh context, only reread by fewer of them. That mandatory
   pass also tightens its own in-scope fix bar to High alone (Decisions Log #119, origin: 3 High
   findings in 172 final passes, 101 of 104 needs-fixes final passes carrying no High at all): an
   in-scope Medium there rides along exactly as a Low already did, rather than earning a
   fix-and-reverify cycle of its own, and every ride-along the pass carries is named on the pull
   request body and counted on `h9k task show` rather than dropped silently. Which shape a
   cycle ran under — Discovery, Verify, or FinalFullPass — is a deterministic engine decision
   recorded on the run stream, so `h9k task show` and the daemon log say which one dispatched;
   only the review content itself is agent judgment.
4. **The daemon opens the pull request.** Agents never do, and there is deliberately no create-pr
   skill. The task reaches **Done** here, when the pull request opens, so Done means "the work is
   on a PR and waiting on review" rather than "merged". A **pr-review** task is the one exception
   (§16 #99): there is no diff of its own to open a pull request over, so its own park is resolved
   with `h9k review resolve --merge-ready` directly, and the task reaches Done there instead —
   still without any pull request ever opening or merging.
5. **Copilot and the human review it.** The closeout monitor reads unresolved threads and
   dispatches follow-up runs to answer them, bounded by a retry budget (#22, #62).
6. **The human merges.** The platform never merges. The observed merge is true closeout: it is the
   moment the run completes, dependents unblock, and the worktree is removed. The task was already
   Done; what the merge changes is everything around it.

The window's part is steps 3, 5 and 6: relay the parks, tell the human when a PR is waiting on
them, and never start a review thread yourself. That last one is not a style preference; the
thread discriminator depends on it (see *Git rules*).

Two things follow from step 6 that are easy to get wrong. A dependency is met only at the merge,
so a task showing Done with an open PR does **not** unblock its dependents yet. And an unsubmitted
GitHub review is invisible to the API, so a quiet PR may have a half-written review on it: never
report silence as "the reviewer had nothing to say".

**A pull request that is the base of a stack (another branch cut from it instead of from main) is
reviewed commit by commit, every one of them: checking only its own final tip is not enough.**
Reviewing the stack's cumulative diff, the shape the top branch's tip naturally shows, can look
clean while an intermediate commit on the base itself leaves the tree broken; even building the
base pull request's own final tip is not enough on its own, because a later commit on the base can
silently repair what an earlier one broke, leaving no trace at the tip for either side to catch.
Origin incident (ARX-4836, 2026-08-31): a leaked file move broke the base pull request's own build,
and nothing caught it because the author verified only the stack's tip and the reviewer built
nothing at all: the base's intermediate state was never independently exercised by either side.
This is a working agreement today, for whoever reviews a base-of-stack pull request by hand, not a
platform behavior: `h9k`'s own dependency graph carries no stacked-on edge yet for a review prompt
to key on, so folding this into the automated review loop waits on stacked-PR slice 1 (backlog:
`IDEA-stacked-prs.md`, task 28e400d9).

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
- Branch naming for task work: `task/<id>-<slug>` unless the project set its own convention
  (`h9k project set <project> --branch-template`, below), created off `origin/main` with
  `--no-track`.
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
- **An agent never pushes; the daemon pushes every branch — fresh or follow-up — with
  `git push --force-with-lease`, never plain `--force`.** Rewriting history per the rule
  above is safe: verify tree identity, finish, and let the platform push. Origin incident
  (2026-08-17): the first two automatic follow-up runs rebased correctly, the daemon's
  then-plain push rejected both rebased branches ("failed to push some refs"), and both
  runs failed with completed, gated work stranded in the worktrees; this file briefly
  told agents to run the force push themselves as the workaround, until the daemon became
  force-aware (decision #26) — but #26 kept the plain push for a first run and gated the
  lease on `RunDispatched.IsFollowUp`; the push became unconditional across fresh and
  follow-up branches alike only later, under decision #103's `h9k task deliver` change,
  which made `IsFollowUp` an unreliable proxy for "does a remote copy already exist" —
  decision #104 did not widen that scope. What #104 added is a guard in front of the lease:
  a fresh build session's own end-of-work checkpoint recompose
  (below) can make a retried run's branch diverge from a tip this same task already pushed,
  and a bare lease could pass — and silently overwrite — a tip this run never accounted for,
  so the daemon's pull-request push now runs an explicit ancestor-or-reflog check before
  pinning the lease's expected value, refusing the push outright rather than forcing over
  anything it cannot account for (decision #104).
- **Commit as you go during a fresh build session, then recompose once, right before you
  finish.** Checkpoint commits along the way are crash protection, not authored history —
  they exist so an abnormal ending (context exhaustion, an early exit) strands at most the
  increment since the last checkpoint. Once the full verification suite is green and every
  checkpoint is committed, the session hunts its own finished diff for defects before recomposing
  anything: an adversarial self-review, capped at two rounds, that reads a fresh
  `git diff origin/<base>...HEAD` — naming `origin/` because a worktree's local base-branch ref is
  routinely stale relative to the task's actual base — and runs three mandatory hunts (a
  refactor once-over, a blast-radius sweep for sibling sites, and actually executing any documented
  procedure in the branch's diff — whether the session authored it this session or it arrived
  already in the diff a resumed run inherited — rather than proofreading it, inside a scratch
  directory made with `mktemp -d` outside the worktree wherever the procedure mutates state, and
  read rather than run instead whenever no relocation makes it safe — a live daemon or its
  database, a machine-wide install, a destructive maintenance command, or a write to an external
  service — recording why in the summary and handoff rather than in a finding, since a procedure
  read as correct produces none). A
  correctness-or-behavior finding is fixed and checkpoint-committed, or left with a stated,
  checkable reason it is not actually a defect; a
  style-only finding is fixed in place and checkpoint-committed, or skipped outright with nothing
  to commit. Whenever a fix does land — style-only included, not only a correctness-or-behavior
  one — the full suite re-runs before the loop continues or the recompose begins, since a fix that
  broke something is itself a defect the recompose must not ship silently; a skipped finding earns
  neither a commit nor a re-run. Origin: two full external review laps
  in one afternoon (2026-08-30, tasks cea5ae6e and b6dfcbe5) that a same-session hunt would have
  caught before it ever reached a reviewer. Once that phase has run its course, record the
  pre-reset tip (`git rev-parse HEAD`) — the tree-identity check below verifies the recompose
  against it — then reset to the branch's own fork point, never the moving tip of
  `origin/<base>` itself (that ref lives in the shared repository and can move mid-session).
  Capture the fork point into a variable and stop if it does not resolve; never
  inline the substitution directly into the reset — an unresolved `origin/<base>` makes
  `git merge-base` print nothing and exit nonzero, and `git reset --mixed $(...)` on an empty
  substitution silently becomes a bare `git reset --mixed`, a no-op that exits 0 as though the
  recompose had happened, with the tree-identity check below unable to catch it (the diff
  would compare HEAD against itself and read clean):
  `FORK_POINT=$(git merge-base origin/<base> HEAD)`
  `test -n "$FORK_POINT" || { echo "no fork point resolved — stop here, do not reset" >&2; exit 1; }`
  `git reset --mixed "$FORK_POINT"`
  Then immediately recompose that tree into real history — the commit-plan skill, if this repo
  ships one, or the same mechanics done by hand — with nothing in between (no test run, no fix)
  so the recomposed commits provably describe the exact tree that passed the suite. Verify tree
  identity afterward (`git diff <old-tip> HEAD`, against the tip recorded before the reset, must
  print nothing, the same check the narrative rebase path requires), and the session is not done
  while `git status` shows anything uncommitted or untracked — check it last and commit whatever
  remains. Origin incident (2026-08-29): three no-commit strandings in one night, each a large
  session that finished its work and left everything uncommitted with nothing to recover from an
  abnormal ending (decision #104).

## Repo skills

Repo-resident Claude skills live in `.claude/skills/` and are available in every worktree:

- **absorb-review-fixes** — fold review-feedback fixes into their owning commits (fixup +
  autosquash + tree-identity check) so the PR branch stays authored history
- **commit-plan** — organize the working tree into cohesive, buildable commits ordered for PR review
- **resolve-review-threads** - triage every unresolved review thread on an existing PR,
  whoever opened it (Copilot, a teammate, or the author's own self-review): fix, reply
  in-thread, resolve. Supersedes the Copilot-only `resolve-copilot-reviews` skill (§16 #62)
- **rebase-onto-main** — bring a PR branch conflicting with its base current: replay its own
  commits onto the moved base, resolve conflicts with judgment, never leave a conflict
  marker, re-run the verification gates against the rebased tree. The inverse of
  absorb-review-fixes (that one folds new fixes in, this one replays existing commits
  forward) (backlog 44)
- **pr-summary** — generate a PR title/description from the branch's commits (text only — the
  daemon opens PRs; agents never do)
- **walk-pr-review-findings** — walk a pr-review task's findings report with the owner, finding
  by finding, and post only what they direct (a batched GitHub review or a plain comment) on
  their explicit go, under their own login. Use once a pr-review task (§16 #99) parks NeedsHuman
  with a findings report

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
