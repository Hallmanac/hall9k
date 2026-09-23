# The `h9k` command surface

**The `--help` tree is the reference, and this page is the map.** Nothing here reproduces an
option's full description, on purpose: a duplicated one goes stale, and the copy in the terminal is
the one that is true. What the page does is name every command and every flag the tree prints, with
a sentence on what it is for, so that the map has no gaps against the territory.

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
| `h9k status` | The attention pane: what needs you, what has gone quiet, what is running. Bounded on purpose. Beneath the spend lines it prints a throughput block for the current spend period — tasks merged, median and p90 claim-to-merge, first-pass merge share, laps per merged task, and the share of task time spent queued or waiting on a human — so speed and efficiency read in the same glance as cost. Under five merged tasks it prints the count and says there are too few to summarize rather than a median of two. Each queued row also says how long it has waited for its present slot, and the queued section's own heading carries the period's total queue time. |
| `h9k task show <id>` | One task in full: contract, dependencies, external reference, conversation, its own passage in time (how long it queued, built, sat in gates, cycled through review, waited on a human, and waited for its merge, plus lap, cycle, and session counts), every run and its outcome, each run's own gate wall-clock durations, a flag when one materially exceeds the project's recent recorded average for that gate, and each run's own worktree path, branch and live agent sessions. The second command of any investigation. |
| `h9k logs <id>` | A run's transcript, rendered from its stream-json (`--raw` for the stream-json itself, and `--run <run-id>` for an earlier run than the task's latest). The log dive `h9k status` is meant to save you. |

### Ideas: capture and discovery

`h9k idea add | list | show | revise | assign | promote | conclude | archive | scope | share | set-private`

Capture is one command with one argument and an optional project. Revision has no ceremony,
because nothing dispatches from an idea and there is no promise an edit could break. There is no
single graduation ceremony: `h9k task add --from-idea <id> --objective "…"` cuts a draft task from
an idea through the ordinary add door, and invoked repeatedly one idea fans out into any number of
them — each cut needing its own objective and requiring a project, supplied on the cut or already
assigned to the idea. `promote` survives as sugar over cutting exactly one task (the note's first
sentence becomes the objective) and concluding in the same breath. Cutting a task never ends the
idea; only an explicit `conclude` (something came of discovery) or `archive` (nothing did) does,
each with its own required `--reason`. `h9k idea list` shows twenty rows, newest first, and
`--limit <N>` changes that; `--unassigned` shows only the ideas that have no project yet, the ones
still deciding where they belong.

### Decisions and lessons

`h9k decide "<statement>" | list | show | supersede | import` and `h9k learn "<statement>" | list | show | retire | distill`

Both branches have the same shape: the bare positional form always writes and never reads, and
every read lives behind a subcommand. Recording prints the record's id, and that id is the
citation key — a stable identity from the moment of recording, which is what the Decisions Log's
sequential numbers could never be. Scope defaults to a project (the one named with `--project`,
then the project of the run you are recording from, then the sole registered project) and
`--owner` widens it to a cross-project habit. A decision also takes `--origin "<the incident that
produced this rule>"` and `--supersedes <id>`, which records both directions of the replacement in
one act. Provenance is observed rather than inferred: pass `--task <id>` from inside a run and the
record carries that run and task, leave it off at a shell and both are recorded as explicit nulls.
A decision recorded from inside a run that is not human-attended is refused and pointed at `h9k
learn`, because agents record lessons and humans record decisions. Neither terminal verb deletes:
`h9k decide supersede <id> --reason "…"` and `h9k learn retire <id> --reason "…"` append, and
`--all` on either `list` brings the ended ones back into view, and `--limit <N>` on `decide list` and
`learn list` changes how many of the newest rows they show, twenty by default. `h9k decide
supersede <id> --by <decision>` names the decision that replaced it when one is already recorded,
and leaves the flag off for a decision that was simply overruled, so the record says so rather than
pointing at the nearest plausible successor. `h9k decide record "<statement>"` and `h9k learn record
"<statement>"` are the spelled-out forms of the bare positional ones, for a statement that is itself
the name of a subcommand.

`h9k decide import` is the one-time migration that put this repository's own markdown rulebooks
into the store: every PLAN.md §16 Decisions Log entry and every AGENTS.md standing rule, each
recorded keeping the citation it already had, so a reference written anywhere as "Decisions Log
#62" still resolves two ways: by searching `decisions.md` for that text, and through `h9k decide
show "Decisions Log #62"`, which takes the citation itself and is the only route to an entry the
file leaves out for no longer binding. It refuses on a node where event
replication has not switched on (idea 202383dc, M2a), naming the milestone, because an event
written before that point never rides an outbox and the whole rulebook would sit in one install's
store forever. Running it again is safe: the citations already recorded are what the later run
skips, so an interrupted first run is finished rather than duplicated. Two runs at once are
refused rather than merged, by an advisory lock the import holds for its own transaction: each
would otherwise record the whole rulebook under its own ids.

Lessons also ride into the prompt of every implementation, follow-up, review and fix session, as
a bounded section under "What this project's earlier runs already learned": this project's active
lessons plus the owner's, newest first, each line led by the id a session cites or retires it by.
`h9k config set --lesson-prompt-max-lessons` and `--lesson-prompt-max-characters` bound it, and
truncation is announced rather than silent: the section names both counts, both caps, and
`h9k learn list`. The section's own recording verb names the task (`h9k learn "…" --task <id>`),
so a dispatched session's lesson carries its run rather than landing marked as having named none.
What is injected is decided by provenance, not only displayed: a lesson an agent
run on ANOTHER node recorded stays in `lessons.md` and out of every prompt until the security
review in idea 7e403b80 rules otherwise, and `h9k learn show <id>` says which side of that line a
lesson falls on, and whether it is still active enough to reach one at all.

`h9k learn distill [--project <name>|--owner]` authors the task that merges a scope's lessons into
fewer, better ones. It creates an ordinary Research task **draft** and stops there: the daemon
never distils on its own judgment, because merging two claims into one is a judgment about meaning
and a wrong merge replaces two things somebody observed with one thing nobody did. The task's
instructions are merge-and-cite only, and the citation half is enforced rather than requested:
`h9k learn "<merged claim>" --distilled-from <id> --distilled-from <id>` records the merge with its
sources, and the decider refuses one whose citations resolve to nothing, repeat, or name the lesson
itself. Merging retires nothing on its own; each source still ends with its own
`h9k learn retire <id> --reason "Absorbed into <id>"`. `h9k status` names this lever for a project
whose active lessons have passed the count cap.

### Tasks: development and dispatch

`h9k task add | revise | set-session-cap | set-review-caps | set-pre-approved | publish | assign | unassign | draft | list | show | pull | log-interaction | scope | share | set-private | handoff | take | grant | refuse | run-local`

`add` creates a Draft. `revise` is Draft-only, with a few exceptions, the main one being `--queue-first`/
`--clear-queue-first` sets or clears a task-level scheduling marker — the next free dispatch slot
takes this task regardless of assignment age — and is settable in any live state except Abandoned
(Decisions Log #127). `--clear-interactive-mode` is another, and so are a spike's kind, exit
criterion, and budget while it is Draft or Published. `publish` is the readiness gate. `assign` is the
dispatch trigger. `assign <id> --node <id-or-fragment>` narrows that to one node of the owner's
fleet: only that node's own dispatcher claims the task, and every other node of the fleet stands
down without a forced take; run with no owner argument against a task already assigned, it
changes only the placement. A bare `--node` with nothing named clears an existing placement, and a
forced takeover or cooperative grant that moves a placed task to another node records that node as
the new placement on its own (idea 202383dc: an owner can place a task on one node of their fleet).
The path back for an edit is `unassign → draft → revise → publish → assign`.
`set-session-cap <id> <cap>` overrides how many agent sessions this task's own run may hold
simultaneously — settable any time, even mid-run — in place of the node's global default.
`set-review-caps` overrides the node's compiled review-cycle-cap defaults for one task —
settable at any time, even while the task's run is live, so it doubles as the takeover lever
for a grinding run. `add`/`revise --review-stage-composition <VALUE|default>` sets the task-level
override of which pre-PR review stages a run gets — unlike the review caps above, Draft-only, so
a live task's next run is the earliest a change reaches it — and `--accept-reduced-review`
acknowledges a value that removes a load-bearing guarantee (Decisions Log #129, see
[project set](#projects-owners-connections) below). See
[operations.md](operations.md#daemon-operating-settings).

`h9k task add` also adopts existing external work: `--from-issue 42` (or `owner/repo#42`, or a
URL), `--from-jira PROJ-1` (or a URL), and `--from-pr` (a pull request number, `owner/repo#42`, or
a URL) — the last of which adopts a pull request to review rather than build, always as a
`pr-review` task (§ below). Adoption is a one-time snapshot: the title seeds the objective, the
description becomes agent context, and the state read at import is recorded as an observation of
that moment and never re-checked. Acceptance criteria are never read out of a description; supply
them with `--criteria` or at the prompt.

