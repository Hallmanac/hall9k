# The orchestrator window

**Who this file is for.** An *interactive* Claude Code session in this repo is the
**orchestrator window** (PLAN.md §2, §12): the conversational surface over `h9k`, driving the
platform on a human's behalf. Since the cutover (backlog 52) landed, "this repo" is the project's
own home, opened at `~/.hall9k/projects/hall9k/repo/dev` — the same `repo/dev` worktree shape
every project's home takes, not a standalone checkout. A *headless* session the daemon dispatched
is not one, and it never loads this file automatically — `AGENTS.md` only points at it, it does
not `@`-import it. If you arrived here with a task id, a worktree and acceptance criteria, this is
not your job; stop reading and go back to `AGENTS.md`'s coding standards.

Everything here has been done live, by one interactive session, through the whole v0 build. It is
a record of what proved out, not a proposal.

## The role: a window, not an alarm

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

## The law: all new work enters through `h9k task add`

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
  (backlog 48). AGENTS.md's own Working agreements placeholder rule (`PLACEHOLDER-<shortid>`) is
  for a task's branch, never this window: an orchestrator entry lands straight on `main`, with no
  branch and no rebase step ever coming along behind it to assign a number later, so it takes the
  log's next real number by hand, same as before that convention existed.
- **Reading anything.** Inspecting the tree, the streams, the logs, a PR diff.
- **Unbreaking the platform when the platform is what is broken.** A daemon that will not start
  cannot dispatch the task that fixes it. Do the smallest thing that restores dispatch, then task
  the real fix.

Everything else is a task. When the human says "just quickly add X", the answer is a draft, and
`h9k task publish --assign` is how fast looks around here.

## Taking the wheel

The window can also put its own hands on a task's code instead of only drafting the task and
stepping back — claiming a Published task (assigned to nobody, or already Queued or Blocked) and
either building it directly in an interactive session (`h9k task work`) or dispatching it headless
on the spot while still standing as the boundary arbiter (`h9k task start`), plus the levers
documented under both in `AGENTS.md`'s *Build / test / run* section. Doing that runs on its own doctrine, settled
in one long design conversation on 2026-09-02 (idea fcaded0b; Take the Wheel epic 9272e514, Slice
6) and cited below by their R-numbers from that conversation's R1-R9 set.

**The continuity principle (R1).** Hall9k records only what it performs or observes. A lifecycle
transition happening outside both — the fully manual bypass, where a human opens a vanilla Claude
Code session in a worktree, builds, opens the pull request, and merges it, telling Hall9k only
after the fact — is explicitly unsupported: that work belongs outside Hall9k entirely. Brian's own
"at least not yet" on the bypass is recorded as the park trigger, not a closed door: if a
diligent-user path for it ever earns its keep, this is where it gets built, rather than a wall
nobody may cross. `h9k task work` is not that bypass, even though a human's own hands do the
build: its one manual region is bounded on both ends by an act Hall9k performs (the claim going
in; `deliver`, `handback`, or `release` coming out), so Hall9k performs every transition even when
a human types the code.

**Two modes (R2, R3).** Hands on the code: the human builds, in their own interactive session (the
`work` path). Hands on the judgment: agents do all the building, reviewing, and fixing, and the
human is the arbiter at the boundaries. Interactive mode is a recorded property of the *task*, not
of the claim that started it (design ruling R2): once it is on, it stays on across every later run,
follow-up, retry, or reopen, whether or not a human is still attached, until one of the exit doors
below turns it off. Which mode owns the build is a free choice, revisable at any boundary in either
direction — but the choice is scoped to the build alone. Everything after it — review, fix,
closeout — always runs as headless agents; there is no interactive review, fix, or merge path, only
an arbiter watching one park at a time.

**The verb map.** Four verbs an operator acts from, each with the one sentence that matters: who is
in charge once it returns.

| Verb | Command | What it does | Who's in charge after |
|---|---|---|---|
| **work** | `h9k task work <id>` | Claims the task for the human, interactively, and turns on the task's interactive-mode flag. | The human, on the build. |
| **dispatch-the-phase** | `h9k task delegate <id> --note "<text>"` | Delegates one phase (today, the build) to a headless contractor dispatched onto the same live claim, briefed by the human's own handoff note. | Still the human — the claim, the assignment, and interactive mode are all untouched; the contractor reports back and `h9k task work` resumes it (design ruling R6). |
| **handback** | `h9k task handback <id> [--first \| --now]` | Ends the human's claim and returns the task to headless dispatch, resuming the branch from wherever it stands, at one of three pickup speeds: the default (normal rotation, ordered by assignment age), `--first` (a recorded priority marker — the next free slot takes it regardless of age), or `--now` (ceiling-exempt, immediate, through `h9k task start`'s own mechanism). | The machine — a handback always clears interactive mode (design ruling R6; the three pickup speeds themselves are R7). |
| **release** | `h9k task release <id> [--keep-interactive]` | Gives an untouched claim back to the queue (refused once there is committed work; `handback` or `deliver` is the lever then). | The machine, exactly as `handback` — `--keep-interactive` is the one stated exception: the operator explicitly asks for a headless run that still parks at every boundary, rather than that being the default. |

