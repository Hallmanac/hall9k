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
| `h9k task show <id>` | One task in full: contract, dependencies, external reference, conversation, every run and its outcome, each run's own gate wall-clock durations, a flag when one materially exceeds the project's recent recorded average for that gate, and each run's own worktree path, branch and live agent sessions. The second command of any investigation. |
| `h9k logs <id>` | A run's transcript, rendered from its stream-json (`--raw` for the stream-json itself). The log dive `h9k status` is meant to save you. |

### Ideas: capture and discovery

`h9k idea add | list | show | revise | assign | promote | discard`

Capture is one command with one argument and an optional project. Revision has no ceremony,
because nothing dispatches from an idea and there is no promise an edit could break. `promote` is
the hinge to a draft task and is the only step that requires a project.

### Tasks: development and dispatch

`h9k task add | revise | set-session-cap | set-review-caps | publish | assign | unassign | draft | list | show | log-interaction`

`add` creates a Draft. `revise` is Draft-only, with one exception: `--queue-first`/
`--clear-queue-first` sets or clears a task-level scheduling marker — the next free dispatch slot
takes this task regardless of assignment age — and is settable in any live state except Abandoned
(Decisions Log #127). `publish` is the readiness gate. `assign` is the
dispatch trigger. The path back for an edit is `unassign → draft → revise → publish → assign`.
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

The one exception, and not really one: an issue **another hall9k install published** carries a
machine-readable task record at the foot of its body, and `--from-issue` reconstructs the whole
draft from it — criteria become criteria rather than context, the context body becomes the agent
context, and type, model, caps, dependencies (as issue numbers) and the epic (by title) all carry
over. Pre-approval does not: the record states the origin's answer and this install gives its own
with `--pre-approved` (default off), which the output names. The record is read once, here, and
never re-checked, so the origin's later revisions reach this copy only by adopting again;
`h9k task show` says which install published it and under what id. See
[scope.md](scope.md#the-task-record-on-a-published-issue).

`--file task.md` reads a whole task from a markdown file: a minimal `---` frontmatter block
(project, type, objective, criteria, an optional model, optional blocked-by, optional stacked-on,
optional epic)
followed by a body that becomes the agent context — or a `context:` block scalar, which is the form
the task record on a published issue uses. The document grammar is deliberately not whole-document
YAML, since a handful of known keys does not warrant the dependency and this platform's own
`task.md` renders plain scalars carrying colons; each **value**, though, is read as a real YAML
scalar, so a double-quoted objective or criterion is stored without its quote characters and a
`|` block scalar arrives as the multi-line text it denotes. The numbered [`backlog/`](../backlog) files are
written in that format; the `IDEA-` notes beside them are earlier-stage prose with no
frontmatter, so they are read and authored from rather than fed to `--file`.

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
wakes you; a request left standing holds the wait open without re-announcing itself. It reaches Done
when every thread you opened is resolved with no re-review requested of you, or when the pull
request merges or closes. `h9k task abandon <id>` is how you stop watching early — and it is the
only lever that does, since `h9k task resolve` is the attestation exit from a Failed task alone. A review with nothing outstanding on it (an approval with no threads, a
report you dismissed without posting) sits in Waiting for one poll interval and then closes out —
and an answer that arrives *before* that first poll is caught by it rather than lost, which is the
whole point of counting replies instead of comments.

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

### Recovery

`h9k task retry | resolve | abandon` · `h9k pr resolve` · `h9k review resolve` ·
`h9k review proceed` · `h9k review fixed`

Seven levers, and picking the wrong one loses work. [operations.md](operations.md#the-recovery-levers)
is the decision table. Two are interactive mode's own: `review proceed` is the bare-approval lever
for a routine phase-boundary park, alongside `review resolve`'s redirect verbs, and `review fixed`
is the newest — you did the fix yourself, in your own worktree, and the review agents check it the
way they would check a fix session's. It applies at the review-verdict-to-fix boundary only (on
either side of the pull request), refuses over an uncommitted worktree, and refuses an unmoved
branch tip unless `--no-change "<why>"` says why. `review resolve` also carries the one lever whose
effect another person sees: on a park where a fix lap disagreed with a human reviewer's
changes-requested finding, it takes `--post-reply-as-written`, `--post-reply "<text>"`, or
`--post-nothing` alongside your verdict, and that choice is the only way a disagreement ever
reaches the reviewer.

### Projects, owners, connections

`h9k project add | init | list | show | set` · `h9k owner show | set` ·
`h9k connection add jira | list`

`project add` registers a project **and creates its home directory**; `project init` is the same
recipe for a project that has none yet, and the repair path for one that is incomplete. See
[the project home](#the-project-home) below.

`project set` is where the verification gates, the agent model, parallelism, commit style,
context links, skip-permissions, the Jira board binding, the backlog policy (`--backlog
none|github-issues|jira`) and its routing guidance, the review re-request policy, the
project-level review-cycle-cap overrides, the review stage composition (`--review-stage-composition`,
below), the branch-name template (`--branch-template`,
[below](#branch-naming)), the auto-pr-review speed (`--auto-pr-review
off|normal|first|now`, [above](#pull-request-review)), the claim gate (`--claim-gate
off|tracker-assignee`, [above](#the-claim-gate)), the close-linked-issue rule (`--close-linked-issue
on-closeout|never|when-all-tasks-close`, [below](#closing-a-linked-issue)), the writing conventions
(`--writing-conventions`, [below](#writing-conventions)), and the home's location live.
Settings resolve most-specific-wins, and the exact chain differs per setting;
[operations.md](operations.md#per-project-and-per-owner) has the two that matter.

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
├── recipes/    this project's orchestrator window recipe (below)
├── .claude/    generated Claude Code plumbing: skills/ and recipes/orchestrator-recipe-generator/ symlinked, never copied
├── journal.md  seeded once by the orchestrator-recipe-generator skill; the window's own live
│               state, never regenerated
├── sessions.md seeded alongside it: the registry of sessions this window has spawned
└── notes/      seeded alongside it: prototype-feedback.md holds dated recipe feedback
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
writes the **task record** into it — a collapsed section holding the whole task, so a second install
adopts the issue and gets the same task
([scope.md](scope.md#the-task-record-on-a-published-issue)) — and `h9k task revise` rewrites only
that section afterwards, leaving a human's own edits to the issue's prose alone. `--backlog-routing`
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
`h9k task set-review-caps`), the review stage composition (`--review-stage-composition
<VALUE>`, [above](#tasks-development-and-dispatch); the node level has no clearing word and needs
`--accept-reduced-review` for a value that removes a load-bearing guarantee, Decisions Log #129),
and a periodic token-spend budget (`--spend-budget <tokens|none>`
paired with `--spend-period <day|week>`, backlog: spend-governor step three, Decisions Log #120) —
once the current period's recorded spend reaches the budget, the dispatcher declines to claim
further queued work until the period rolls, gating claims only and never touching work already
claimed; `--spend-budget none` clears it back to unbudgeted, since "no budget" has no compiled
default number the way the review caps do. Unlike the four review-cycle caps above, this and the
per-role model overrides (`default` clears an override) both have a real way back once set. Every
one of these is durable in the platform config file so a fresh machine or an autostarted
daemon runs with the operator's settings without an environment variable ritual, and every one
except the interactive-claim staleness threshold takes effect only on the daemon's next start —
`h9k status`'s own Queued section names a stopped concurrency or spend gate honestly, but only for
whatever a running daemon last confirmed, so raising a spent budget still needs a restart before
the queue moves again. `show` resolves and names each setting's origin (environment variable,
config file, or built-in default); `set` merges a change into the file. See
[operations.md](operations.md#daemon-operating-settings).

The node ceiling has a per-project counterpart in the same denomination:
`h9k project set <project> --max-parallel-tasks <N|default>` (Decisions Log #140) caps how many of
one project's task runs may be live at once. It is a ceiling, never a reservation — nothing is
held free for an idle project — `0` pauses the project, and it lives on the project's own stream
rather than in the config file, so a change lands on the next dispatch cycle with no restart.

Which project a free slot actually goes to is a rotation (Decisions Log #141): the eligible
project longest unserved since its last dispatch wins the next one, oldest task first within it,
with no configuration and nothing to set on a single-project node.
`h9k project set <project> --priority high|normal|low|default` overrides it for a focus — a higher
tier wins every free slot while it has ready work and releases itself the moment its queue drains,
which is what makes it the opposite lever to the sticky cap-0 pause; `default` is the clearing
word, putting the project back in the rotation. Nothing preempts either way:
ordering decides only who receives the next free slot, and every claim logs one sentence naming
the winner and why. See [operations.md](operations.md#who-gets-the-next-free-slot).

### Orchestrator windows

`h9k orchestrator node [--cli]` · `h9k orchestrator project [PROJECT] [--cli]` ·
`h9k orchestrator launch-text show | set` · `h9k orchestrator measure`

Never launches anything — the design's own explicit refusal to have Hall9k spawn an interactive
session. `node`/`project` print that window's daemon liveness, its launch text (the exact line to
paste into a fresh terminal to start one), its recipe and journal paths, and its last-measured
cost or "not measured"; with no project named and more than one registered, `project` prints one
block per project rather than guessing. `launch-text show`/`set` reads and replaces the launch
line for a given agent CLI (`--cli`, default `claude-code`) — the node's own in the platform
config file, a project's own with `--project` — and `measure` runs a
fixed, cheap-model, non-interactive probe against it so a "lean window" claim is a number, not an
adjective. The recipe content itself (what the window is, its start-up sequence, how it spawns
scoped sessions) is never platform-rendered — only a tiny, always-overwritten hand-off file is —
written instead by the `orchestrator-recipe-generator` skill, which `h9k install` and
`h9k project add`/`init` publish and seed. See
[README's Orchestrator windows](../README.md#orchestrator-windows) for the full picture.

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