A published task's whole self lives in the project's own ledger now, not in its tracker item
(idea 202383dc, A3a): `records/<task-id>.yaml` on `refs/hall9k/ledger/records`, one file per task,
keyed by task id because ids are the same on every node. When the local store does not already
hold the item's own task (the ordinary case, checked by `ExternalReference`), adoption falls back
to scanning the ledger for a record naming the same reference — the shape a node still catching up
on event replication is in — and reports the task it names rather than creating a second copy,
whether that task is already replicated here or still on its way. Nothing is ever reconstructed
from a record's own fields any more: full event replication means a record found in the ledger
always names a task that either already exists here or is arriving, never one to seed a fresh draft
from. An item with no record anywhere and no local task adopts exactly as described above. See
[scope.md](scope.md#the-task-record-in-the-ledger).

That fallback also queues an events-request for the missing stream, so the refusal is doing
something rather than only telling you to come back later — and it says which of three things
happened: the request went out now, one from an earlier run is still on its way, or nothing was
queued at all because this node has no owner root fingerprint yet and so has no identity to send
from, in which case it names `h9k project join` instead of promising the task will turn up.

`h9k task pull <task-id> [--project <PROJECT>] [--again]` makes the same ask directly, with no
tracker item involved. The full id works, and so does the short id off a board row, a pull request
title or a branch name: a fragment is matched against this node's own tasks first and then against
this project's ledger records, which is the one local source that names a task whose stream is not
here — and the project whose ledger carries that record is the project asked, so `--project` is
only needed when no record names the task. That ledger read is the one time this command touches
git; everything else it does is local. It queues one project-wide events-request and returns; the
daemon's next message sweep sends it, and `h9k status` shows it while it stands.

A request a peer has already declined, or answered, is closed rather than in flight, so a re-run
asks again and says that it did, and `h9k status` names which node declined it and when for the
next day. One genuinely still outstanding is reported as outstanding and nothing new is queued; `--again` closes that one out as superseded and asks afresh, which is also
the only way to clear a request minted before v0.10.5, when a decline did not yet close a
broadcast.

A peer answers an explicit request like this one from below its own replication switch-on point,
which an ordinary flush and an automatic gap-fill are still held above — so a task published before
that peer ever switched replication on is reachable this way and no other. It answers with the
task's own run streams as well, so a pulled task whose runs finished reads Done rather than
Delivered with no laps and no sessions. A run's own stream id is accepted here too, for a run
missing from a task already held. A private task is never served, however explicit the ask. A
stream this node holds only the *tail* of is refused up front instead: a replicated event is
appended to the local stream and the older half cannot be put in front of the newer half already
here, so there is nothing to ask for while that tail is applied. The daemon's own startup repair
frees exactly that shape whenever it can reconstruct the stream faithfully, holding every
replicated event already on the stream, removing the partially-applied documents and releasing the
stream id, after which this same command goes through and the held tail completes the moment the
genesis lands. The refusal says so rather than calling the stream unreachable, and says the other
half too: a stream the repair cannot reconstruct is left exactly as it is, named with its reason in
the daemon log, which is where to look if the same refusal comes back after a restart.

A task that arrives naming blocked-by or stacked-on ids whose own streams are not here has those
asked for automatically, one request per missing dependency, walking the graph as each one lands —
that is what keeps `h9k task assign` from refusing the task for a dependency the platform could
have fetched itself. `h9k status` names each of those asks and whose dependency it is, and running
`h9k task pull` on a task already here makes the same asks for anything it is still missing. Which
node states catch up automatically and which need a pull is in
[concepts.md](concepts.md#catching-a-node-up).

`--file task.md` reads a whole task from a markdown file: a minimal `---` frontmatter block
(project, type, objective, criteria, an optional model, optional blocked-by, optional stacked-on,
optional epic)
followed by a body that becomes the agent context — or a `context:` block scalar, the same form
the ledger's own task record uses. The document grammar is deliberately not whole-document
YAML, since a handful of known keys does not warrant the dependency and this platform's own
`task.md` renders plain scalars carrying colons; each **value**, though, is read as a real YAML
scalar, so a double-quoted objective or criterion is stored without its quote characters and a
`|` block scalar arrives as the multi-line text it denotes. The numbered [`backlog/`](../backlog) files are
written in that format; the `IDEA-` notes beside them are earlier-stage prose with no
frontmatter, so they are read and authored from rather than fed to `--file`.

### Task types, budgets, and pre-approval

`h9k task add --type <TYPE>` names what kind of work a task is: `feature`, `bugfix`, `refactor`,
`chore`, `research`, `content`, `pr-review` (set for you by `--from-pr`), or `spike`. Two of the types
change how the pipeline runs, and each has its own flags; what the types mean is in
[concepts.md](concepts.md#task-types).

| Command | What it is for |
|---|---|
| `h9k task add --type spike --kind research\|experiment\|prototype` | Cuts a spike, which answers one stated question and never opens a pull request, where the kind decides whether the gates run and what becomes of the branch. |
| `h9k task add --exit-criterion "<sentence>"` | States the one checkable sentence a spike's single review pass judges its findings and branch against, required for a spike and refused on any other type. |
| `h9k task add --max-turns <N>` | Caps a spike's build session at that many turns, and crossing it records a budget-exhausted verdict rather than a failure. |
| `h9k task add --max-tokens <N>` | Caps a spike's build session by cumulative token spend, with the same budget-exhausted outcome. |
| `h9k task add --max-wall-clock <DURATION>` | Caps a spike's build session by elapsed time, given as a .NET duration such as `00:30:00`, with the same budget-exhausted outcome. |
| `h9k task revise --kind`, `--exit-criterion`, `--max-turns`, `--max-tokens`, `--max-wall-clock` | Change a spike's kind, exit criterion, or budget while it is a Draft or a Published spike, which is the one place a published task can still be edited. |
| `h9k task revise --clear-budget` | Drops a spike's turn, token, and wall-clock limits entirely. |
| `h9k task add --type content` | Cuts a documentation, skill, or prompt-template task that dispatches with a conformance-only review and fails at the gates if its diff leaves the project's non-executable-path set. |
| `h9k task revise <id> --type content` | Changes a Draft's type to content or to any other ordinary type, but never to `pr-review`, which only `--from-pr` can attach. |
| `h9k project set <project> --non-executable-path <glob>\|default` | Adds a glob to the set of paths whose diffs skip the verification gates, layered on the compiled defaults, with `default` clearing the project's own additions, where a glob ending in `/` matches anything under it from the repository root, one with no `/` matches a file name at any depth, anything else matches the whole path (`assets/**/*.png`), and a `!` prefix is refused. |
| `h9k task revise --clear-dependencies` | Drops every `--blocked-by` edge so nothing blocks the task. |
| `h9k task revise --clear-interactive-mode` | Clears the task's interactive-mode flag directly, for the cases where neither `handback` nor `release` has an active interactive claim to act on. |

**A task can link both a GitHub issue and a Jira card, and one of them is primary.** Passing
`--from-issue` and `--from-jira` together to `h9k task add` adopts both. `--primary-tracker
github|jira` on that command picks which reference keeps the claim gate, the branch key, publish,
the closeout comment or close, and every tracker write; the other becomes a secondary that is shown
and linked and gates and writes nothing. A project default (`h9k project set <project>
--primary-tracker github|jira|none`, where `none` clears it) answers when the flag is left off, and
a task adopting both with neither is refused. The flag is ignored, never refused, when only one
tracker is named. See [concepts.md](concepts.md#ideas-and-tasks).

**Pre-approval lets the daemon merge a task's pull request itself.** `h9k task publish --pre-approved
[on|after-human-review|off]` (and the same option on `h9k task add` for an adopted issue) gives a task
standing pre-approval, and `h9k task set-pre-approved <id> on|off|after-human-review` changes it later
on any live task whose pull request has not merged, without the unassign, draft, revise, publish
ceremony a readiness change would otherwise need. `on` merges the moment GitHub's own gates read
satisfied; `after-human-review` holds the same merge until a human reviewer has been requested and
every requested reviewer has approved the current head; `off` returns the merge to you. Hall9k
stores no reviewer setting and requests no reviews, so you add the reviewers in GitHub. Every
existing human waypoint still stops the pipeline as it would for an unflagged task. The full
behavior is under [Closeout in concepts.md](concepts.md#closeout).

Smaller flags on the same commands:

| Command | What it is for |
|---|---|
| `h9k task add --context "<text>"` | Sets the agent-facing context, meaning the pointers, constraints, and boundaries the session reads, and `h9k task revise --context` replaces it. |
| `h9k task add --model <model>` | Sets this task's own model, which outranks every other level of the model chain, and `h9k task revise --model` changes it, with `default` clearing the override. |
| `h9k task publish --no-assign` | Publishes and stops, without offering to assign, which is the form a script wants because an interactive terminal is otherwise asked about single-owner assignment. |
| `h9k task release <id> --unassign` | Takes a claim straight to Published in one atomic act, so the dispatcher never sees the task claimable between a release and an `unassign`. |
| `h9k task release <id> --keep-interactive` | Preserves the interactive-mode flag across the release, so the next headless run still parks at each phase boundary for a recorded `h9k review proceed`. |
| `h9k task deliver <id> --handoff "<text>"` | States what the run hands down to a dependent task or a resuming session, and is prompted for on an interactive terminal when omitted. |
| `h9k task resolve <id> --pr <url>` | Records where the work landed, and when it names a real pull request on the project's repository it enrolls that pull request in closeout's orphan sweep. |
| `h9k task list --limit <N>` | Shows that many rows, newest first, twenty by default, with a footer saying how many were held back. |
| `h9k task set-review-caps <id> --max-compliance-review-cycles <N\|default>` | Overrides this one task's conformance-track cycle cap, live if need be, which is also the takeover lever for a task observed grinding. |
| `h9k task set-review-caps <id> --max-adversarial-review-cycles <N\|default>` | Overrides this task's adversarial-track cycle cap in the same way. |
| `h9k task set-review-caps <id> --max-final-full-pass-rounds <N\|default>` | Overrides this task's cap on consecutive mandatory final-full-pass rounds. |
| `h9k task set-review-caps <id> --lifetime-review-cycle-budget <N\|default>` | Overrides this task's lifetime ceiling on review cycles counted across every run and follow-up. |

### Stacked pull requests

`--stacked-on <parent>` on `h9k task add` — or `--stacked-on` / `--clear-stacked-on` on
`h9k task revise` — declares this task **stacked on** that blocker rather than merely blocked by
it. The option implies the `--blocked-by` edge, so the parent needs naming only once. A stacked
task dispatches at its parent's `Delivered` rather than its merge, cuts its branch from the
parent's branch head, opens its pull request against that branch, reviews and recomposes against
it, is kept off the merge bar until the parent merges, and is then retargeted onto the base branch
and mechanically replayed there (a parent branch that moves short of merging gets the replay alone,
with the base left where it is). The tool never infers a stack from an ordinary `--blocked-by`;
reserve the edge for slices of one feature that are genuinely cohesive.

`--stacked-on-pull-request <number>` is the same edge when the parent lives on somebody else's
node — a teammate's pull request this install never dispatched and whose run it will never see
reach `Delivered`. It takes the number as GitHub shows it (`264` or `#264`) on this project's own
repository, carries **no** `--blocked-by` (there is no local task to name), and needs no task to
exist for that pull request at all. The pull request being *open* is the remote parent's
`Delivered`, read from GitHub on the closeout watcher's cadence once the child is assigned — an
unassigned one is on no cadence at all, so `h9k task assign` is what starts the watch. A hold can
lag a few minutes behind the browser, and `h9k task show` labels every line about the parent as an
observation, with when that reading was taken, rather than as current. The two forms are
alternatives: pass one, and declaring either one replaces
whatever the task previously stood on. `--clear-stacked-on` drops whichever form the task holds.
Full behaviour: [concepts.md](concepts.md#stacked-pull-requests).

### Pull-request review

`h9k task add --from-pr <number-or-url> [--again]` ·
`h9k pr review <number-or-url> [--no-worktree] [--since-my-review]` ·
`h9k pr approve <task> --note "…"` · `h9k pr request-changes <task> --note "…" [--finding "…"]`

`--from-pr` creates a read-only `pr-review` task: the node pulls the pull request into a detached
worktree, runs an adversarial-weighted independent review over it (never a build or a fix), and
parks a findings report for the owner to walk — `h9k review resolve <id> --merge-ready` once every
finding has been directed, since a pr-review task has no diff of its own for `--needs-fixes` to
act on. Nothing is ever posted to the pull request without an explicit human go: the
`walk-pr-review-findings` skill is what walks the report and posts on direction, always under the
owner's own login. No merge is ever observed for it — there is no pull request of this task's own
to merge.

**A posted review is followed through, not ended** (PLAN.md §16 #160). Once the review is out —
`h9k pr approve`, `h9k pr request-changes`, or a review you posted by hand and then closed with
`h9k review resolve --merge-ready` — the task does **not** go Done. It parks on the pull request as
`AwaitingAuthor`, listed under **Waiting** on `h9k status` with the pull request named and the count
of your own threads still open beside it, and the closeout watcher polls it on its existing cadence
(minutes, not seconds). When the author replies in one of your threads, pushes new commits, or
re-requests your review, the task flags **needs-you** with a line naming what changed — replies and
threads counted, new commits counted, a re-review request said outright. A reply counts when *you*
did not write it: your own follow-up comment in your own thread never flags the task, and the line
says the pull request moved rather than naming an author, since the counts do not say who wrote
them. A re-review request is the one part that names you, because GitHub records who a review
request is addressed to — and it wakes you on its own, since an author who resolves your threads
themselves and asks you back without a word or a push is still asking. Only the moment it arrives
wakes you; a request left standing holds the wait open without re-announcing itself. Every thread
you opened being resolved no longer ends the wait by itself (Decisions Log #178,
amending #160): the task stays Waiting until the pull request itself merges or closes, whatever the
threads say, since a task that closed the moment it had nothing left to watch would leave a later
`@login` mention on the same pull request with no live task to attach to and mint a redundant
second one instead. `h9k task abandon <id>` is how you stop watching early — and it is the only
lever that does, since `h9k task resolve` is the attestation exit from a Failed task alone.

**Reading only what changed**: `h9k pr review <number-or-url> --since-my-review` opens a scoped lap
over the deltas alone — the replies on threads you opened, verbatim, plus the commits pushed since
your review and their diff. A re-review request standing against you is stated at the top of that
packet, because it is the one thing that can summon a lap with nothing in either half. It skips the
objective, the blast radius, the CI results and the earlier
findings report outright (you read those in the first lap) and reports findings in the same shape,
which you direct with the same two commands. Without the flag, a lap on a waiting review reads the
pull request whole, exactly as the first one did.

**A repeat `--from-pr`** on a pull request this node already holds — a review waiting on its author,
one whose author has just answered, or one that closed out — **names that task instead of minting a
second one**, and says which and what to do next. That existing task carries the findings, the
verdict, and the whole record of the review; a second one starts from nothing. `--again` mints one
deliberately when that is genuinely what you want. Any other live holder (a review still running, a
findings park nobody has walked, a failed one) is refused as it always was.

**Your own review lap, on top of that task** (PLAN.md §16 #149): `h9k pr review <number-or-url>`
attaches to the `pr-review` task this node already holds for the pull request — auto-adopted from a
GitHub reviewer assignment, or created by `--from-pr` — and adopts the pull request itself only when
no live task exists. It reuses that task's read-only worktree (`--no-worktree` skips the checkout,
for reviewing against a deployed environment) and prints a briefing to paste into a Claude Code
session you start yourself: the stated objective and acceptance criteria when this node can read
the authoring task, the surfaces touched with a blast-radius summary, what CI ran, and the
platform's own merged findings report when the automated review has already parked one. The
briefing is deliberately factual — no test scenarios, no areas of concern, no suggested review
order; the session offers all of that the moment you ask, and helps with local setup, running the
suites, or writing end-to-end tests. It never commits to or pushes the pull request's branch (the
session is denied `git push`; every `gh pr` write verb, `update-branch` and `edit` included, so
neither the branch nor the description moves under your login; every `gh issue` write verb too,
since issues and pull requests share one number space and one resource, so `gh issue comment` on
the pull request's number would comment on the pull request; `gh api`, the endpoint they all
reach and the one this platform's own poster uses; and the two verdict commands below, which are
yours to run and not the session's), and tests you write go to a branch of your own the
session offers to stack on the pull request.

The lap **never ends on its own**. It ends when you run `h9k pr approve <task> --note "<text>"` or
`h9k pr request-changes <task> --note "<text>" [--finding "<path:line: text>"]...`, each of which
posts the GitHub review on the pull request's current head under your own login — the
changes-requested one with every `--finding` as a line comment — records the verdict on the task,
releases the worktree, and parks the task on the pull request to wait for its author (above),
exactly as `h9k review resolve --merge-ready` does.
The review is posted *before* anything is recorded, so a post that fails records nothing and you
simply run the command again; GitHub rejects the whole review when a `--finding` names a line its
diff does not contain, which means nothing gets posted until every line is one it accepts. These
two commands replace the `review resolve` ceremony for somebody who is actually reviewing;
`h9k review resolve <id> --merge-ready` remains the way to close a pr-review task out when the
report was walked and nothing needs posting.

One shape a single-login install runs into: GitHub takes no review from a pull request's own
author, so neither verdict can be posted on a pull request the same account opened — it answers
"Can not approve your own pull request", which both commands relay as exactly that. Reviewing your
own account's pull requests is what the lap plus `review resolve --merge-ready` is for; a posted
verdict needs a second account.

That task starts on its own by default, rather than waiting on `--from-pr`: the daemon polls
GitHub for open pull requests in each project's repo requesting this install's own login, and
mints, publishes, and starts a `pr-review` task the moment GitHub reports the assignment.
`h9k project set <name> --auto-pr-review off|normal|first|now` chooses the speed, and `normal` is
the default for every project, new and existing (Decisions Log #161 — it defaulted to `off` until
2026-09-08, when the feature was found to have been installed and silent on both nodes for three
days). `off` is an explicit opt-out, honoured for as long as it stands.

A second, independent search runs on the same sweep: GitHub's own `mentions:` qualifier for the
install's login, over the same registered repositories, read fresh every time. A direct `@login`
mention fires; a team-handle mention never does. A mention on a pull request no live pr-review task
watches mints the identical task type a review request does — never a new one — at the project's
own effective speed; a mention on a pull request a live task already covers attaches to it instead,
and when that task's report is already parked or it is waiting on the pull request, the daemon
dispatches a bounded follow-up lap that reads the tagged comment against the review already done
and parks an addendum beside the report — walked with the `walk-pr-review-findings` skill exactly
like the original report, including its own new step: show the drafted reply, take edits, and post
it only on the owner's explicit go, under their own login, in the exact thread the mention came
from. A comment id already handled never fires again, and a comment the install's own login wrote
never counts. `--auto-pr-review off` silences mentions too; there is no separate switch.

One pr-review task per pull request per install stays waiting on it until it merges or closes,
whether or not anything was ever posted to it (Decisions Log #178, amending #160):
every review thread being resolved no longer ends the wait by itself, since a task that closed out
the moment it had nothing left to watch would leave a later mention with no live task to attach to.
`h9k task abandon` remains the one early exit.

Three things make that default safe to leave on:

- **Its state is always printed.** `h9kd` logs one line per project at start naming whether auto
  pr-review is on or off there and whether that is the project's own choice or the platform
  default; `h9k status` prints the same fact as one line per project; `h9k project show` always
  carries the Auto pr-review row with its effective value, its origin, and the command that
  reverses it.
- **No backfill.** A review request GitHub recorded before a project's registration — or, for a
  project that predates this behaviour, before it first ran on this install — never starts a task
  on its own, whatever the setting says. The cutoff is recorded once per install and never
  recomputed, so a fortnight-old request cannot be swept up as though it had just arrived.
- **Every request is recorded and shown, whatever the setting.** Each pull request GitHub requests
  a review of is recorded once, with its outcome, and appears on `h9k status`: as a needs-you row
  wherever nothing started, naming the lever that actually ends that wait — both commands where an
  explicit `off` is what held it, and `h9k task add --from-pr` alone where the cutoff above did,
  since turning the setting on would not start a stale request — and as an informational row
  naming the task and following it wherever one is running — never needs-you there, since the
  daemon is already doing the work. The row
  clears when the request is withdrawn, the pull request closes, or a task adopts it; turning the
  setting on clears an `off` row, and deliberately does not clear one held by the cutoff above —
  a stale request stays stale, so the row keeps naming `h9k task add --from-pr` as the only lever
  that ends it. Every row names the login GitHub made the request of rather than assuming it is
  yours: a row is kept per reviewer login, and two installs with two `gh` authentications can
  share one database. It is kept per observing project and install too, since a project's setting
  and registration and an install's own cutoff are what graded it — so a repository two projects
  both point at gets a row each, printed once where they agree and twice, each with its own
  project's lever, only where they genuinely disagree.

### Replying in a review thread

`h9k pr reply <task> --thread <node-id> --disposition fix|decline|route --body "<text>"` ·
`h9k pr reply-guard`

The platform's own posting path, and the only route a dispatched follow-up has into a review
thread on its own pull request. It exists so exactly one question can be asked before words leave
the machine: whose thread is this? A **decline** or a **route** into a thread a *person* opened is
refused outright and recorded on the run — telling a colleague their point does not hold is the
owner's to send, so the lap drafts the reply and parks it, and `h9k review resolve` sends it,
edits it, or drops it. A **bot's** thread and a **fix**'s reply into anyone's thread post exactly
as they always have.

Whose thread it is comes from the platform's own read of the pull request at the moment the lap
was dispatched, not from the session's say-so; the disposition is the session's word, so every
accepted reply records the claim and the daemon compares it against that thread's own closing
triage block, putting a contradiction in the run log. The park is enforced off that same triage
rather than off the session's closing verdict, so a lap that declined a person's thread and then
closed as though it were finished parks anyway, with a blank draft for you to fill in or drop.
`h9k pr reply-guard` is not a command you
type: it is the PreToolUse hook a follow-up session launches with, refusing the `gh` routes into a
review thread — on either shell tool the session has — so the sanctioned one is the only one left.
`gh pr comment` is deliberately not
refused, because a review's own *body* is unthreadable and a top-level comment is the only answer
it can have.

### The claim gate

Every teammate runs their own install against their own database, so a Jira card or a GitHub
issue is the only record two machines share. `h9k project set <name> --claim-gate
off|tracker-assignee` (default `off`, the platform's original behaviour byte-for-byte) makes that
shared record the one act that hands out work: with `tracker-assignee`, a task linked to a card
or an issue is claimed on this install only while the tracker shows that item assigned to this
install's own tracker identity, so two teammates' installs cannot both run the same card.

The identity is read from the tracker, never typed and never matched by email or display name:
for Jira it is the `accountId` `/rest/api/2/myself` answers, captured at `h9k connection add
jira` and recorded on the connection (a connection registered before that read it live on first
use); for GitHub it is the login `gh` is authenticated as, read live on every check and
deliberately never stored, so a machine that re-authenticates as somebody else stops matching
immediately. Each check reads the item's assignee field and nothing else, so the one-time content
snapshot a task's adoption took is untouched. A GitHub issue may carry several assignees, and
being among them passes.

Every claim door re-checks. The dispatcher leaves a refused task Queued and says why once per
episode in the daemon log; `h9k task work` and `h9k task start` refuse with the same wording and
exit 70. `h9k task assign` warns on stderr — naming who holds the item, or that nobody does, with
its link — and assigns anyway, because the tracker is the go signal and the task is meant to wait
in the queue until it turns green. A queued task's own line on `h9k status`, `h9k task show` and
`h9k project show` says it is waiting for the tracker to show the item assigned to you, and names
the current holder when there is one. A task with no linked item, an untracked one, and a
`pr-review` task are all untouched: a pull request's own assignment is already auto-pr-review's
signal.

The gate itself never writes to the tracker and has no override flag. A tracker this install
cannot read holds the claim rather than releasing it — a gate that exists to stop two installs
running the same card must not let both through when the shared record goes dark — and the hold
quotes the tracker's own error and says what ends it: renew the connection's token with the
command printed, restore the connection, or wait out the outage, told apart so the remedy is never
a guess. A held task is re-read no more often than every three minutes rather than on every
five-second sweep.

**One command can move the tracker and the board together.**
`h9k task assign <id> [owner] --take` (Decisions Log #143) is the one write this feature makes, so
claiming stops being a two-place act: in a gated project it reads the linked card or issue fresh
and, when the tracker shows **no** assignee, writes this install's own tracker identity into the
assignee field, reads the item back, records what the read-back showed, and assigns — so the gate
passes on its own and stops being what keeps the task in the queue. (It is the gate that stops
holding the task, not a promise the task runs now: a task with unmet dependencies still waits on
those, and the assign line above the take's own says so.)

It only ever moves an item from unassigned to you. An item somebody else holds is refused (exit
70) naming the holder, nothing is written, the task is left exactly as it was, and there is
deliberately no flag that takes an item from another person — ask them to unassign themselves, or
take a different task. An item already assigned to you writes nothing and simply records what it
saw. A tracker that cannot be read refuses too: a take that cannot see who holds an item cannot
know it is taking it from nobody. And an item that was unassigned a moment ago but comes back from
the read-back naming somebody else *beside* you is refused as well — two installs took it in the
same moment, so neither may claim it. That one is GitHub-only, because `--add-assignee` adds where
Jira's write replaces, and the refusal names who else is on the issue and says to settle it with
them: your login is on the issue too, Hall9k never takes an assignment off one, and re-running
would simply find it assigned to you both.

The write is a **field update and never a transition**: Jira gets an ordinary update carrying
`assignee` and nothing else, GitHub gets `gh issue edit --add-assignee`, and neither touches the
item's status, labels or milestone — which state an item belongs in is your team's workflow. Be
aware, though, that your own board automation may react to an assignment; that is the one
consequence Hall9k cannot see. On GitHub, only a login that can be assigned on that repository — a
collaborator, or an organisation team member with access — is accepted, and GitHub's own refusal is
quoted verbatim when it is not.

Without the flag, an interactive `h9k task assign` in a gated project **offers** the same take on
an unassigned item (defaulting to no), and a non-interactive one warns and proceeds without
writing anything: Hall9k never writes to your tracker unless it was told to, and an unattended
process cannot tell it. `--take` on a project whose gate is off, or on a task with no linked card
or issue, is refused rather than quietly honoured — there is nothing for a take to unlock, and the
refusal says which of the two it was.

### Epics: naming a family of tasks

`h9k epic add | list | show | link-jira | close`

`h9k epic add --title "<name>"` requires the title, which is the epic's name.

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

**One Jira card that needs many pull requests does not distribute across sibling tasks the way it
looks like it should.** A task's own `h9k task add --from-issue`/`--from-jira` and
`h9k task link-jira`/`link-issue` refuse to adopt a card another task already carries, unless that
holder has since been abandoned (Done still holds the reference) or is a Done `pr-review` task,
whose completed review does not hold its pull request hostage the way adopted work holds its
issue: one card, one owning task, by design, so a card never ends up with two sets of runs and two
closeout comments. A card that genuinely needs several tasks to satisfy (found by trial and error
against exactly that refusal) puts the link on the epic instead:
`h9k epic link-jira` records the card once at the epic level; exactly one member task formally
adopts it; every other member task carries the same card only in its agent context, not as a
second `ExternalReference`. The refusal a second task hits trying to adopt the same card is the
guard working as intended, not a bug to route around. On a project tracked under `jira` or
`github-issues`, every sibling task must still publish with `--untracked` (below): it carries no
`ExternalReference` of its own, so the ordinary publish gate either refuses it or, taken past with
`--no-existing-item`, auto-mints a second card or issue for it instead.

### Working a task interactively

`h9k task work <id> [--direct-launch] [--acknowledge-unmet-dependencies] | register-session | verify | deliver | delegate | handback | release`

An operator can work a Published, Queued, or already-Blocked task in their own terminal instead of dispatching it
headless (Decisions Log #122). On a Published task assigned to nobody, `work` assigns it to the
operator's own owner and claims it interactively in one atomic event append: the task is never
observably Queued in between, so the dispatcher can never win the race to it. An unmet
dependency — whether just discovered here or already sitting Blocked from an ordinary
`h9k task assign` or a handed-back or retried claim — warns rather than refuses outright: the
platform names every open blocker, and
`--acknowledge-unmet-dependencies` is the human's recorded override to claim it anyway. Not needed
twice: an acknowledgment this task already carries from an earlier claim on the same still-open
blockers is honored without asking again, and `h9k task show` names whether a claim's own
acknowledgment was given fresh or carried forward from an earlier one (design ruling R7). `h9k task
assign` and `h9k task publish --assign` are unchanged and remain the headless dispatch triggers —
edges still gate automatic dispatch exactly as before; only this deliberate human claim gets the
warn-and-proceed path. Either way, `work` cuts the same branch and worktree headless dispatch
would, assembles the prompt through the same code path (its working rules swapped for an attached
operator) — and by default prints the worktree, the branch, and that prompt for the operator to
paste into a Claude Code session started anywhere, rather than launching one itself. The pasted
session's first act is `register-session`, which records its own process identity (from
`CLAUDE_PID`) against the claim; `--direct-launch` keeps the old behavior (a regular interactive
Claude Code session `work` launches and waits on itself) for one release, and re-running `work`
under it resumes the most recently recorded session's own conversation (falling back to a fresh
session, announced, only when the recorded one cannot be resumed). The claim is held by the human,
not a process either way, so closing the terminal is a normal way to leave and re-running `work`
re-enters the same worktree — by default with a fresh prompt. From there, `verify` runs the
project's gates on demand, `deliver` pushes the branch and hands the claim into the standard
delivery pipeline, `delegate <id> --note "<text>"` dispatches a headless build contractor onto this
same run, worktree and branch for one phase while the task stays claimed interactively (design
ruling R6) — distinct from `handback`, which ends interactive mode outright — `handback` releases
the claim to a headless agent partway through (resuming the
branch), and `release` gives an untouched claim back to the queue. `handback`'s pickup speed is a
three-way choice (Decisions Log #127): no flag is the normal rotation, unchanged; `--first` records
the queue-first marker so the next free dispatch slot takes the task regardless of assignment age;
`--now` dispatches it immediately instead, ceiling-exempt, through the same mechanism
`h9k task start` uses — refused together with `--first`.

### A deliberate human kick-off

`h9k task start <id> [--acknowledge-unmet-dependencies]`

`start` dispatches a Published, Queued, or already-Blocked task on the spot, headless, instead of waiting on the
dispatch queue or working it interactively. It reuses `work`'s own claim shape exactly — the same
ceiling-exempt sentinel `NodeId`, so every lever above (`verify`, `deliver`, `handback`, `release`,
the stale-claim nudge, and re-entering with `work` itself) accepts a start-it-mine claim on the
identical terms an interactive one already gets — but launches the
agent headless and detached (`claude -p`) under the `<task-shortid>-build` name, addressable on the
session mesh, rather than attached to the caller's terminal, and returns as soon as the process is
confirmed alive rather than waiting for it to finish. Shares `work`'s own warn-then-acknowledge
shape for an unmet dependency, on a Published task and on an already-Blocked one alike (a task
already sitting Blocked from an ordinary `h9k task assign`, or from a handed-back/retried
deliberate claim): the platform names every open blocker, and
`--acknowledge-unmet-dependencies` is the human's recorded override to start it anyway. Not needed
twice: an acknowledgment this task already carries from an earlier claim on the same still-open
blockers is honored without asking again, whichever of `start` or `work` gave it. Refused otherwise
on Draft, a pr-review task, a reopened task's follow-up branch, and any task that already carries a
live claim; there is no re-entry path the way `work` has one — a fresh claim on an already-Blocked
task is exactly what its Blocked entry is, not a re-entry. Giving the claim back (`handback`,
`release`, `retry`, or `pr resolve` reopening it) lands the task on Blocked rather than Queued when
the acknowledged dependency is still open, since only `h9k task assign` clears that snapshot — and
the acknowledgment itself stays on record for whichever command reclaims it next. See
[PLAN.md Decisions Log #125, #128](../PLAN.md).

### Logging an outside interaction

`h9k task log-interaction <task> --party "<who>" --summary "<what happened>" [--human-directed --reason "<why>"]`

The escape-hatch invariant's own CLI gate (PLAN.md §16 #123): every dispatched agent's prompt
states that any interaction with a party outside its own session — another agent session reached
through the mesh, a human steering it that way, an external service — is logged through this
command unconditionally, even one the interacting party asked it to keep quiet. `--human-directed
--reason "<why>"` records that a human, not the agent's own judgment, directed the interaction or
its outcome, so the record says so plainly rather than letting a human's own call read as the
agent's independent decision. Unlike `write-jira` or `link-issue`, this is not an observation gate:
there is nothing external here to verify the claim against, so the platform records only what its
own channels can see, honestly — best-effort by construction, not enforcement.

### Messages: node-to-node notes

`h9k message send <text> --to <audience> [--about <id>] [--project <PROJECT>]` · `h9k messages [--project <PROJECT>]` · `h9k message show <id> [--project <PROJECT>]` · `h9k message handle <id> [--project <PROJECT>]`

The successor to `notes/node-mailbox.md`'s GitHub-issue workaround (idea 202383dc, M1b): `send`
queues an envelope in this node's own store (no git, no network wait) and the daemon's own
message sweep is what actually lands it in this node's outbox, on a jittered cadence (15 to 25
seconds while there is something to send or read, 30 to 45 seconds when idle, immediately again
the tick right after this node's own push). `--to` addresses a specific `node:<node-id>` (the full
id: `h9k status` prints only its short form; `h9k project join` prints the full id), every
node an owner reads from with `owner:<fingerprint>`, or the whole project with the literal word
`project`; `h9k owner show` prints a root fingerprint.
`--about <id>` carries a task or idea id through as-is for the reader to act on. `messages` lists
what has arrived, clipping each body to sixty characters; `message show <id>` prints one note in
full — sender, project, kind, about-task, times, whether it is handled, and the whole body on its
own lines, written plain to stdout so its line structure survives a caller that is not a terminal.
Reading is not handling, so `show` never marks anything: `handle <id>` is the explicit act, never
implied by `messages` or `show` having merely printed the note. The orchestrator feed's own line
for a received note opens with the same id, so a window goes from the courier's delivery straight
to the full text. Project-scoped end to end (idea 202383dc, M2): a node registered to
several projects sends and reads each one's messages through its own repository, `--project`
names which one (defaulting to this node's only eligible project when there is exactly one), and
`messages`, `message show` and `message handle` all take `--project` to filter or disambiguate
(see [scope.md](scope.md)).

### The distributed team: identity, fleet, and holding

Every command that involves more than one machine or more than one person, in one place, one
sentence each, in the order a newcomer meets them. The model behind them is
[Identity, fleet, and team](concepts.md#identity-fleet-and-team); the prose for the commands that
also appear elsewhere on this page stays where it is.

**Joining, identity, and membership**

| Command | What it is for |
|---|---|
| `h9k project join <project>` | Generates this node's signing key the first time it runs, writes this node's identity into the project's ledger, and establishes your owner root when the project has no owner yet. |
| `h9k project join <project> --owner <fingerprint>` | Claims an existing owner root instead of establishing a new one, unverified until a node already enrolled under that root vouches for this one. |
| `h9k project join <project> --invite <secret>` | Proves you hold a single-use secret from `h9k node invite` or `h9k project invite`, so the minting node's daemon vouches you in with no further prompt. |
| `h9k project join <project> --from-project <name>` | Names the registered project whose ledger already vouches for this node, so the vouch is carried into a brand-new project's ledger without the root-holding node ever touching it. |
| `h9k project add --invite <token>` | Registers a project and finishes the join in the same call with an invite, which is the flow when the project belongs to someone else. |
| `h9k project assign-key <project>` | Backfills the project's generated key on a ledger that predates it, once, before the first `project invite` on an adopted project. |
| `h9k project members <project>` | Lists the members the ledger currently shows, each with its root fingerprint, its role (owner or member), its fleet of nodes, and whether it verified. |
| `h9k project member remove <project> <fingerprint>` | Removes a member by deleting its file from the members ref, refused unless this node's own root holds the owner role. |
| `h9k project invite <project> --role owner\|member` | Mints a single-use invite secret, printed once, for a new project member, whose role is `member` unless `--role` says otherwise. |
| `h9k node invite` | Mints a single-use invite secret, printed once, that lets a new machine of yours join your fleet on every non-archived project you are registered to. |
| `h9k node vouch <node-id>` | Vouches a node into your fleet by writing its id and public key into every non-archived project you are registered to, and prints its key fingerprint. |
| `h9k node revoke <node-id>` | Revokes a node from your fleet, effective at the next ledger read on any machine, and undone by vouching for the same node id again. |
| `h9k owner show [owner]` | Prints an owner's id, which is their root fingerprint and the value `--owner` and `--to owner:` take, along with their projects, linked GitHub accounts, this node's key path and public key, and every preference their work runs by. |
| `h9k owner set [owner]` | Changes an owner's standing preferences; its `--rerequest-review`, `--voice-skill`, `--clear-voice-skill`, `--persona`, and `--clear-personas` options are described under [Projects, owners, connections](#projects-owners-connections). |

**Keeping the fleet's stores whole**

| Command | What it is for |
|---|---|
| `h9k project reconcile <project>` | Asks every node of your own fleet for everything it holds of the project, which is the lever for a reconcile that `h9k status` reports stalled. |
| `h9k project pull <project> --since <sequence>\|all` | Asks the project's other members for history this node's own catch-up would never ask for by itself, with `--since` required so that you choose how far back. |
| `h9k task pull <task-id> --project <project>` | Asks the project's other members for one task's whole event stream when this node does not hold it. |
| `h9k task pull <task-id> --again` | Closes a request that is still outstanding as superseded and asks afresh. |

**Messages between nodes**

| Command | What it is for |
|---|---|
| `h9k message send <text> --to node:<node-id>\|owner:<fingerprint>\|project` | Queues a note for one node, for every node an owner reads from, or for the whole project, and the daemon's next sweep sends it. |
| `h9k message send <text> --about <id>` | Carries a task or idea id through with the note for the reader to act on. |
| `h9k message send <text> --project <project>` | Names the project the note belongs to, required when more than one project is registered on this node. |
| `h9k messages` | Lists this node's received messages that are still unread. |
| `h9k messages --all` | Includes the messages already handled alongside the unread ones. |
| `h9k messages --project <project>` | Lists only one project's received messages, where every project's show together by default. |
| `h9k message show <id> --project <project>` | Prints one received note in full without marking it read, and `--project` only matters when a short id matches in more than one project. |
| `h9k message handle <id> --project <project>` | Marks a received note handled, which nothing else ever does for you, and takes the same `--project` narrowing as `message show`. |

**Who holds a task**

| Command | What it is for |
|---|---|
| `h9k task assign <id> [owner] --node [node]` | Places an assigned task on one node of the owner's fleet so only that node claims it, and a bare `--node` clears the placement. |
| `h9k task take <id> --reason "..."` | Asks the node that holds a task to hand it over, and the project's take policy decides how that node answers. |
| `h9k task take <id> --force --reason "..."` | Overrides the holder on your own judgment when it has gone quiet, refused unless your root holds the owner role. |
| `h9k task grant <id>` | Grants a cooperative take request that the holder's take policy parked for a person. |
| `h9k task refuse <id> --reason "..."` | Refuses a parked take request and tells the requester why. |
| `h9k task handoff <id> --text "..."` | Leaves a note for whoever holds the task next, from the current holder only. |
| `h9k task handoff <id> --file <path>` | Reads that note from a file instead of the command line. |
| `h9k task handoff <id> --to <owner>` | Sends the nudge that a note was left to one owner's fleet, by root fingerprint, instead of to the whole project. |
| `h9k task release <id>` | Beyond its ordinary use, releases just the ledger holder of a task this node still names itself holder of but no longer claims, leaving the task's own state as it was. |
| `h9k task list --state HeldElsewhere` | Lists the claimed tasks that another node holds, and `--state attention-heldelsewhere` selects the same group the status pane counts. |
| `h9k project set <project> --take-policy auto\|ask` | Chooses whether this project's holder answers a cooperative take request itself (`auto`, the default) or parks it for its person (`ask`). |
| `h9k project set <project> --take-timeout <minutes>\|default` | Sets how long a take request waits for an answer before `--force` is named as the way on, thirty minutes by default. |

**How far an idea or task travels**

| Command | What it is for |
|---|---|
| `h9k idea scope <id> private\|fleet\|team` | Sets how far an idea's events travel: only this node, every node you run, or every project member's fleet. |
| `h9k idea share <id>` | Sets an idea's scope to `team`, the one door onto team scope an idea has. |
| `h9k idea set-private <id> on\|off` | Keeps an idea on this node with `on` or returns it to fleet scope with `off`, and is kept as an older spelling of `idea scope`. |
| `h9k task scope <id> private\|fleet\|team` | Sets how far a task's events travel, with `team` one-way once set. |
| `h9k task share <id>` | Sets a task's scope to `team`, which lets a draft reach the team before it is published. |
| `h9k task set-private <id> on\|off` | Keeps a task on this node with `on` or returns it to fleet scope with `off`, and is kept as an older spelling of `task scope`. |

**Settings for messages and invites**

| Command | What it is for |
|---|---|
| `h9k config set --message-poll-active-min <seconds>` | Sets the fast end of the message sweep's jittered cadence while this node has something to send or read, 15 seconds by default. |
| `h9k config set --message-poll-active-max <seconds>` | Sets the slow end of that active cadence, 25 seconds by default, which may not fall below the floor. |
| `h9k config set --message-poll-idle-min <seconds>` | Sets the fast end of the cadence for a node with nothing to send, read, or hold, 30 seconds by default. |
| `h9k config set --message-poll-idle-max <seconds>` | Sets the slow end of that idle cadence, 45 seconds by default, which may not fall below the floor. |
| `h9k config set --invite-expiry-hours <hours>` | Sets how long a newly minted invite stays valid, 72 hours by default. |

### Recovery

`h9k run kill` · `h9k task retry | resolve | abandon | take` · `h9k pr resolve` · `h9k review resolve` ·
`h9k review proceed` · `h9k review fixed`

`h9k run kill <task-or-run-id> [--reason "…"]` is the run-level stop, distinct from `h9k task
abandon`'s task-level walk-away: it ends a run's live agent process tree on this machine and
records the run **Killed** (never **Failed**), while the task itself lands exactly where any
other run failure leaves it, **Failed**, with `retry`, `resolve`, and `abandon` all still open.
Refused when `h9kd` is not running, since a stopped daemon supervises nothing.

Nine levers, and picking the wrong one loses work. [operations.md](operations.md#the-recovery-levers)
is the decision table. The ninth, `h9k task take`, is the only one that reaches across nodes: it is
the way on for a task another node holds and cannot finish, cooperative first and `--force` when the
holder has gone quiet ([Identity, fleet, and team](concepts.md#identity-fleet-and-team)). Two are
interactive mode's own: `review proceed` is the bare-approval lever
for a routine phase-boundary park, alongside `review resolve`'s redirect verbs, and `review fixed`
is the newest — you did the fix yourself, in your own worktree, and the review agents check it the
way they would check a fix session's. It applies at the review-verdict-to-fix boundary only (on
either side of the pull request), refuses over an uncommitted worktree, and refuses an unmoved
branch tip unless `--no-change "<why>"` says why. `review resolve` also carries the one lever whose
effect another person sees: on a park where a lap deliberately said nothing to a human reviewer —
a disagreement with their changes-requested finding, or a decline or route of a thread they
opened — it takes `--post-reply-as-written`, `--post-reply "<text>"`, or `--post-nothing`
alongside your verdict, and that choice is the only way those words ever reach the reviewer.

`h9k pr resolve <task>` dispatches a follow-up lap onto a done task's existing pull request branch and
resets the closeout monitor's automatic retry budget. The flag says which problem the lap is for:
`--checks` dispatches the fix-the-CI prompt when the pull request's checks are failing, and
`--rebase` dispatches the rebase-onto-main prompt when its branch conflicts with its base, which is
for when you see the conflict before the closeout monitor's next inspection does. With neither, the
lap resolves review comments. `h9k task resolve <id> --pr <url>` is the attestation exit from a
Failed task, and `--pr` records where the work landed.

### Projects, owners, connections

`h9k project add | init | join | assign-key | list | show | set | remove | cancel-purge | reactivate | rename | invite | pull | reconcile | members | member remove | prompt-addendum | run-skill` ·
`h9k owner show | set` · `h9k node invite | vouch | revoke` · `h9k connection add jira | list`

`project add` registers a project **and creates its home directory**; `project init` is the same
recipe for a project that has none yet, and the repair path for one that is incomplete. `project
add` refuses up front when this install has no confirmed GitHub account — a Jira connection alone
(`connection add jira`) tracks cards, not repository access, so it is not enough on its own (run
`gh auth login`, then retry). `project join <name> [--owner <fingerprint>]` establishes or confirms
this node's identity in a project's ledger: it generates this node's own signing key the first time
any project is joined, and `project add` runs it automatically once the project's repository is
reachable on disk, pushing a signed commit to the project's own remote. `join` also takes
`--invite <secret>`, proving possession of a single-use secret from `h9k node invite` (a new node
of an already-enrolled owner) or `h9k project invite` (a new project member); the minting node's
own daemon sweep matches the proof and vouches it in with no further prompt (idea 202383dc, T2).
Both `add` and `join` (invite or not) read this install's GitHub identity fresh from `gh` right
before they need it (also refreshed once at every daemon start), and `join` additionally refuses
before any key is generated or any ledger byte is written when the resolved GitHub account has no
push on the repository, naming the repository and the rule. `owner show` lists every confirmed
GitHub account linked to the owner (login and GitHub's own numeric id, or "unconfirmed" when `gh`
has never answered for it). See [the project home](#the-project-home) below.

`project assign-key <name>` is the one-time backfill for a project whose ledger predates the
project key: it mints a fresh key and writes it, signed, onto the genesis owner's own members
file, refused for anyone but the genesis owner and refused again once a key already exists. Run
it once, before the first `project invite` on an adopted project; every other install picks the
key up the next time it runs `project join` there. `MessageSweepEngine` skips a project with no
key recorded, so a legacy project's sweeps stay off until this runs.

`project remove` archives a project on this install: reversible, and nothing is deleted. The
dispatcher stops claiming its tasks, the project-home render, closeout, and auto-pr-review sweeps skip it,
`project list` hides it by default (`--include-archived` shows it, marked archived with the date),
and `project show` names it archived with the date. It refuses while any of the project's tasks
sits in a state the daemon may still act on — anything other than Draft, Published (always
unassigned), Done, or Abandoned — naming those tasks; drafts and unassigned published tasks stay
exactly as they are, hidden with the project. `project reactivate` ends the archive in place: same
id, settings, tasks, ideas, and home, and every sweep resumes for it immediately. `project rename`
changes a project's name and nothing else — no task, run, or idea references a project by name, so
only the display and the duplicate-name check change, and the home directory on disk keeps its old
folder name. Registering under an archived project's name (`project add`) offers to reactivate it
in place or to rename the archive and free the name, with `--reactivate-archived` and
`--rename-archived-to <NAME>` answering that non-interactively. All three commands act on this
install's own database only; a registration of the same repository on another node is unaffected.

`project remove --purge` accepts an already-archived project or archives one first, then schedules
a permanent hard delete of its database footprint 24 hours out: its own stream, every task, run,
idea, and epic stream it owns, and their projection documents. This is the one operation in Hall9k that
actually deletes anything — linked tracker items, the repository, and the home directory on disk
are outside its scope and are never touched. The confirmation names the scope in numbers (tasks,
runs, ideas, and epics) and the deadline; `--yes` covers non-interactive use. `project cancel-purge <project>`
ends a pending purge before it fires, leaving the project archived, never reactivated; reactivating
a project with a purge still pending is refused (cancel it first) so a daemon sweep can never
destroy a project that has gone live again. The same reasoning refuses handing a purge-pending
project new work it would otherwise destroy along with everything else at the deadline: `task add`,
`idea add`, `epic add`, `idea promote`, `idea assign`, and `pr review` all refuse against a project
with a purge scheduled, naming the deadline and the cancel command. `project list --include-archived` and `project show`
mark a purge-pending project with its deadline and the cancel command. A daemon sweep, alongside
the closeout and auto-pr-review sweeps, checks for due purges on start and on its own poll
interval, so a purge whose deadline passed while the daemon was down fires on the next start rather
than never; it logs what it destroyed in numbers.

`project set` is where the verification gates, the agent model, parallelism, commit style,
context links, skip-permissions, the Jira board binding, the backlog policy (`--backlog
none|github-issues|jira`) and its routing guidance, the review re-request policy, the
project-level review-cycle-cap overrides, the review stage composition (`--review-stage-composition`,
below), the branch-name template (`--branch-template`,
[below](#branch-naming)), the auto-pr-review speed (`--auto-pr-review
off|normal|first|now`, [above](#pull-request-review)), the claim gate (`--claim-gate
off|tracker-assignee`, [above](#the-claim-gate)), whether the design review drives the running
product (`--design-review-drive on|off`, below), whether a QA review may launch and drive it
(`--qa-review-drive on|off`, default off, below), the close-linked-issue rule (`--close-linked-issue
on-closeout|never|when-all-tasks-close`, [below](#closing-a-linked-issue)), the writing conventions
(`--writing-conventions`, [below](#writing-conventions)), the orchestrator-feed band
(`--orchestrator-feed actionable|transitions|everything`,
[below](#orchestrator-windows)), the feed courier's own per-project batching ceiling
(`--courier-max-wait <seconds>|default`, idea 89471598, piece 3 — the daemon-level
`--model-courier` sits on `config set` beside every other model role), and the home's location
live.
Settings resolve most-specific-wins, and the exact chain differs per setting;
[operations.md](operations.md#per-project-and-per-owner) has the two that matter.

`owner set` holds the preferences that belong to the human rather than to a project: the review
re-request policy (`--rerequest-review on|off|default`, which a project setting outranks), the
skill the owner writes in (`--voice-skill <name>`, forgotten with `--clear-voice-skill`, printed by
`owner show`), and the review personas they hold (`--persona engineer|qa|designer`, repeatable,
cleared with `--clear-personas`, also printed by `owner show`). A named voice skill makes every
prompt seam where a session composes text a human
reads as the owner's — a pull request description, a review-thread reply, a commit message, a posted
review finding, a drafted reply to a GitHub mention — tell that session to load the skill and its
matching context before writing: `contexts/code-review.md` for prose the session posts itself,
`contexts/explainer.md` for a draft the owner reads and decides on. The skill stays the owner's own,
referenced by name and never copied into a project, a prompt template, or the platform, so the name
has to already be a skill directory in the owner's user skills (`~/.claude/skills/<name>`) or in a
project home's `skills/`; a name in neither is refused naming both paths. It settles the prose only:
the repository's own PR-description rule and the project's `--writing-conventions` still decide the
structure.

A declared persona is the lens somebody else's pull request gets reviewed through when it is
assigned to this member. The set is fixed, because each persona maps to its own prompt and criteria
in the platform's persona registry: `engineer` is today's review of code, logic and functionality,
`qa` is compliance and functionality through the lens of blast radius, `designer` is user
experience, the proposed design, accessibility and the project's design system. A pull request
assigned to them mints the same pr-review task it always has, and that task runs one review session
per declared persona on its single worktree and branch, reported in one findings report sectioned
engineer, QA, designer. Declaring none is the ordinary case and reads as the engineer's review, so
nothing changes for anyone who never passes the option. All three prompts are registered, so a
declaration always runs the review it asked for; a persona added to the set before its prompt
exists would be named in the report and in `task show` as skipped rather than silently ignored,
and a member who declared only unregistered personas would get the engineer's review in their
place rather than an unreviewed pull request.

The QA review's own subject is blast radius, not correctness. It opens with a map, in plain
language, as the report's first section: what the diff changes, what sits next to it through a
shared code path or a shared data shape, and the user-facing flows crossing either. Each entry is
graded covered by a named existing test, owed a new automated end-to-end test it specifies, or
owed a human walk-through it writes out step by step, and every later finding cites its entry. The
session runs the project's end-to-end tests on the review worktree — scoped to the blast radius
where the test layout allows, in full where it does not — and reports pass, fail, or absent with
evidence. Whether it may also launch the running product and drive it through browser automation
is the project's own `--qa-review-drive on|off`, off by default; with it off, a verdict that needs
the running product comes back as a walk-through instead, and with it on the report carries a
Driven section naming the flows walked and a screenshot beside each finding one supports. A
project with no run skill drives nothing whatever the setting says.

The design review answers seven lenses, in this fixed order every time: user experience,
conformance to the proposed design, motion (transitions and animations), CSS practice,
accessibility, look and feel, and the design system. A lens the change does not touch says so in
one line rather than going missing, which the platform writes rather than the session. The
proposed design is a Figma link, an image set, or a prototype named on the linked task or issue or
in the pull request body; when none is named, the conformance lens reports `no reference supplied`
and judges nothing against imagination. The session looks for a design system in the repository —
tokens, a component library, a documented system — names what it found or that there is none, and
cites the token or component behind every design-system finding.

Whether that review stands the product up is a project setting, `h9k project set <name>
--design-review-drive on|off`, default **on** and printed by `project show` with its origin. On,
and with a run skill on the project's ledger, the session launches the app on the review worktree
on an ephemeral port it reports, walks the changed user-facing flows through browser automation,
runs an automated accessibility audit on each screen it walked, tears the app down, and cites each
screenshot beside the finding it supports in a `Driven` section of the report. Off, or with no run
skill, the review is code-and-design-file only, its accessibility findings are marked static, and
the report says which of the two reasons applied rather than leaving a reader to assume the product
was seen. Either way the report ends with the offer to run the branch locally so the reviewer can
walk it in person, present whenever the project has a run skill — a question in the report, never
an action. The QA review ends with the same offer on the same terms.

The reviewer answers it in their orchestrator window, and the window runs
`h9k task run-local <task>`. The report's own closing block carries the identity the command needs
— the task, the run, the branch, the worktree — so a yes resolves to exactly one checkout without
the reviewer naming anything. A review opened with `--no-worktree` has no checkout at all, and that
block says the offer cannot be taken up here rather than printing a command it already knows would
refuse. The command follows the project's run skill as an ordered plan on
that worktree: the steps only a person can do first, then prerequisites, one-time setup and launch.
A step carrying a command runs; a step carrying none stops the launch and prints exactly what the
skill says to do, with `h9k task run-local <task> --continue` picking up at the next one. A launch
command with somewhere to put a port takes an ephemeral one, so a review launch never seizes a port
the reviewer was already using; one with nowhere to put one runs as written and no port is claimed.
When the product is up it prints the address, the skill's own signal that it came up, and every
human step in order. It refuses with a sentence when the worktree is gone, when the project has no
run skill (nothing records how it is started, and it will not guess), or when a launch of the same
task is already up. `--stop` ends it, and so does the daemon on its own when the task closes out or
the worktree is removed: a launch is never left running.

Which pre-PR review stages a run gets is itself a project-, task-, and node-level setting
(`--review-stage-composition <full-pipeline|adversarial-only|conformance-only|skip-final-pass|none>`
at `h9k config set`, `h9k project set`, and `h9k task add`/`revise`, Decisions Log #129): the full
pipeline (default, both lenses every cycle plus the mandatory final full pass), one lens only,
the mandatory final full pass skipped, or no pre-PR review at all. Resolved once, at each run's
own dispatch, task > project > node > compiled default, and frozen for that run's whole lifetime —
unlike the review-cycle caps above, a mid-run change reaches only the task's next run — and
recorded on the run's own stream, so `h9k task show` always answers which pipeline shape a given
run actually ran under. A value that removes a load-bearing guarantee (Decisions Log #92, or a
lens's own attention budget) is refused at set time unless acknowledged with
`--accept-reduced-review`, which prints the consequence being accepted.

`project pull <project> --since <global-sequence|all>` asks this project's other members for
history this node will never ask for on its own. Three node states, and only the first two are
automatic: a brand-new node bootstraps the whole project the first time its sweep finds no applied
history at all, any node gap-fills when a peer's outbox stalls across a numeric hole, and a node
that is neither — joined a while ago, holding the retention window and work of its own — is
permanently out of reach of both, which is what this command is for. `--since` is a global sequence
read on the *answering* node, not this one (from that node's own diagnosis, or from a sequence in a
log — no `h9k` command prints a node's own global sequence today), or the word `all` for everything
it holds; it is required, because the two are very different
asks. A peer serves a pull that names what it wants from below its own replication switch-on
point, as it serves a brand-new node's own bootstrap; an ordinary flush and a gap-fill are both
still held above. Answers apply by origin event id, so pulling over streams
this node already holds changes nothing, and a private task or idea is never served however far
back the pull reaches. A stream this node holds only the tail of stays as it is, for the reason
`task pull` names above. Like `task pull` it queues one project-wide events-request and returns,
touching no git and no network of its own. Background:
[concepts.md](concepts.md#catching-a-node-up).

`project reconcile <project>` asks every node of *this owner's own fleet* for everything it holds
of the project. The daemon's sweep already does this once per (peer, project) on its own, and
re-asks once if no answer completes inside the outbox squash window, so the command is the lever
for what that rule cannot reach: a reconcile `h9k status` reports stalled, a fleet that changed
shape mid-exchange, or simply wanting the exchange to run again now. One ask per sibling, addressed
to that node rather than broadcast, carrying the same explicit bound `project pull --since all`
does, so each sibling answers from the start of its own log. Unlike the two pull commands it reads
this project's ledger chain live to learn the fleet, the same read `project members` performs;
everything after that read is local, and the daemon's next sweep sends the asks. Each ask restarts
that peer's exchange, so the counts `h9k status` then shows for it are this exchange's rather than a
previous one's, including how many streams are still held tail-only.

### Project settings the prose above only names

`h9k project set` carries most of a project's standing choices, and the paragraphs above explain the
ones with behavior worth a story. These rows are the remaining flags of `project add`, `project set`,
and `project run-skill set`, and what each one does, so that nothing those pages print is absent from
this one.

| Command | What it is for |
|---|---|
| `h9k project add --home <path>` | Puts the project's home directory somewhere other than `~/.hall9k/projects/<name>`, on any drive. |
| `h9k project add --base-branch <branch>` | Names the branch task branches are cut from and `repo/dev` is checked out on, `main` by default. |
| `h9k project add --repo <path>` | Registers against a repository that already exists on this machine instead of cloning one, which leaves the home's `repo/` unmaterialized and is rarely what you want. |
| `h9k project add --no-home` | Registers the project without creating a home directory, for a project whose files live somewhere the recipe should not touch, so it requires `--repo`; `h9k project init` gives it a home later. |
| `h9k project set --repo <path>` | Points the daemon at the local repository it cuts worktrees from, which ordinarily follows the home and needs setting by hand only after a relocation moved the clone. |
| `h9k project set --verify <name=command>` | Sets a verification gate, repeatable, replacing the whole list, and each gate is run once against a clean checkout of the base branch before it is accepted. |
| `h9k project set --verify-gate-filter <name=filter\|none>` | Marks one `dotnet test` gate host-coupled, so it runs only at a run's first verification and its final full pass and is serialized against other runs' copies on the node. |
| `h9k project set --accept-broken-gate` | Records a `--verify` gate that fails on a clean base anyway, with a loud warning, instead of refusing the whole command. |
| `h9k project set --model <model>` | Sets the model this project's sessions run on unless a task or the node's per-role default says otherwise, with `default` clearing it. |
| `h9k project set --orchestrator-model <model>` | Sets the model this project's orchestrator window runs on, independent of `--model`, with `default` clearing it back to the node's own resolution. |
| `h9k project set --commit-style narrative\|append\|default` | Chooses whether review fixes are folded into their owning commits or stacked on top, with `default` clearing the project's override. |
| `h9k project set --link <name=url>` | Adds a context link injected into agent prompts, repeatable, and replaces the whole list. |
| `h9k project set --skip-permissions <bool>` | Turns off the `--dangerously-skip-permissions` every new registration already records, since a headless agent cannot answer a permission prompt. |
| `h9k project set --max-compliance-review-cycles <N\|default>` | Overrides the conformance-track cycle cap for this project, sitting between the task and node levels. |
| `h9k project set --max-adversarial-review-cycles <N\|default>` | Overrides the adversarial-track cycle cap for this project. |
| `h9k project set --max-final-full-pass-rounds <N\|default>` | Overrides the cap on consecutive mandatory final-full-pass rounds for this project. |
| `h9k project set --lifetime-review-cycle-budget <N\|default>` | Overrides the task-lifetime review-cycle budget for this project. |
| `h9k project run-skill set <project> --against-commit <sha>` | Records the commit a hand-written run skill was written against, which is left unknown rather than defaulted to whatever `HEAD` happens to be here. |

### The project home

Every project owns a directory on disk, `~/.hall9k/projects/<name>` unless the project says
otherwise, in the same shape on every machine:

```
<home>/
├── AGENTS.md   generated from the project's facts; never hand-maintained
├── decisions.md  rendered from the Decision streams: what this project has decided, and what
│               superseded what; never hand-maintained (`h9k decide`)
├── lessons.md  rendered from the Learning streams: what this project's runs have learned, each
│               with the provenance it was recorded under; never hand-maintained (`h9k learn`)
├── repo/       <name>.git (bare clone) · dev/ (a worktree on the primary branch) · wt-*/
├── ideas/
├── tasks/      _archive/ holds terminal tasks (closed out or abandoned); moved back if reopened
├── skills/     plain markdown skill docs, seeded from the install's canonical set
├── prompt-addenda/  one <builder>.md per prompt builder with an addendum set (below); daemon-owned,
│               materialized from the ledger on every sweep — never hand-edit, it is overwritten
├── recipes/    this project's orchestrator window recipe (below)
├── .claude/    generated Claude Code plumbing: skills/ and recipes/orchestrator-recipe-generator/ symlinked, never copied
├── journal.md  seeded once by the orchestrator-recipe-generator skill; the window's own live
│               state, never regenerated
├── sessions.md seeded alongside it: the registry of sessions this window has spawned
└── notes/      seeded alongside it: prototype-feedback.md holds dated recipe feedback. No
              recipe this platform generates arms a byte-offset log waiter, `tail -F`, or the
              `Monitor` tool — the daemon's own feed courier delivers a project's undrained feed
              items into a live orchestrator window instead, so nothing under notes/ polls for news
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

The same sweep renders `decisions.md` and `lessons.md` at the home's root, from the Decision and
Learning streams rather than from any file anyone edits, and the daemon writes the identical two
files into every worktree it cuts at dispatch, on the repository's own exclude list there so a
projection can never be committed into authored history. A session is pointed at them instead of
at a hand-maintained decisions log: a superseded decision and a retired lesson drop out of the
render without being deleted (`h9k decide list --all`, `h9k learn list --all` still show them),
and the render is deterministic, so two nodes holding the same history render byte-identical
files. A file at either name that the platform did not render is never overwritten.

The same sweep moves a task's whole directory into `tasks/_archive/` the moment it goes
terminal — true closeout (merged, and the closeout monitor observed it) or abandoned — and moves
it back out if it is ever reopened. `_archive`'s leading underscore sorts it to the top of an
editor's file explorer, ahead of every live task, so the one folder everything finished sorts
into is out of the way at a glance rather than interleaved with what still needs attention.

Going from nothing to a working project directory on a second machine (a second node of your own fleet, or a teammate's first node; see [Identity, fleet, and team](concepts.md#identity-fleet-and-team) for what joining involves):

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

### Writing conventions

`h9k project set <project> --writing-conventions "<TEXT>"`

How prose an agent composes for people has to read on this project. The text is pasted verbatim
into every prompt that asks a session to write something a person reads under your login: a pull
request's title and body, the summary comment and the thread replies a review-feedback lap writes,
the note a review lap drafts for `h9k pr approve` or `h9k pr request-changes`, and an attended
`h9k task work` session's own commit messages and drafts. `h9k project show` prints it, and
`default` (or an empty value) restores the platform's own, which is: no em dashes (U+2014), full
sentences over telegraphic fragments, and no AI attribution such as "Generated with Claude" or a
Co-Authored-By trailer.

Two of those rules a machine can also check, so the platform checks them again immediately before
it posts anything it composed. An em dash is rewritten to a comma, a semicolon, or a colon by
context, or simply removed where there is no clause on one side of it for a mark to join, unless it
sits inside a code block or an inline code span, where it is data somebody is quoting rather than
punctuation. A code block is a fence the text actually closes, marker length and all, so a longer
fence quoting a shorter one comes through verbatim, or four-space-indented lines; a fence nobody
closed is malformed markdown rather than a block, and the lines after it are still checked. An
attribution alone on its own line is dropped. An attribution
welded into a sentence somebody wrote has no mechanical fix, whether it sits mid-sentence or opens
the line and then carries on in their own words, so that prose is not posted at all: the daemon
drops it, logs the rule under event id 2005, and opens the pull request on what is left, while the
CLI stops the command before anything reaches GitHub and tells you what to reword. A run never
fails over a convention miss. Everything the check enforces is read off your own conventions text,
and read as a prohibition rather than as a mention: a project that rewrites it and leaves the
em-dash sentence out gets its prose posted exactly as its agent wrote it, and so does one whose
text says em dashes are fine here.


The check runs over composed prose only, never over the platform's own bookkeeping lines or over
your task's objective and acceptance criteria. Those are this platform's voice and your own words
respectively, and neither is text an agent wrote for you.

Origin incident (2026-09-09): a review-feedback follow-up on `AgelessRx/arx-platform#2042` posted a
summary comment under the owner's login with em dashes in most of its paragraphs. The rule existed
in every orchestrator recipe on both nodes and in the operator's own user-level `CLAUDE.md`, and
reached none of them, because `--setting-sources project` drops `CLAUDE.md` from a dispatched
session and no composition prompt carried the rule itself.

### Prompt addenda

```bash
h9k project prompt-addendum set <project> <builder> --file <path>   # replace the whole addendum
h9k project prompt-addendum show <project> <builder>
h9k project prompt-addendum list <project>
h9k project prompt-addendum remove <project> <builder>
```

A team's own house guidance for one shipped prompt builder, set once and spliced into that builder's
every future prompt after its rules section, never replacing or referenced by any of the platform's
own prose: `work` (`h9k task work`, and also the daemon's own fresh-dispatch build run — a task with
no retry branch to resume composes through the identical `WorkPromptBuilder`, so a headless build the
daemon starts on its own carries this addendum too, not only an interactive `h9k task work`),
`review-lap` (`h9k pr review`), `agent` (every daemon-dispatched review and lifecycle prompt
`AgentPromptBuilder` composes that carries project context — follow-up, review, review-fix, and
rebase; its five purely mechanical retry/recovery builders carry no project context today), and
`mention-follow-up`. `set` replaces the whole file; there is no partial edit.
Content past a length cap is refused unless accepted with `--over-cap "<reason>"`, which records the
reason and renders the addendum under a heading that says so.

This node's own event stream is the audit trail `show`/`list` read back (who set it and when); the
ledger is the actual transport, written and read only by the daemon's own sweep, never by a
dispatched agent session — the same "CLI records the fact, the daemon alone writes the ledger" split
`h9k project join`'s own vouch uses. `set` refuses a project with no home yet
(`h9k project init` first): the daemon only ever materializes an addendum under a project's own home
directory, so one set before that exists could never reach a prompt. `list`/`show` also warn when the
daemon has not been able to push a project's own addenda to the ledger for a while, since until that
push lands, nothing above has actually reached a prompt yet.

### The run skill

```bash
h9k project set <project> --discover-run-skill                        # ask for one (or a fresh one)
h9k project run-skill show <project>
h9k project run-skill set <project> --file <path> --shape pointer|full-text|none-discoverable
```

Every project carries a run skill on its ledger: how to stand it up locally, so a review session or
an orchestrator on any member's machine never has to guess. `h9k project add` asks for one at
registration; `--discover-run-skill` asks again once the repository's launch story has changed, or
after a discovery failed. Registration asks once the home is built rather than before, and the
daemon leaves a request standing while there is still no checkout with files in it to read, so a
project whose repository is a long clone is discovered when the clone lands instead of being told
to repair a home that is fine. Only after half an hour with still nothing there is that recorded
as a failure.

A discovery that fails over a project that already has a skill leaves the old one in place, and
both `h9k project show` and `h9k project run-skill show` print the failure beside it, so a skill
the repository may have outgrown never reads as current.

The order of work is tools before tokens. The daemon surveys the repository first — root briefings,
`docs/`, `.claude/skills/*/SKILL.md`, build manifests — and hands what it found to a read-only
discovery session, so no session spends turns on a directory listing. A repository the survey finds
nothing in never gets a session at all: the daemon records the none-discoverable skill itself, and
`h9k project show` reads `run skill: none discoverable` rather than nothing.

Every run skill is the same six sections in the same order (prerequisites, one-time setup, launch,
how to know it is up, address or entry point, human steps), and its first line states which of two
shapes it is. **Pointer** means the repository already documents launching it, so the skill points
at those files by path and adds only what they leave out. **Full text** means it does not, so the
skill holds the whole procedure and cites the file each step derives from. Which of the two it is
is the composing agent's call, made from the files rather than from the scan's guess. Anything the
agent could not determine — a secret, a login, a service it could not reach — goes under human
steps with what is needed, and is never guessed at.

The session never writes the ledger. It reports the markdown in its own summary, the daemon records
the event (with the author, the time, and the commit the daemon itself read with `git rev-parse
HEAD`), and the daemon's own sweep writes `run-skill.md` on `refs/hall9k/ledger/run-skill` — the
same "CLI records the fact, the daemon alone writes the ledger" split the prompt addenda use.
`run-skill set --file` goes through the identical event and the identical six-section check, so a
hand-written skill reads the same way as a composed one.

### Branch naming

`h9k project set <project> --branch-template "<TEXT>"`

A team's branch convention is a project setting rather than a fork of the platform. The template
names a task's branch out of three tokens: `{shortid}` (the task's short id), `{slug}` (its
objective, lowercased and hyphenated, capped at 30 characters) and `{key}` (the linked Jira key or
GitHub issue number). Everything else is literal, so `--branch-template "{key}-{slug}"` cuts
`ARX-14-add-rate-limiting` on a task adopted from ARX-14. The default is `task/{shortid}-{slug}`,
which is exactly the name the platform cut before the setting existed, so a project that sets
nothing sees no change at all; `none` restores it.

Two rules make the feature safe rather than merely convenient. The template is rendered and checked
as a legal git ref at `project set` time, so a name git would refuse is refused where you can still
fix it instead of at the dispatch it would fail. And every token is fixed at or before dispatch: the
task's id cannot change, its objective cannot be revised once it leaves Draft, and its external item
cannot be relinked to a different one. That matters because the rendered name is recorded on the run
and pushed verbatim much later, when the pull request opens — a branch name that could drift between
those two moments is the same failure a hand-renamed branch caused on the Windows node on
2026-08-31, where the push hit a refspec that no longer existed and the task parked Failed. A task
carrying no linked item renders `{key}` as `no-key`, saying that nothing was observed rather than
eliding the segment or inventing a card number.

That fallback is not a narrow edge case: under either tracking backlog policy (`--backlog jira` or
`--backlog github-issues`) it is the ordinary outcome for a task the platform itself publishes and
dispatches. A `jira` card is minted minutes later by a separately dispatched session, well after
dispatch has already rendered and recorded the branch name, so a task published and assigned in
one breath essentially always cuts `no-key-<slug>` rather than `ARX-14-<slug>`. A `github-issues`
project fares better but is not exempt — `TaskPublishCommand` creates the issue inline, but the
`gh issue create` round trip can still lose the race against the dispatch loop's five-second poll.
A `{key}` template earns a resolved key reliably only on a task that already carries its reference
before dispatch ever runs — adopted with `--from-issue` or `--from-jira`, or linked by hand with
`h9k task link-jira` / `h9k task link-issue` while the task is still a Draft.

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
`link-issue`, the same observation-gate pattern `link-jira` uses. Once the issue is linked, publish
writes the task's **record** to the project's own ledger — every node that shares the project can
adopt the same task from it, whichever tracker item it does or doesn't carry
([scope.md](scope.md#the-task-record-in-the-ledger)) — and `h9k task revise` rewrites it afterwards.
The issue itself carries no record at all; only its acceptance-criteria checklist is regenerated,
and only when a revision actually replaces the criteria, leaving the rest of a human's own edits to
the issue's prose alone. `--backlog-routing`
is read as a comma-separated label list under `github-issues`. `jira` is agent-mediated (below);
`none` is today's default, unchanged behavior. A task adopted with `--from-issue`/`--from-jira`
already carries its reference, so the gate never fires for it.

### Closing a linked issue

`h9k project set --close-linked-issue on-closeout|never|when-all-tasks-close|default` ·
`h9k project set --never-close-labels <LABEL,LABEL,...>` ·
`h9k task publish --close-linked-issue …` · `h9k task revise --close-linked-issue …`

When a task's pull request merges, closeout always comments the linked GitHub issue with the
merge note. Whether it also closes the issue is a configurable rule, because an issue is not
always one unit of work — an epic, a PRD, an ADR, or an issue split into several tasks needs the
issue left open even though one task covering it just merged. `on-closeout` closes the issue in
the same step as the merge note, every time; `never` posts the note and never closes it;
`when-all-tasks-close` (the default) posts the note every time and closes the issue only once
every task linked to it has itself reached true closeout or been abandoned, decided fresh at the
last one. A task overrides the project's default with `--close-linked-issue` at `h9k task publish`
or `h9k task revise`; `default` clears the override. `--never-close-labels` forces `never` for an
issue carrying any of the listed labels, regardless of the project's own default — but a task's
own explicit override still wins over the label. When several tasks link the same issue, the
decision is made across every linked task's own recorded rule: an explicit `never` on any of them
keeps the issue open, otherwise an explicit `on-closeout` or `when-all-tasks-close` on any of them
closes it, otherwise the label list and then the project default apply — so a single override on
one sibling is enough to decide the outcome for the whole issue. `when-all-tasks-close` waits for
this scan until every other linked task has itself reached true closeout or been abandoned, decided
fresh at the last one; `on-closeout` has no sibling to wait for and runs the same scan immediately,
so a sibling's explicit `never` still keeps the issue open even though this task's own rule alone
would have closed it. `h9k task show` renders the effective value and whether it is inherited or set explicitly.
Jira is untouched: a card's merge comment behaviour does not change, and the card is never
transitioned or closed.

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
(Decisions Log #102, #114). `write-jira` validates the payload (a transition or a close is refused
regardless of who composed it), records the intent before anything is sent, executes it against
the Jira Cloud REST API, and verifies by reading the item back before recording the outcome — the
same observation-gate pattern `link-jira` uses for a pre-existing card. A project set to `--backlog
jira` makes the publication request automatically at publish — once the dedup gate above lets the
publish through — so `push-to-jira` becomes the manual retry lever for a project that had no Jira
connection registered yet when it was first published, and also the way to get a card started by
hand before publishing, which the gate recognizes as a publication already pending rather than
demanding an attestation for it.

`h9k connection add jira --site <url> --email <address>` registers the Jira Cloud connection: the
site must be `https`, because every request carries the API token in an `Authorization` header, and
the token is only usable with the account whose email it belongs to. The token itself is named one of
three ways, or prompted for when none is given: `--token <token>` stores it under
`~/.hall9k/credentials` readable by you alone, and lands in your shell history; `--token-env
<variable>` records the name of an environment variable and never copies the value, with the catch
that `h9kd` inherits the environment it was started in, so a variable exported after `h9k daemon
start` is invisible to the daemon until it restarts; and `--keychain <service>` names a macOS
keychain item you already created and is macOS only. `h9k task write-jira --op create|update|comment`
says what a Jira write does, and a transition or a close is refused whatever is passed; `--issue
<key>` names the item to update or comment on, is optional when the task already carries a linked
item, and takes precedence over it when both are present.

### Install and the daemon

`h9k install` · `h9k update` · `h9k uninstall [--purge-data]` · `h9k daemon start | stop | status` ·
`h9k daemon autostart enable | disable` · `h9k daemon autostart launch`

`h9k daemon autostart launch` is the one command in this section nobody types: it is the vehicle
the Windows logon task runs, and it exists to open `h9kd.log` with an append handle the daemon can
inherit, since a handle cannot be passed through the VBScript command line the registration
composes (Decisions Log #222). See [INSTALL.md](INSTALL.md) for the Windows mechanics around it,
including the one-time `h9k daemon autostart enable` an already-registered machine needs.

`uninstall` takes the platform off the machine — binaries, PATH link, autostart, and everything
else `install` itself wrote under `~/.hall9k` — but leaves a registered project's home,
credentials, and the `hall9k-postgres` Docker container's data volume untouched by default, so a
later `install` reconnects to it. `--purge-data` is the only path that destroys the volume too,
and it asks first.

| Command | What it is for |
|---|---|
| `h9k install --repo <path>` | Publishes from a hall9k repository checkout, meaning the directory holding `Hall9k.slnx`, taken as given and never searched upward. |
| `h9k install --from-release <dir>` | Installs from an already-downloaded, already-extracted release payload (binaries, `skills/`, `templates/`, and a `VERSION` file) instead of building, which is what the bootstrap scripts and `h9k update` use. |
| `h9k install --restart` | Restarts a running daemon onto the fresh binaries without asking. |
| `h9k install --no-restart` | Leaves a running daemon on its current binaries, and it picks up the new ones at its next start. |
| `h9k update --repo <owner/repo>` | Names the GitHub repository releases are fetched from. |
| `h9k update --restart` | Restarts a running daemon onto the fresh binaries without asking, the same as on `install`. |
| `h9k update --no-restart` | Leaves a running daemon on its current binaries until its next start. |
| `h9k daemon start --binary <path>` | Starts an explicit `h9kd` binary directly, rather than through a registered autostart job, where a relative path is resolved against the current directory and must exist. |
| `h9k daemon autostart launch --binary <path>` | Names the installed `h9kd` this launch starts, which is the one `autostart enable` recorded and is never resolved afresh. |
| `h9k daemon autostart launch --log <path>` | Names the log `h9kd`'s standard output and error are appended to, `~/.hall9k/h9kd.log` by default. |

Covered in [operations.md](operations.md#the-daemon-lifecycle) and [INSTALL.md](INSTALL.md).

### The daemon's operating settings

`h9k config show` · `h9k config set`

The node ceiling (`--max-concurrent-task-runs`, counted directly in task runs), the per-run
session cap's global default (`--session-cap-per-run`, overridable per task with
`h9k task set-session-cap` even mid-run), the per-role model overrides, the interactive-claim
staleness threshold, the node-level review-cycle caps (compliance, adversarial, final-full-pass,
and the task-lifetime budget — each overridable per project, and per task too via
`h9k task set-review-caps`), the review stage composition (`--review-stage-composition
<VALUE>`, [above](#tasks-development-and-dispatch); the node level has no clearing word and needs
`--accept-reduced-review` for a value that removes a load-bearing guarantee, Decisions Log #129),
a periodic token-spend budget (`--spend-budget <tokens|none>`
paired with `--spend-period <day|week>`, backlog: spend-governor step three, Decisions Log #120) —
once the current period's recorded spend reaches the budget, the dispatcher declines to claim
further queued work until the period rolls, gating claims only and never touching work already
claimed; `--spend-budget none` clears it back to unbudgeted, since "no budget" has no compiled
default number the way the review caps do; and the message sweep's own poll ranges
(`--message-poll-active-min`/`-max`, 15 to 25 seconds by default, and `--message-poll-idle-min`/
`-max`, 30 to 45 seconds by default; idea 202383dc, M1b), each a floor that must stay at or below
its own ceiling once this call's change applies; and the two caps bounding how much of a project's
recorded lessons any one dispatched prompt carries (`--lesson-prompt-max-lessons`, 15, and
`--lesson-prompt-max-characters`, 4000; idea d805fd8b, piece 5), two rather than one because
thirty short lessons and three essays are each a prompt nobody reads. Unlike the four review-cycle caps above, the
spend budget and the per-role model overrides (`default` clears an override) both have a real way
back once set. Every one of these is durable in the platform config file so a fresh machine or an
autostarted daemon runs with the operator's settings without an environment variable ritual, and
every one except the interactive-claim staleness threshold, the invite expiry, and the two
lesson-prompt caps takes effect only on the daemon's next start; those four are read fresh from
the file at the moment each is used, so they are in force the instant they are written.
`h9k status`'s own Queued section names a stopped concurrency or spend gate honestly, but only for
whatever a running daemon last confirmed, so raising a spent budget still needs a restart before
the queue moves again. `show` resolves and names each setting's origin (environment variable,
config file, or built-in default); `set` merges a change into the file. See
[operations.md](operations.md#daemon-operating-settings).

The rest of what `h9k config set` takes, one sentence each. The message-poll and invite-expiry options
are in [the distributed-team tables](#the-distributed-team-identity-fleet-and-holding), and the two
lesson-prompt caps are under [Decisions and lessons](#decisions-and-lessons).
[operations.md](operations.md#every-key-in-the-hall9k-section) has the config-file key, the
environment variable that outranks it, and the default for every one of them.

| Command | What it is for |
|---|---|
| `h9k config set --max-concurrent-task-runs <N>` | Sets how many task runs may be live on this node at once, which is the node's own admission ceiling. |
| `h9k config set --max-concurrent-agent-sessions <N>` | Is the retired session-denominated ceiling, still writable and still read as a fallback that converts to `floor(n/2)` runs, minimum one, when the run-denominated setting is absent. |
| `h9k config set --session-cap-per-run <N>` | Sets the global default for how many agent sessions one run may hold at once, which `h9k task set-session-cap` overrides per task. |
| `h9k config set --default-model <model>` | Sets the platform default every agent session runs on unless a more specific level says otherwise, with `default` clearing it back to the built-in `claude-opus-5[1m]`. |
| `h9k config set --orchestrator-model <model>` | Sets the model the node's orchestrator window runs on, independent of `--default-model`, with `default` clearing it. |
| `h9k config set --model-build <model>` | Sets this node's model for the Build role, the session that writes the feature. |
| `h9k config set --model-review <model>` | Sets this node's model for the Review role, the independent reviewer over a run's diff. |
| `h9k config set --model-review-verify <model>` | Sets a narrower override for the Verify-shape review pass, which falls through to `--model-review` when unset. |
| `h9k config set --model-review-finalpass <model>` | Sets a narrower override for the mandatory final full pass, which falls through to `--model-review` when unset. |
| `h9k config set --model-fix <model>` | Sets this node's model for the Fix role, the session that applies review findings. |
| `h9k config set --model-synthesis <model>` | Sets this node's model for condensing a fan-in of blocker handoffs. |
| `h9k config set --model-refinement <model>` | Sets this node's model for the future draft-refinement role, which is configurable before it exists. |
| `h9k config set --model-publication <model>` | Sets this node's model for writing a task up as an external tracker card. |
| `h9k config set --model-courier <model>` | Sets this node's model for the feed courier, where `default` does not clear to the platform default because the courier's own floor is `claude-sonnet-5`. |
| `h9k config set --max-compliance-review-cycles <N>` | Sets this node's cycle cap for the conformance review track, with no clearing word, so the way back is the compiled default's own number. |
| `h9k config set --max-adversarial-review-cycles <N>` | Sets this node's cycle cap for the adversarial track, with the same resolution order and the same lack of a clearing word. |
| `h9k config set --max-final-full-pass-rounds <N>` | Sets this node's cap on consecutive mandatory final-full-pass rounds. |
| `h9k config set --lifetime-review-cycle-budget <N>` | Sets this node's task-lifetime review-cycle budget, counted across every run and follow-up a task has had. |
| `h9k config set --spend-budget <tokens\|none>` | Sets the node's periodic token-spend budget, past which the dispatcher declines further claims until the period rolls, and `none` clears it. |
| `h9k config set --spend-period day\|week` | Sets the window the spend budget resets on, `week` by default, in UTC with the week starting on Monday. |
| `h9k config set --review-stage-composition <value>` | Sets the node's review stage composition, one of `full-pipeline`, `adversarial-only`, `conformance-only`, `skip-final-pass`, or `none`. |
| `h9k config set --accept-reduced-review` | Acknowledges the consequence of a composition that removes a load-bearing review guarantee, which those values require. |
| `h9k config set --interactive-claim-stale-after-days <days>` | Sets how many days an interactive claim can sit untouched before `h9k status` nudges about it, three by default, read fresh on every render. |

**Two of these settings decide which model runs what, and they are deliberately independent.**
`--default-model` is the bottom of the agent-dispatch chain: a session's model is the task's own
`--model`, then this node's per-role default, then the project's `--model`, then the default model,
which ships as `claude-opus-5[1m]`. `--orchestrator-model` is a separate lever for the
window you talk to, not for the sessions it dispatches. It is the model that `recipes/settings.json`
is rendered for, so raising or lowering the model dispatched agents run on never moves your own
window, and the reverse. A window's model resolves as the project's `--orchestrator-model`, then the
project's `--model`, then the node's `--orchestrator-model`, then the node's `--default-model`, then
the platform fallback, and `default` clears an override at whichever level it was set. `h9k config
set` re-renders the node's own `recipes/settings.json` on the spot, and `h9k project set` does the
same for that project's file, but a project window that defers to the node is caught up only the next
time `h9k project init` runs or that project's own model changes. `h9k config show` and `h9k project
show` print the resolved value and where it came from.

The node ceiling has a per-project counterpart in the same denomination:
`h9k project set <project> --max-parallel-tasks <N|default>` (Decisions Log #140) caps how many of
one project's task runs may be live at once. It is a ceiling, never a reservation — nothing is
held free for an idle project — `0` pauses the project, and it lives on the project's own stream
rather than in the config file, so a change lands on the next dispatch cycle with no restart.

Which project a free slot actually goes to is a rotation (Decisions Log #141): the eligible
project longest unserved since its last dispatch wins the next one; which of its own tasks takes
the slot is decided by rank before assignment age (Decisions Log #188) — a
follow-up lap past its first pull request outranks a retry or hand-back, which outranks a plain
first claim — with no configuration and nothing to set on a single-project node beyond that.
`h9k project set <project> --priority high|normal|low|default` overrides it for a focus — a higher
tier wins every free slot while it has ready work and releases itself the moment its queue drains,
which is what makes it the opposite lever to the sticky cap-0 pause; `default` is the clearing
word, putting the project back in the rotation. Nothing preempts either way:
ordering decides only who receives the next free slot, and every claim logs one sentence naming
the winner and why. See [operations.md](operations.md#who-gets-the-next-free-slot).

### Orchestrator windows

`h9k orchestrator node [--cli]` · `h9k orchestrator project [PROJECT] [--cli]` ·
`h9k orchestrator register --project --session --pid [--cli] [--replace]` ·
`h9k orchestrator deregister --project --pid` · `h9k orchestrator status [--project]` ·
`h9k orchestrator feed --project <name> [--drain] [--since <time>]` ·
`h9k orchestrator launch-text show | set` · `h9k orchestrator measure`

Never launches anything — the design's own explicit refusal to have Hall9k spawn an interactive
session. `node`/`project` print that window's daemon liveness, its launch text (the exact line to
paste into a fresh terminal to start one), its recipe and journal paths, and its last-measured
cost or "not measured"; with no project named and more than one registered, `project` prints one
block per project rather than guessing. `feed` is what a window runs at start-up to learn what
happened while none was live: everything past that project's own cursor that its
`--orchestrator-feed` band admits, oldest first, grouped by task, each written as one plain line
by the feed's own table of event type to wording. `--drain` moves the cursor after printing; without it the same items
come back, which is what makes a plain read repeatable. `--since` reads history and never moves
the cursor, so two windows catching up cannot consume each other's news, and the two flags are
refused together. It is a cursor over this node's own event log plus a filter, never a second
store — see
[concepts.md's The orchestrator feed](concepts.md#the-orchestrator-feed) for the three bands and
what each one carries. `launch-text show`/`set` reads and replaces the launch
line for a given agent CLI (`--cli`, default `claude-code`) — the node's own in the platform
config file, a project's own with `--project` — and `measure` runs a
fixed, cheap-model, non-interactive probe against it so a "lean window" claim is a number, not an
adjective. The recipe content itself (what the window is, its start-up sequence, how it spawns
scoped sessions) is never platform-rendered — only a tiny, always-overwritten hand-off file is —
written instead by the `orchestrator-recipe-generator` skill, which `h9k install` and
`h9k project add`/`init` publish and seed. See
[README's Orchestrator windows](../README.md#orchestrator-windows) for the full picture.

A window that is already open does not have to run `feed` again to stay current: the daemon's
**feed courier** spawns a short-lived, cheap-model session that delivers a project's undrained feed
items into the live window and exits, once the feed has something to say, a window is registered
live for the project on this node, no courier is already running, and a batching wait has elapsed.
Urgent items (a park, a dispute, daemon trouble, a message from a person) go at once. Two settings
tune it: `h9k project set <project> --courier-max-wait <seconds>|default` is that project's ceiling
on the batching wait (sixty seconds by default), and `h9k config set --model-courier <model>` is the
node's model for the role, which bottoms out at `claude-sonnet-5` and does not clear to the platform
default. The mechanism is in [concepts.md's The feed courier](concepts.md#the-feed-courier).

`register`/`deregister`/`status` are how the platform knows whether a window is actually up for a
project on this node. A window registers itself as the anchor's first start-up step, naming its
session, process id, and agent CLI; the recipe's restart and close steps deregister it, naming
that same process id, so the audit trail exists without the operator doing anything. A window
drops its own claim and only its own: `deregister` against a registration some other window holds
reports whose it is and leaves it standing, so a stale window's close step cannot unregister the
replacement that took over from it. Presence has to be declared rather than
discovered: Claude Code's own session registry cannot tell the orchestrator window from the
discovery, refinement, and planning sessions running in the same project home, and another
vendor's CLI may keep no registry at all, so liveness is checked against the process id alone.
Registering the same session again is a no-op; a second live orchestrator for the same project on
this node is refused naming the live one, unless `--replace` records that one as shut down first.
The daemon's presence sweep records a registered window whose process is gone as lost, and a
`register` that finds the previous window already gone records that loss itself in the same
write, so no window ever leaves the stream without an ending. `status`
and the `h9k status` header print the same line: the live window's session, CLI, process id and
age, or "none live" with the last shutdown or loss time.

### Flags that repeat across commands

A few flags carry the same meaning wherever they appear, and a handful of one-off flags are easy to
miss in the sections above.

| Command | What it is for |
|---|---|
| `h9k task register-session\|verify\|deliver\|delegate\|handback\|release --force` | Proceeds even though the claim's interactive session was recorded on another machine this one cannot check, which attests that you confirmed by hand that it has exited. |
| `h9k task handback --reason "<why>"` | Records why a headless agent is finishing the task, on the stream and in the follow-up's context. |
| `h9k task unassign --reason "<why>"` | Records why the task is being taken back, and leaves it unknown when omitted rather than inferring one. |
| `h9k project remove --reason "<why>"` | Records why a project is being archived, and leaves it unknown when omitted. |
| `h9k epic close --reason "<why>"` | States why the epic is done, which is required because closing without a reason is exactly the automatic close this platform never does. |
| `h9k epic list --state open\|closed\|all` | Filters epics by their state, and shows only the open ones by default. |
| `h9k task revise --objective "<sentence>"` | Replaces the objective with one outcome-phrased sentence, on a Draft. |
| `h9k task revise --criteria "<criterion>"` | Replaces the whole acceptance-criteria set, repeating the option for each criterion. |
| `h9k task revise --stacked-on-pull-request <number>` | Declares the stacked edge against a teammate's pull request instead of a local task, the same as on `task add`. |
| `h9k task write-jira --file <path>` | Names the JSON payload a card-authoring session composed, whose `format` may be `markdown` or `plain` and never `html`. |
| `h9k learn record "<claim>" --distilled-from <id>` | Records a merged lesson with the lessons it absorbed, repeatable, and the decider refuses a citation that resolves to nothing. |
| `h9k decide import --project <project>` | Names the project whose rulebook is being imported, defaulting to the project of the run you are importing from. |
| `h9k idea assign --project <project>` | Sets the project an idea belongs to when capture did not know it, or changes it when discovery says otherwise. |
| `h9k idea promote --project <project>` | Names the project the one task belongs to, required unless the idea is already assigned to one. |
| `h9k pr review --project <project>` | Names the project whose repository the pull request belongs to, and is optional when exactly one project is registered. |
| `h9k doctor --yes` | Remediates without asking, by starting Hall9k's own Postgres and creating the schema, which is what a script or a dispatched agent wants. |
| `h9k uninstall --yes` | Skips the `--purge-data` confirmation prompt, which is required in a non-interactive session and has no effect without `--purge-data`. |
| `h9k install --now`, `h9k update --now` | With `--restart`, restarts the daemon at once instead of waiting for a live verification gate to finish, though `h9k daemon stop`'s own warning still prints. |

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
Jira write, verifies by reading the item back with its own follow-up REST call before recording
the outcome, so what gets recorded is what Jira answered rather than what the agent's own claim
was — it loads the registered Jira connection to authenticate every call against the right tenant
explicitly, the same strict lookup `h9k doctor` uses, refusing rather than guessing when more
than one is registered.
`h9k task link-jira` reads the key back through the connection before recording it, so what gets
recorded is what Jira answered rather than what the agent claimed. `h9k task link-issue` is the
same gate for GitHub — read back through `gh` before recording — and the platform's own
`gh issue create` claim goes through it exactly as an agent's would. When you add a command an
agent will call, that is the shape to follow.

`h9k task log-interaction` (above) is the one deliberate exception: it records an outside
interaction unconditionally, with nothing external to read back and verify the claim against, so
it is honest best-effort rather than an observation gate — the platform records what its own
channels can see, no more.

Two commands an agent might reach for do not exist yet: `h9k ask` and `h9k answer`. The design is
settled and the events are already on the task stream, but the commands are Slice 2. An agent
that needs a decision today makes the most reasonable call and records the assumption in its
handoff. See [scope.md](scope.md).
