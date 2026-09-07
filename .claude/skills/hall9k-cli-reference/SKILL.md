---
name: hall9k-cli-reference
description: The full h9k/h9kd command surface and the domain semantics behind it — the board's state/phase/attention model, project homes, task and idea lifecycles, epics, Jira and GitHub backlog integration, branch templates, and auto-pr-review. Load this when a task needs CLI syntax or platform semantics beyond the four dev commands and the rules in AGENTS.md's own "Build / test / run" section.
---

# The hall9k CLI reference

`AGENTS.md` keeps only `dotnet build` / `dotnet test` / `dotnet run --project src/Hall9k.AppHost` /
`docker compose up -d` and the rules a session must obey on every turn. Everything else about
operating the platform through `h9k` lives here, load it on demand rather than assuming a
dispatched session was pre-briefed on it.

```bash
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
h9k project set <name> --auto-pr-review off|normal|first|now   # a GitHub reviewer assignment to this install's own login auto-starts a pr-review task; default off (Decisions Log #34's amendment, #133)
h9k project set <name> --max-parallel-tasks <N|default>   # this project's own ceiling in TASK RUNS, enforced by the dispatcher: a ceiling never a reservation, 0 pauses the project (held even on an idle node, and nothing but a human raises it), 'default' clears it so the node ceiling alone decides; takes effect next dispatch cycle, no restart. --max-parallel is a quiet alias; the old session-denominated value it used to record is retired, not converted (Decisions Log #140)
h9k project set <name> --priority high|normal|low|default   # which tier this project's ready work competes in for a FREE dispatch slot. Default normal, and free slots rotate: the eligible project longest unserved wins the next one, oldest task first within it (nothing to set on a single-project node). 'high' is focus — wins every free slot over lower tiers while it has ready work and RELEASES ITSELF when its queue drains, which is the opposite of the sticky --max-parallel-tasks 0 pause. 'default' is the clearing word, restoring normal. Nothing preempts; every claim logs why that project won (Decisions Log #141)
h9k project set <name> --claim-gate off|tracker-assignee   # a task linked to a Jira card or GitHub issue is claimed on this install only while the tracker shows that item assigned to this install's own identity; default off (Decisions Log #142)
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
h9k task add --project <name> --objective "…" --stacked-on <id>   # a STACKED edge (Decisions Log #144), not a plain blocked-by — see below
h9k task revise <id> --stacked-on <id>            # declare the stacked edge on a Draft; --clear-stacked-on drops it (leaving the blocked-by alone)
h9k task revise <id> --queue-first                # the one revision Draft-only doesn't gate: marks the task-level queue-first fact (Decisions Log #127), settable in any live state; --clear-queue-first removes it
h9k task revise <id> --clear-interactive-mode     # the other revision Draft-only doesn't gate: clears the interactive-mode flag (below) directly, settable in any live state, for when neither h9k task handback nor a default h9k task release has an active interactive claim left to act on
h9k task revise <id> --review-stage-composition <VALUE|default>   # Draft-only, unlike the review caps below — a live change reaches only the task's next run (Decisions Log #129)
h9k task set-review-caps <id> --max-compliance-review-cycles <N>   # a task-level review-cycle-cap override, settable at any time — even while the run is live (Decisions Log #112)
h9k task publish <id> [--assign]                  # the readiness gate; --assign starts it too
h9k task publish <id> --no-existing-item          # required if a tracking backlog policy finds no linked item yet and has no publication already pending
h9k task publish <id> --untracked                 # the same gate's other exit: deliberately skip tracking for this task, attested on the stream
h9k task publish <id> --pre-approved              # the owner stops being a synchronous gate at the pull request: the daemon rebase-merges once every real gate (CI, review decision, requested reviewers, threads) reads satisfied (Decisions Log #135)
h9k task assign <id> [<owner>] [--take]           # the dispatch trigger — Queued, or Blocked on dependencies; --take also takes the linked card/issue for this install when nobody holds it, in a project whose claim gate is on (Decisions Log #143)
h9k task set-session-cap <id> <cap>               # override how many agent sessions this task's run may hold at once; settable any time, even mid-run (Decisions Log #111)
h9k task set-pre-approved <id> on|off             # flip standing pre-approval after publish, without the unassign/draft/revise/publish ceremony — settable on any live task whose pull request has not yet merged, Draft excepted (the flag is part of the readiness contract set at publish) (Decisions Log #135)
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

**A stacked pull-request edge is one explicit opt-in dependency and nothing is inferred**
(Decisions Log #144, Brian's cohesion ruling 2026-08-28). `--stacked-on <parent>` implies the
`--blocked-by` edge and adds six behaviours a plain blocked-by never gets: the child dispatches at
the parent's **Delivered** rather than its merge; its branch is cut from the parent's branch head;
its pull request opens **against the parent's branch**, forming a GitHub stack — unless that branch
is already gone from origin because the parent merged mid-build, in which case it opens against the
project's base branch, where the retarget below would have put it anyway, with the replay still
owed; its diff, review
packet, self-review hunt and end-of-work recompose are all computed against the parent's branch, so
its reviewers read the child's own delta; it is **not at the merge bar** while its pull request is
still aimed at the parent (the board never says "the merge is yours" and a pre-approved child is
not auto-merged); and when the parent's branch moves out from under it the daemon dispatches a
**mechanical replay** (one `git rebase --onto` between two exact commits — the boundary everything
at or before which is the parent's, and the freshly observed commit it lands on — then the gates,
and no review cycle at all, since nothing new entered the branch; the boundary is observed or
recorded, never derived from `git merge-base`, which a force-pushed parent makes wrong). A **merged**
parent additionally retargets the child's pull request onto the project's base branch, since the
parent's branch is going away; a **force-pushed** parent does not — the base stays the parent's
branch and only the replay is owed.
The replay is bounded by the child's own rebase budget (`DaemonOptions.MaxStackReplayRuns`,
default 12, separate from the lifetime closeout ceiling so a busy parent cannot spend the child's
review budget); past the cap the child parks with the reason. Reserve the edge for slices of one
feature that are genuinely cohesive — the interactive-mode pair is the canonical example — and
never reach for it just because one task happens to wait on another. A plain `--blocked-by` task
behaves exactly as it always has. Slice two, not built: the child still starts at the parent's
Delivered rather than at its build-complete-before-review.

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
`h9k task work` again re-enters the same worktree (Decisions Log #103). This claim always turns on
the task's own interactive-mode flag (task: interactive mode becomes a recorded property of the
task, design ruling R2): from here on, this task's run — and any later follow-up, retry, or reopen
of it — parks at each of the review engine's own four routine phase boundaries (build done to
review, review verdict to fix, fix to re-review, gates to pull request) for a recorded
`h9k review proceed` or `h9k review resolve`, whether or not a human is still building it (see
`ORCHESTRATOR-WINDOW.md`). `h9k task handback` and a default `h9k task release` are the two
ordinary exit doors that turn it back off; `h9k task revise <id> --clear-interactive-mode` is the
one that still reaches it once neither of those has an active interactive claim left to act on (a
headless follow-up dispatched under a real node claim while the flag is still on, or the task has
already reached Done with its pull request open). The same flag turns outbound reporting on too
(design ruling R8, idea fcaded0b): a dispatched build, review, or fix session reports judiciously,
never a running commentary, to the human's own registered session (`h9k task register-session`)
through the cross-session mesh's SendMessage tool — a small, fixed per-role vocabulary of
milestones, ending with each role's own end-of-phase report (a build's handoff, a review's verdict
and findings, a fix session's summary) as its last act before it ends normally. Every send, landed
or not, is logged through `h9k task log-interaction` so what the human was told lives on the run
stream rather than only in a transcript; with no registered session it skips sending and logs that
once for the phase, and with a registered session SendMessage cannot reach it still attempts and
logs each send individually — neither degradation holds up the boundary park above, which parks
exactly the same either way. No registered session is the ordinary case for a fresh headless
dispatch under interactive mode (`h9k task start`, an ordinary dispatch carrying the flag forward
from an earlier `h9k task release --keep-interactive`, or a retry, reopen, or follow-up redispatch
— each starts a new run, and no registration carries forward from an earlier one yet), so today the
build role's own milestones always take this skip-and-log path; only the review and fix roles,
dispatched later on the same run once a human's own `h9k task work` claim has registered against
it, can actually reach a live address. A handback is never one of these no-registration cases
either way: it clears the interactive-mode flag unconditionally, so a handback-dispatched run never
reaches these rules at all.

Delegating the build while staying at the wheel is its own command, distinct from handback
(task 15f889e3, design ruling R6): `h9k task delegate <id> --note "<text>"` dispatches a headless
build contractor onto the operator's own live interactive claim — the same run, worktree, and
branch, reused exactly as they stand, whether or not any work has been committed there yet — while
the task stays the operator's, still in interactive mode. It shares `h9k task start`'s own
ceiling-exempt mechanism (the claim's sentinel node id already exempts it) and its slice-1
`<task-shortid>-build` mesh name — the contractor's own prompt, stream, settings and stderr files
are instead keyed on a per-delegation discriminator, so a claim delegated more than once never has
one contractor's transcript truncated by the next — and sets `ResumesPreviousWork` whenever the
branch already carries a commit ahead of base, the worktree holds modified or untracked files, or
its git status could not be read at all, exactly as a resumed retry does for the commit case and
conservatively folds the other two into "assume this worktree already holds work." `--note` is
required and carries the
operator's own handoff into the contractor's starting prompt in the blocker-handoff mold (what was
attempted, what is deliberate versus abandoned, what latitude is granted); the prompt's own default
posture toward inherited work is conservative, and discard latitude exists only when the note
grants it explicitly, and the contractor's own end-of-work recompose (below) never rewrites a
commit that predates this delegation — only the checkpoints the contractor itself adds. Refused on
everything but that live claim — a headless claim, a different owner's claim, a claim whose
interactive-mode flag is off (`h9k task handback --now` turns it off deliberately; there is no
boundary park left for a contractor's report to arrive at), a pr-review sentinel claim, a run
already handed to the standard pipeline, or a worktree another session is still attached to all
refuse by name. The reverse move: once the contractor reports back, `h9k task work <id>` re-enters
the same worktree interactively so the operator can finish the build themselves — the wheel changes
hands in both directions, always at a boundary.

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
h9k task delegate <id> --note "<text>"   # dispatch a headless build contractor onto this same claim for one phase, staying in interactive mode
h9k task handback <id>               # release the claim to a headless agent partway through, resuming the branch
h9k task handback <id> --first   # same release, plus the queue-first marker: the next free slot takes it regardless of age (Decisions Log #127)
h9k task handback <id> --now     # same release, dispatched immediately, ceiling-exempt, through h9k task start's own mechanism — refused together with --first
h9k task release <id>                # give an untouched claim back to the dispatch queue; clears interactive mode by default (an exit door alongside handback)
h9k task release <id> --keep-interactive   # same release, but the task's interactive-mode flag survives it, so a later headless run still parks at each boundary
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
other way. A direct `h9k task start` invocation — unlike `h9k task handback --now`'s own call into
this same mechanism, which passes false — always turns on the task's own interactive-mode flag too
(task: interactive mode becomes a recorded property of the task, design ruling R2), the identical
fact `h9k task work`'s claim above always sets: the build here is headless either way, but from
this claim on, the run still parks at each of the review engine's own four routine phase boundaries
for a recorded `h9k review proceed` or `h9k review resolve`, so a task started this way is no
longer the fire-and-forget dispatch it was before this flag existed. The same two ordinary exit
doors, and the same `h9k task revise --clear-interactive-mode` fallback, turn it back off.

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

**A project can make the tracker's own assignment the one act that hands out work** (Decisions
Log #142, idea 64c75e43). `h9k project set <project> --claim-gate off|tracker-assignee` — default
`off`, today's behaviour byte-for-byte — claims a task linked to a Jira card or a GitHub issue on
this install only while the tracker shows that item assigned to this install's own tracker
identity, so on a team where every teammate runs their own install against their own database,
two installs cannot both run the same card. The identity is read from the tracker, never typed:
the Jira `accountId` `/rest/api/2/myself` answers (captured at `h9k connection add jira`, read
live and recorded on first use for a connection registered earlier), or the login `gh` is
authenticated as, read live on every check and never stored. Each check reads the assignee field
alone, so a task's one-time content snapshot is untouched, and records `TrackerAssignmentObserved`
when it passes. Every claim door re-checks: the dispatcher leaves a refused task Queued and logs
once per episode, `h9k task work` and `h9k task start` refuse with the same wording and exit 70,
and `h9k task assign` warns on stderr naming the holder and the link but assigns anyway — the
tracker is the go signal and the queue is where the task waits. A queued task's line on
`h9k status`, `h9k task show` and `h9k project show` names the item and its holder. Untouched: a
task with no linked item, an untracked one, and a `pr-review` task, whose pull request's own
assignment is already auto-pr-review's signal. The gate itself never writes to the tracker and has
no override flag; a tracker that cannot be read holds the claim rather than releasing it, quotes
the tracker's error, tells a credential refusal apart from an outage, and is re-read no more often
than every three minutes. The one write this feature makes lives outside the gate, in the command
below.

**One command can move the tracker and the board together** (Decisions Log #143).
`h9k task assign <id> [owner] --take` is the one write this feature makes, and it lives outside the
gate rather than in it (`TrackerAssignmentTake`, which calls `TrackerClaimGate` for its read — the
gate every door and every sweep calls stays read-only, because the dispatcher calls it on a cadence
with nobody watching). In a gated project it reads the linked item fresh and, when the tracker
shows **nobody** holds it, writes this install's own tracker identity into the assignee field (Jira
as an `assignee`-only update through `JiraWriteExecutor`, GitHub as
`gh issue edit --add-assignee` with the login read live), reads the
item back, records `TrackerAssignmentWritten` from that read-back, and assigns — so the gate then
passes on its own instead of the task sitting in the queue waiting for a second act. It only ever
moves an item from unassigned to this install: one somebody else holds is refused with exit 70
naming the holder, nothing is written, the task is left exactly as it was, and **there is no flag
that takes an item from another person**. Already yours records the observation and proceeds; an
unreadable tracker refuses rather than writing blind. A read-back that comes back naming somebody
else *beside* this install is refused too, and it is the one outcome only GitHub can produce
(`--add-assignee` adds where Jira's `PUT` replaces): two installs took the same issue in the same
moment, so neither may claim it, and the refusal names who else is on it and says to settle it with
them rather than to run the command again. The write is a **field update, never a
transition** — the item's status, labels and milestone are untouched — but a team's own board
automation may react to an assignment, which is why the flag is explicit. Without it, an
interactive assign offers the same take on an unassigned item (defaulting to no) and a
non-interactive one warns and proceeds without writing anything. `--take` where there is no gate
to satisfy (gate off, or no linked card or issue) is refused rather than quietly honoured.
Releasing a task leaves the tracker assignment where it is.

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

**A pull request GitHub assigns to this install's own login is a go signal in its own right, on a
project that opts in** (idea e5e98a33, Decisions Log #34's own amendment, #133): `h9k project set
<project> --auto-pr-review off|normal|first|now` (default `off`, today's behavior byte-for-byte)
makes the daemon poll GitHub, on the closeout monitor's own interval-with-backoff shape, for open
pull requests in that project's repo requesting this install's own login — read back from `gh`
fresh every sweep, never a cached name — and mint, publish, and start a pr-review task exactly as
`h9k task add --from-pr` would, recording the GitHub assignment as provenance rather than a human
typing the command. The three non-off speeds are the general dispatch levers, not new scheduling
code: `normal` joins the ordinary queue, `first` also sets the queue-first marker (#127), and `now`
claims it immediately, ceiling-exempt, through the same sentinel-node-id mechanism `h9k task start`
uses (#103, #125) — so a human re-speeds any auto-created task afterward with the identical general
levers. `now` is capped at one immediate, ceiling-exempt launch per sweep across every opted-in
project: the consent a human gives at `--auto-pr-review now` promises one extra concurrent agent
session, not an unbounded burst of them if several pull requests are newly assigned in the same
poll interval, so a candidate beyond that one launch is not dropped — it is minted, published and
assigned exactly as a `first`-speed task is, so it still takes the next free ordinary dispatch slot
rather than waiting a full poll interval for nothing to happen. One live task per pull request, the
same one-item-one-live-task rule adoption already
enforces (PLAN.md §3.1a): a re-request after an earlier auto-created review closed Done mints a
fresh task, noted as a re-review. An assignment withdrawn before the run dispatches concludes the
task honestly (abandoned, the go signal recalled by the same authority that gave it); withdrawn
after the run is Claimed or parked, it is recorded as an observation only — findings already
produced are never discarded for a reviewer reshuffle. The pr-review run itself (#99) is entirely
untouched: this changes only when a review starts, never what it does once it has.