`handback` and a default `release` are both genuine exit doors for the same reason: giving a claim
back is itself the explicit human act of returning the task to headless dispatch, and headless
dispatch must not gate phase boundaries for a human who walked away — so both clear interactive
mode unless told otherwise. `dispatch-the-phase` is not an exit door at all: it is staying at the
wheel while someone else drives for one phase.

**No resident agents; conversations run on resume (R5).** The first instinct here — agents staying
alive until the human approves — was reversed deliberately, on the window's own pushback, and
accepted enthusiastically. An agent sends its report and ends normally; the human's reply resumes
it, on the session mesh's own already-observed mechanic (a message to a finished session wakes it
from its own transcript), rather than a Hall9k-specific one. The daemon advances a phase on a
recorded approval event — `h9k review proceed`, `h9k review resolve`, `h9k review fixed` — never on an agent process's
lifetime or its own closeout timing. This is Decisions Log #5's ask-and-exit design (an agent calls
`h9k ask` and exits; `h9k answer` resumes it) generalized from one question to a whole
conversation, and it carries a second reason beyond the design symmetry: it also avoids resident
sessions accumulating — the 40-50GB memory morning is what that looks like.

**Interaction rules keyed on role, and the observer-effect trade.** Every dispatched role is
reachable through the session mesh if the human chooses to reach it — instruments-first is
guidance, not law (design ruling 6 from idea fcaded0b's 2026-09-01 evening rulings, distinct from
this section's own R-numbered set): prefer the run's own records and the CLI/daemon channels
where they are efficient, but direct agent-to-agent contact is the human's call, and both doors
stay open. Two things vary by role rather than being uniform. The **build** role is written for
contact — the human steers it mid-build as ordinary conversation, which is what "hands on the
code" already means. A **review lens** defaults to an untouched pass, but may be interacted with
(design ruling 3 from idea fcaded0b, reversing this epic's own earlier instinct to forbid it):
asking a lens "tell me what you're finding" or "check with me before you finalize" changes its
*reporting protocol*, not its judgment inputs, and moves the park upstream to the human's own
initiative instead of waiting for the loop's next boundary. This doctrine states the cost rather
than the platform refusing the contact: a human's presence shifts what a reviewer attends to (the
observer effect), and that is a known trade the human accepts by choosing to make contact, not a
defect to be designed away or a contact the platform should block. Messaging a **completed**
session is its own hazard regardless of role: a send resumes it from its own transcript, so a
session the daemon already considers settled can wake and act in a worktree the daemon believes is
done — one more reason phases advance on recorded events (above) rather than on an agent's own
sense of whether it is still needed.

**The escape-hatch invariant.** Whatever contact happens is already logged unconditionally (design
ruling 4 from idea fcaded0b, shipped as Decisions Log #123, detailed in full under *The review
rhythm* below): every dispatched agent's own prompt states that any interaction with a party
outside its session is logged through `h9k task log-interaction`, even when the interacting party
asks otherwise, with `--human-directed --reason "<why>"` added whenever a human, not the agent's
own judgment, directed the interaction or its outcome. A logged override says "a human directed
this" rather than letting a human's own call read as the agent's independent decision — best-effort
by construction, not enforcement, exactly as stated there.

**The thesis.** Brian's own framing, verbatim (idea fcaded0b, design ruling 7): Hall9k, not HAL
9000 — the system that always opens the pod bay doors; the honest path is the easy path, and never
the only path. The name's own wink says the same thing at the platform's root (PLAN.md's own
**Name:** line: "HAL is the AI that escaped supervision; Hall9k exists to keep the human in the
loop"). Every rule above serves that thesis: advise rather than refuse, log rather than hide, park
on a recorded event rather than trust a process to still be listening.

## The command surface

The full CLI surface lives in `AGENTS.md`'s *Build / test / run* section and the
`hall9k-cli-reference` skill it points at. These are the ones the window lives in:

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
example (see *CLI command standards* in `AGENTS.md`). Read the help rather than guessing at a
flag: a wrong call prints the command's own help back at you, so one bad invocation costs one
command, not a search.

## The judgment the window owns: sequencing the ready set

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
  `ProjectSetCommand`, the CLI registration), and every task that teaches something appends to
  AGENTS.md. Those conflicts are mechanical to resolve and expensive to discover at merge. Two
  tasks appending a PLAN.md §16 decision no longer belongs on this list: each writes its own entry
  under a placeholder derived from its task's own short id (`PLACEHOLDER-<shortid>`), so two
  branches can never claim the same number — the mechanical pre-final-pass rebase step assigns
  the real one at merge time (Decisions Log #PLACEHOLDER-6df5f975).
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

## Questions and answers: the relay

`h9k status` is where the platform asks for a human. Its **needs-you** section is the whole point
of the pane, and today a row lands there for one of seven reasons:

| Row says | What happened | The lever |
|---|---|---|
| `NeedsHuman`, review parked | The pre-PR review loop spent its automatic fixes, hit a disputed finding (#24, #63), or the task's lifetime review-cycle budget is spent (#112) — that last one can fire on a run that just converged cleanly, since the budget counts every run and follow-up the task has ever had and nothing resets it; a `--needs-fixes` grant there earns one more cycle but re-parks at the next settle point unless the budget itself is raised with `h9k task set-review-caps` | `h9k review resolve` |
| `NeedsHuman`, an interactive-mode boundary park | The task's interactive-mode flag is on (task: interactive mode becomes a recorded property of the task), and the run reached one of the review engine's own four routine phase boundaries — build done to review, review verdict to fix, fix to re-review, gates to pull request — which now hold for the human's recorded go rather than advancing on their own | `h9k review proceed`, or `h9k review resolve` to redirect the boundary instead of merely approving it. At the **review-verdict-to-fix** boundary there is a fourth choice (#148): `h9k review fixed` when the human fixed the findings herself in the worktree and committed — the review agents then check her fix and no fix agent runs |
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

## The recovery levers

Seven levers, and picking the wrong one loses work. The question that separates them is *what
actually failed*.

| Lever | Use it when | What it does |
|---|---|---|
| `h9k task retry <id>` | The task is **Failed** and the machinery is what failed (a daemon bug, a dead process, a push that was rejected). The work has to run again. | Requeues the task. The failure stays on the stream. The new run resumes the failed run's branch when it survived, or starts clean from the base branch when the artifacts are gone (#25). |
| `h9k task resolve <id> --reason "…"` | The task is **Failed** but the objective was met anyway: the work merged, or you finished it by hand, and only the bookkeeping died. | Ends the task Done on your attestation. `--reason` is required (an attestation without a why is a guess) and `--pr` records where the work landed and, when it names a real pull request on the project's own repository, enrolls that pull request in closeout's orphan sweep too, so its later merge completes this task's closeout exactly as it would for any watched run (#27, #116) — except on a **pr-review** task, whose `--pr` names the pull request it reviewed rather than one of its own, and is never enrolled. |
| `h9k task abandon <id> --reason "…"` | You have stopped believing in the work. Reaches every non-terminal state, drafts and published tasks included. | Terminal. Releases any lease. Nothing is deleted: the reason is the record. |
| `h9k pr resolve <id> [--checks \| --rebase]` | The task is **Done**, its pull request is open, and review feedback, failing CI, or a conflict with its base branch needs another pass, either because the monitor spent its budget or because you want one now (`--rebase` is for when you spot the conflict before the monitor's next inspection does, backlog 44). | Dispatches a follow-up run onto the existing PR branch and resets the monitor's automatic retry budget (#20, #22). |
| `h9k review resolve <id> --merge-ready [--reason "…"]` / `--needs-fixes "<why>"` | A run parked **before** its PR, in the internal review loop, and is waiting on your verdict. | `--merge-ready` runs one mandatory full-scope verification gate over the fix unless this tip was already gated at full scope (#98: nothing merges on scoped green alone) and proceeds to the pull request if it passes; `--needs-fixes` dispatches a fix session with your reason as its findings and restores the fix budget (#24). `--merge-ready` is refused when the park is a disputed rebase conflict (nothing has been rebased yet, so there is nothing ready to merge) — only `--needs-fixes` applies there. Either verdict's reason is recorded on the task and carried into every later review pass as a settled ruling (#88) — except on a thread-dispute park (#62), which settles a disputed thread before any reviewer ever read the diff and so is not recorded as a review ruling — so pair `--merge-ready` with `--reason` when you dismiss a finding — e.g. the evidence that dismissed it — rather than leaving the next fresh-context reviewer to rediscover it. A **pr-review** task's own park (§16 #99) refuses `--needs-fixes` outright — it has no diff of its own for a fix session to apply — and takes only `--merge-ready`, once you have walked the findings report and directed each one by hand (`walk-pr-review-findings`); that verdict never opens a pull request, it closes the task Done directly. |
| `h9k review proceed <id>` | An interactive-mode task's run parked at one of the review engine's own four routine phase boundaries (task: interactive mode becomes a recorded property of the task) — build done to review, review verdict to fix, fix to re-review, gates to pull request — with nothing disputed, just the human's recorded go to continue. Refused on a park that IS a dispute or a cap/budget reason; those still take only `review resolve`. | Appends the boundary approval and rings the daemon; the loop resumes exactly where it parked, with no verdict of its own to record. `review resolve --merge-ready`/`--needs-fixes` still applies at these boundaries too, when the human wants to redirect rather than merely approve. |
| `h9k review fixed <id> [--no-change "<why>"]` | An interactive-mode task parked at the **review-verdict-to-fix** boundary specifically — either side of the pull request — and the human did the fix herself in the run's own worktree and committed it (#148). Refused at the other three boundaries, which have no verdict asking for a fix, and on a pr-review task, which has no diff of its own. | Records the fix, pushes when a pull request is already open, and re-enters the loop at the same fix-to-re-review boundary a completed fix session lands on: the gates run over her commits, that boundary asks its own go, then a fresh review pass reads her commits scoped to the parked cycle's own head — exactly as a fix session's would have been. **No headless fix agent runs**, and no automatic fix budget is spent; the review cycle it opens counts as one a fix session opens does. Refused over an uncommitted worktree (naming the files), over a worktree checked out somewhere other than the claim branch, and over an unmoved tip unless `--no-change "<why>"` says why — that reason rides into every later review pass as a dismissal, the way a `--merge-ready` reason does. `--no-change` is refused the other way too, over a tip that *did* move: the commits are that cycle's answer, and a cycle cannot be both. |

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

## The review rhythm

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
   day re-reading 12k-line diffs with two lenses to judge 40-line fixes) — except that a
   ReviewFeedback or FailingChecks follow-up's own opening cycle 1 no longer always reads the whole
   branch either (Decisions Log #139, origin: a single five-cycle rebase lap that cost 109M input
   tokens re-reading a branch its own reviewers had already cleared): both lenses read only the
   lap's own change since the pull request head the previous run pushed, and a Rebase follow-up is
   excluded and unchanged. The mandatory FinalFullPass below still runs at full scope regardless, so
   "nothing merges on scoped green alone" holds for a scoped opening lap exactly as it does for
   every other cycle; `h9k task show` renders the scope seed whenever one applied. A middle cycle
   instead dispatches one **Verify** reviewer, handed the prior cycle's own findings, each finding's fix
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
   only the review content itself is agent judgment. Immediately before that mandatory
   FinalFullPass — the same "nothing merges on scoped green alone" point (idea fc85f609's
   before-push side, completing what 023f08bb's after-push mechanical rebase started) — the run
   fetches the project's base branch and, if it moved past what the branch already contains,
   rebases onto it right there in the run's own worktree, so the mandatory gate and pass that were
   already about to run then read the rebased tree and the pull request that opens afterward is
   mergeable on arrival rather than racing whatever merged into the base while the run was still
   building. Three entry points reach this check: composition none's own settle branch off
   `ReviewPhase.None`, `ReviewPhase.Reverify` when its next cycle really is the mandatory pass, and
   `ReviewPhase.Settling`, which runs it unconditionally on every entry — including the ordinary
   clean convergence that never otherwise touches the mandatory gate at all — rather than only when
   that gate is about to run, since a rebased tip needs the same fresh-context check regardless of
   which path settles it. A no-op (the base had not moved) and a clean git apply are both recorded
   on the run stream (`RunRebasedOntoBase`, rendered by `h9k task show` as "Pre-final-pass rebase")
   and cost nothing else — Brian's 2026-09-04 ruling: git applying every commit without a conflict
   is itself the evidence that no judgment was exercised, so a clean rebase earns no extra Discovery
   cycle, lens, or fix session. A conflict is handed to a narrow recovery session dispatched inside
   this same run — the rebase-onto-main skill's own mechanics, never a task reopen — and a
   recovered conflict, unlike a clean apply, is a reviewer-relevant change: the run is forced
   through one more mandatory FinalFullPass dispatch before it may settle even on the ordinary
   "nothing owed" path, since a session resolved that conflict with judgment rather than a
   mechanical apply (`RunAggregate.PreFinalPassRebaseAwaitingReview`). Only a conflict that session
   cannot honestly resolve parks the run for a human, the same shape a disputed rebase park takes
   today (`h9k review resolve --needs-fixes "<resolution>"` retries it with their guidance;
   `--merge-ready` is refused, since nothing has been rebased yet). Main moving again during the
   final pass itself, or after the push, is the residual case 023f08bb's own closeout mechanical
   rebase and the Rebase follow-up path still cover exactly as before.
4. **The daemon opens the pull request.** Agents never do, and there is deliberately no create-pr
   skill. The task reaches **Done** here, when the pull request opens, so Done means "the work is
   on a PR and waiting on review" rather than "merged". A **pr-review** task is the one exception
   (§16 #99): there is no diff of its own to open a pull request over, so its own park is resolved
   with `h9k review resolve --merge-ready` directly, and the task reaches Done there instead —
   still without any pull request ever opening or merging.
5. **Copilot and the human review it.** The closeout monitor reads unresolved threads and
   dispatches follow-up runs to answer them, bounded by a retry budget (#22, #62). A branch
   obstructed only by a conflict with its own base is not always one of those follow-ups: closeout
   tries a mechanical fetch+rebase+force-push first, in the run's retained worktree, with no agent
   session and no local gates — GitHub's own CI on the push is the authoritative gate here, so a
   local run would only duplicate it (#131). A clean apply is pushed with no reopen at all; only a
   genuine failure (a real conflict, an unusable worktree, a refused push, or a pull request
   retargeted to a base other than the project's own) falls back to the ordinary follow-up dispatch
   above. `h9k task show` prints a **Mechanical rebase** line either way, so a branch rewritten
   under an open pull request is never silent about who did it or when.
6. **The human merges**, and merging by hand is a four-gate check — the same four the daemon's own
   pre-approved merge reads, so a hand merge and an automatic one hold to one standard (#135), and
   every gate is a reason **not** to merge: **CI green**; **the review decision satisfied**
   (`APPROVED`, or no branch rule requiring one — `CHANGES_REQUESTED` and `REVIEW_REQUIRED` are
   both unsatisfied); **no outstanding requested reviewer**; **every review thread resolved**. The
   third is a gate in its own right and not a formality: somebody was asked to look and has not,
   and merging past them spends their goodwill on work they never saw. Copilot is not one of the
   four — it has its own bounded settle window. You do not need a GitHub visit to answer any of
   this: `h9k status` and `h9k task show` name the logins a pull request is waiting on (#150), so a
   Delivered line reading `awaiting review from <logins>` rather than `the merge is yours` is the
   third gate telling you it is shut, and which person it is shut on.
   Ordinarily, the platform never merges — with one deliberate, opt-in
   exception: a task published or later set `h9k task publish --pre-approved` /
   `h9k task set-pre-approved <id> on` (Decisions Log #135) removes the owner as a synchronous gate
   at the pull request. For that task alone, the daemon reads GitHub's own gates — CI, the review
   decision from branch protection, requested reviewers (Copilot handled on its own bounded settle
   window, a required human approval or an outstanding requester stated as a visible wait, never a
   park), and thread resolution — and rebase-merges once every one of them reads satisfied, with no
   agent session involved in the merge itself. `h9k task set-pre-approved <id> after-human-review`
   is the third value (#150): the same automatic merge, held until a human reviewer has actually
   been requested on the pull request and every requested reviewer has approved the current head,
   so a task can start pre-approved and still wait for whoever you add as a reviewer. With nobody
   requested it waits and says so, naming you as the one who adds a reviewer in GitHub or flips it
   to `on` — which is the emergency path and merges on the next sweep. Nothing here names a
   reviewer and nothing here requests a review; both stay GitHub's and yours. Whichever mode a task
   is in — and on one with none — `h9k status` and `h9k task show` name the logins the merge is
   waiting on, so "is it my turn" no longer needs a GitHub visit. Every other human waypoint
   (Failed, a review park, a cap trip) still stops it exactly as it would an ordinary task. The observed merge is true
   closeout either way: it is the moment the run completes, dependents unblock, and the worktree is
   removed. The task was already Done; what the merge changes is everything around it.

The window's part is steps 3, 5 and 6: relay the parks, tell the human when a PR is waiting on
them, and never start a review thread yourself. That last one is not a style preference; the
thread discriminator depends on it (see *Git rules* in `AGENTS.md`).

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
