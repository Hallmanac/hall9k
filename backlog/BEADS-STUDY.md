# Beads: a comparative study against the Hall9k design

**Status:** research note, 2026-08-19. Not a task. Produces one drafted task (`16-learnings-loop.md`)
and a list of follow-ups at the end.

**Subject:** beads (`bd`), Steve Yegge's "distributed graph issue tracker for AI agents",
github.com/gastownhall/beads, docs at beads.gascity.com.

**Standing decision this study operates under:** adopt patterns, not the tool. Hall9k keeps its
event-sourced Marten store and its daemon runtime. Adopting beads would fork the source of
truth, which is the same verdict already recorded against Collaboard (PLAN.md section 17).
Nothing below revisits that; everything below is about which of its ideas earn a place inside
our own machinery.

**Method and its limits.** Everything asserted about beads comes from its README and its
published documentation as read on 2026-08-19. The tool was not installed, initialized, or run,
and no source was read. Where the README and the documentation disagree, both are reported as
disagreeing rather than reconciled by inference. A running list of what could not be determined
is kept in section 7, and no design decision below rests on an unobserved beads fact.

---

## 0. The finding that organizes the rest

Beads' most valuable idea is not memory and not the graph. It is **decay**: the recognition that
an agent-facing corpus grows without bound and that the fix is summarization rather than
deletion or a cap. Its weakest execution is the same idea, because beads applies decay to its
**records**. Its semantic compaction is documented in plain words: "This is permanent graceful
decay - original content is discarded."

Beads has to do that, because in beads the store is the view. One Dolt database holds the issue
bodies, feeds `bd ready`, and is what `bd prime` reads from. Shrinking what an agent sees means
shrinking what is kept.

Hall9k does not have that constraint and should never accept it:

> **Compaction is a property of views, not of records.** Everything Hall9k injects, renders, or
> hands to an agent is a view, and every view gets a budget and an honest truncation notice.
> Everything Hall9k records is append-only and is never summarized in place. The single exception
> is on-disk artifacts, which may be purged only behind a distillate that already exists and a
> recorded purge event.

That sentence is the study's thesis. Sections 1 and 4 apply it; sections 2 and 3 are where it
turns out we already do better than the reference implementation and should say so out loud.

A second, smaller finding is worth stating early because it corrects our own notes:
IDEA-learnings-loop credits beads with keeping full history while compacting the injected view.
It does not. That property is ours to build and is the largest single difference between the
design in `16-learnings-loop.md` and the tool that inspired it.

---

## 1. The memory trio

### 1.1 What beads actually does

**Storage (`bd remember`).** `bd remember "<insight>" [--key <slug>]` stores a persistent memory.
The key is auto-generated from the content when omitted; supplying an existing key updates that
memory in place. `bd recall <key>` returns full content, `bd memories [search]` lists or
keyword-searches, and `bd forget <key>` removes one. The documentation describes removal as
removal, not archival.

One quirk is documented explicitly: passing a bare existing key to `bd remember` recalls instead
of storing, "equivalent to `bd recall`". A write verb that silently becomes a read verb when its
argument happens to match a key is a footgun for a headless agent, and section 1.3 declines it.

Not determinable from the docs: whether memories are a separate table or a bead of some type,
what scopes them (workspace appears to be the unit, but this is not stated), whether there is a
size or count limit, and whether anything ranks them.

**Injection (`bd prime`).** `bd prime` prints workflow context as markdown. It detects whether
an MCP server is active and adapts: roughly 50 tokens of reminders in MCP mode, roughly 1 to 2k
tokens of full command reference in CLI mode. Flags observed: `--export`, `--full`, `--hook-json`,
`--mcp`, `--memories-only`, `--stealth`. A `.beads/PRIME.md` file overrides the default content.

The integration mechanism is the interesting part and is worth copying in spirit. Beads does not
ship an MCP server as its primary Claude Code integration. It installs a **SessionStart hook**
that runs `bd prime --hook-json`, and the documented reason is cost: "MCP tool schemas can add
10-50k tokens to every request; `bd prime` adds ~1-2k tokens." The hook re-fires after context
compaction, so a long session re-primes itself.

Not determinable from the docs: the literal text `bd prime` emits, and whether the memory portion
of it is bounded, ranked, or truncated at all. Given `--memories-only` exists as a mode for
"compact contexts", some budgeting probably exists, but the docs do not say and this study will
not guess.

**Compaction.** Four distinct mechanisms share adjacent vocabulary, and conflating them is easy:

| Command | What it does | Destructive |
|---|---|---|
| `bd compact` | Squashes Dolt commits older than `--days` (default 30) into one base commit, cherry-picks recent commits on top, replaces main, runs Dolt GC | History only, not issue content |
| `bd admin compact` | **Semantic** compaction: summarizes inactive closed issues. Modes analyze / apply / auto (needs `ANTHROPIC_API_KEY`) / dolt. Tier 1 targets issues closed 30+ days for a claimed 70% reduction; Tier 2 (90+ days) is planned | Yes, explicitly: "original content is discarded" |
| `bd mol squash` | Condenses an ephemeral molecule's children into one permanent digest issue | Children go, digest stays |
| `bd admin cleanup` | Deletes closed issues, optionally `--older-than`, `--cascade`, `--ephemeral` | Yes, outright |

Trigger: as documented, all of these are operator-run commands with `--dry-run` and `--force`
guards, not automatic thresholds. `bd admin compact --analyze --json` exports candidates and
`--apply --id` accepts a summary for one issue, which means the intended shape is an agent
proposing and a human or script accepting, one issue at a time.

Not determinable from the docs: what a compacted summary actually looks like. No example output
appears on the pages read.

One genuinely excellent detail sits outside the compaction commands, in the events journal. That
journal auto-prunes on retention floors (7 days or 100,000 rows, whichever protects more), and
when a consumer's checkpoint has fallen below the floor it does not return an empty result. It
refuses with a typed error, `events_journal_truncated`, carrying `floor` and `head` so the
consumer can see the size of the gap, and documents two recovery paths. **That is the honesty
principle expressed as an API contract**, and section 4 argues it should be the template for
`h9k logs` after an artifact purge.

### 1.2 What maps, what does not

| beads | Hall9k equivalent | Note |
|---|---|---|
| `bd remember` | `h9k learn` | Same producer role. Ours records provenance; beads' memories carry no observed source that the docs mention |
| `bd recall` / `bd memories` | `h9k learn show` / `h9k learn list` | Ours splits read verbs from the write verb deliberately |
| `bd forget` (deletes) | `h9k learn retire` (appends) | The central divergence |
| `bd prime` | AgentPromptBuilder | Ours is push at dispatch, theirs is pull at session start |
| SessionStart hook | Daemon-composed prompt | We already own the prompt, so no hook is needed. The cost argument for CLI-over-MCP still applies to any future Hall9k MCP surface |
| `bd admin compact` | `h9k learn distill` | Same trigger, opposite treatment of the original |
| `--key` update-in-place | Nothing | Rejected below |

### 1.3 The Hall9k design

Drafted in full as `backlog/16-learnings-loop.md`. The three decisions worth arguing here:

**RECOMMENDATION:** one Marten stream per lesson, with `LearningRecorded`, `LearningRetired`,
and `LearningSuperseded`, projected into a `LearningListItem` that AgentPromptBuilder queries by
scope plus status.

**AGAINST:** a stream per one-sentence lesson is heavy. The obvious cheaper design is a growing
tail of `LearningRecorded` events on the Project stream, which is where IDEA-learnings-loop
originally put them, or plain Marten documents, since a lesson has no interesting lifecycle
worth event-sourcing. Every lesson stream is a row in `mt_streams` and a replay for anything
that touches it, and a project with 300 lessons over two years is 300 streams standing in for
what a single table would hold.

**AGAINST SURVIVES IF:** lessons turn out to be write-once and read-in-bulk with no per-lesson
history worth keeping. If nothing ever amends, retires with a reason, supersedes, or cites a
lesson individually, the stream is ceremony and a document is correct.

**HOLD:** stream per lesson. Three of those "ifs" are already false in the design: retirement
carries a required reason, distillation supersedes specific sources by id, and the injected
prompt line prefixes each lesson with its short id precisely so an agent can cite or retire one.
Those are per-lesson facts accumulating over time, which is what a stream is for. The decisive
argument against the Project-stream variant is separate and stronger: Project is a permanent
configuration aggregate with two events, and hanging an unbounded knowledge log on it makes
every project read replay every lesson ever recorded, for the benefit of a query that wants
"active lessons for project X" and nothing else.

---

**RECOMMENDATION:** distillation runs as an ordinary Research task through the existing
add, publish, assign, claim, dispatch pipeline. `h9k learn distill` is sugar that authors the
task; the daemon does not learn a new job.

**AGAINST:** the pipeline is built for tasks that produce a diff. A distillation cuts a worktree
it barely uses, runs verification gates over an unchanged tree, and reaches a PullRequestOpener
that has nothing to open. A small daemon background service, in the same family as
`PullRequestMonitor`, would be perhaps a hundred lines and would carry none of that baggage.

**AGAINST SURVIVES IF:** the Research path does not in fact terminate cleanly without commits.
Decision #28(d) exempts Research tasks from the zero-commit failure, which shows the pipeline
already contemplates a run whose product is a transcript, but nothing observed says what
PullRequestOpener does with such a run. If a Research task cannot exit the pipeline without a
PR today, then either that is a bug to fix first or the background service wins.

**HOLD:** the task pipeline, with that question named as a pre-build check in the task file
rather than assumed away. The reason is the standing pipeline-reuse rule that backlog 07 states
outright ("do not invent parallel mechanisms") and that decision #20 chose over a bespoke
follow-up claim path for exactly this trade. The pipeline also brings things the background
service would have to rebuild: the run stream, `TokensRecorded`, the transcript artifact, the
lease, and the capacity cap. There is a real bonus too. A distiller running in a worktree can
read the code, which means it can retire a lesson the codebase no longer supports rather than
merely rewording it.

---

**RECOMMENDATION:** decline `bd remember --key` and its update-in-place semantics, and decline
the bare-key-recalls behaviour.

This one needs no counterargument block because it is style, not architecture, but the reasons
are worth recording. Update-in-place is mutation, which an event-sourced store should not offer
as its primary write. Keys are a naming burden placed on the least reliable namer in the system,
a headless agent mid-task. And a write command that becomes a read command based on whether its
argument collides with an existing key will eventually swallow a lesson silently, which is the
worst possible failure mode for a memory system: it looks like it worked.

Our substitutes: identity is the UUIDv7 the platform mints, "update" is retire plus record or
supersede, and `h9k learn "<text>"` always writes.

---

## 2. Dependency graph nuances

### 2.1 The comparison

| beads | Blocking | Backlog 05 has it | Verdict |
|---|---|---|---|
| `blocks` | Yes | `BlockedBy` | Same idea, ours completes on true closeout (`RunCompleted`) rather than on a status field |
| `parent-child` (epics) | Yes, child blocked when parent blocked | No | Wait for the funnel (roadmap #2) |
| `waits-for` (all children of X) | Yes | No | Depends on parent-child; same wait |
| `conditional-blocks` (runs only if X fails) | Yes | No | **Do not copy** |
| `related` / `tracks` / `caused-by` / `validates` | No | No | **Do not copy** |
| `discovered-from` | No | No | **Adopt**, as a follow-up task |
| `supersedes` (auto-closes the old issue) | No | No | Adopt the shape for lessons only, not for tasks |
| `duplicates` (auto-closes the duplicate) | No | No | **Do not copy** |
| `replies-to` | No | No | See section 3 |
| Gates as blocking nodes (`gh:pr`, `gh:run`, `timer`, `human`, `bead`) | Yes | No | Mostly already subsumed; see 2.3 |
| Hash IDs with hierarchical children (`bd-a3f8.1`) | n/a | UUIDv7 | **Do not copy** |
| Cycle rejection at write time | n/a | At publish only | Keep ours |
| Priority 0 to 4, `bd ready --sort` | n/a | FIFO by `AddedAt` | Not yet, and 05 explains why |
| `bd defer --until` | Removes from ready | No | Not yet |
| `bd ready --claim` (agent self-claim) | n/a | Daemon claims | **Do not copy**, emphatically |
| `bd blocked --explain`, `bd dep tree`, `bd graph` | n/a | Partial | **Adopt the `--explain` idea**, cheaply |
| `bd merge-slot` (exclusive-access primitive) | n/a | Per-repo git mutex | We have one and did not name it |
| `bd stale --days` | n/a | Per-run stall detection | Small gap, low value |
| External deps (`external:backend:api-ready`) | Yes | No | Multi-repo, much later |

### 2.2 What earns a place

**`discovered-from` is the strongest single steal in the tool.** Today a Hall9k agent that finds
real work outside its task's scope has no move. It can ask a question and stall, mention it in a
transcript nobody reads, or quietly do it and blow the scope. PLAN.md has a backward refinement
loop and section 3.1's funnel, but no provenance edge saying "this task was born from that run."
A `--discovered-from <run-id>` on task creation would give us: a task an agent can file without
stalling, an audit edge from the finding back to the run that found it, and a natural feed into
triage. It is also the honest counterpart to the learnings loop. Some discoveries are lessons and
belong in `h9k learn`; some are work and belong in the funnel, and right now both fall on the
floor. Proposed as its own follow-up rather than folded into 05, which is already large and
flagged run-alone.

**`--explain` on blocked work.** Backlog 05 already requires that a blocked task's unmet
dependencies are visible in `h9k status`. Beads' framing adds one thing worth having: the answer
to "why is this not running" should be a first-class output, not a field the reader assembles.
Cheap enough to fold into 05's existing criterion rather than a separate task.

**Naming the merge slot.** Decision #4 gives the daemon a per-repo git mutex, and
IDEA-coordinator-agent proposes serializing tasks whose file footprints collide. Those are the
same primitive at two scales, and beads calls the general form a merge slot. Nothing to build
today; the value is that when the coordinator agent arrives, its "run these alone" output has a
name and an existing mechanism to generalize rather than a new invention.

**A layered graph view, eventually.** `bd swarm validate` computes dependency layers and reports
"parallelism potential". That is precisely the substrate IDEA-coordinator-agent needs as input:
before a coordinator can add edges, someone has to be able to see the graph it is editing. An
`h9k task graph` producing the layer view is a small, useful precursor to that idea.

### 2.3 What we should deliberately not copy, and why

1. **Hierarchical IDs (`bd-a3f8.1`).** They encode structure inside identity, so re-parenting a
   task either renames it or makes the id lie. Beads needs content-hashed ids because it has no
   central id authority and must survive concurrent creation across branches. We solved that
   problem differently in decision #14: UUIDv7 gives global uniqueness plus chronological
   ordering plus index locality, with structure kept where it belongs, in explicit edges. The one
   thing we give up is readable parentage at a glance, and the 8-hex prefix convention already
   used throughout the decisions log covers readability well enough.

2. **`conditional-blocks`, meaning "run B only if A fails".** This is a workflow engine
   primitive, and it is automated compensation on failure. Decision #11 says the daemon never
   acts on its own judgment, decision #27 makes Failed a human-decision waypoint with three
   explicit human-only exits, and decision #25 keeps retry human-only on the grounds that "a
   failure that repeats without human eyes" is exactly the rule being protected. A graph edge
   that dispatches work because something failed reverses all three at once.

3. **`duplicates` and `supersedes` with auto-close.** Both close an issue as a side effect of
   adding a link. Hall9k's whole lifecycle discipline is that every state change is an explicit
   act through `TaskDecider` with a guard, and backlog 05 doubles down on it (unassign, draft,
   publish, and assign are each their own deliberate step). Deduplicating is already expressible
   honestly today: abandon the duplicate with a reason naming the canonical task. That produces
   the same outcome with a real event and a real reason, which is what `h9k task show` will need
   to explain six months later.

4. **`related`, `tracks`, `caused-by`, `validates`.** Non-blocking annotation edges that no
   consumer reads. PLAN.md section 4 already covers the actual need with context pointers, and
   section 3.1a shows the shape working for sibling awareness on adopted epics: point at the
   thing, let the agent fetch it live, no import and no staleness. Adding graph edges that only
   render in a graph view is data we would have to maintain and nothing would consult.

5. **`bd ready --claim`, meaning agents claim their own next work.** This is the deepest
   architectural difference between the two systems and the one to be most careful about.
   Beads is a coordination surface that agents pull from; Hall9k is an orchestrator that pushes.
   Self-claiming agents would bypass the per-node concurrency cap (decision #19), the lease
   fencing token (#7), single-flight per task per node (#29c), and the accountability chain in
   section 6.2 that says every run traces to a node and therefore to a human. Backlog 05 goes
   further still by making human assignment the dispatch trigger. Agent self-service claiming is
   not a feature we lack; it is a property we deliberately refused.

6. **Write-time cycle rejection.** Beads rejects cycles when the edge is added. Backlog 05
   checks at publish only, on the stated reasoning that "drafts may transiently hold cycles while
   a graph is being authored" and that a cycle can never become assignable. That is the better
   rule for a system where a human or a coordinator agent authors a graph in pieces. Keep it, and
   keep the self-correcting error that names the cycle.

7. **Priority levels.** Beads sorts ready work by a 0 to 4 priority. Backlog 05 says dispatch
   order is FIFO by `AddedAt` among Queued tasks. That looks like a gap and is not, because 05
   moved priority somewhere better: assignment is the dispatch trigger and is always an explicit
   human act, so the human expresses priority by choosing what to assign and when. A priority
   field would be a second, weaker channel for the same intent, and the two would drift. Revisit
   only if a single owner routinely holds more assigned-and-ready tasks than capacity, which is
   not the current shape of the work.

8. **`bd defer --until`.** Deferral removes an issue from ready until a date. In 05's lifecycle
   a Published but unassigned task already is a deferred task, with the added honesty that a
   human has to come back and decide rather than a clock deciding for them. The only case not
   covered is an already-assigned task that should wait for a date, which invites a scheduler.
   Not now.

9. **`bd admin cleanup` and anything that deletes closed work.** Covered in section 4.

### 2.4 The gates question, which turns out to be a compliment to 05

Beads models external waits as first-class blocking nodes: a `gh:pr` gate closes when a PR
merges, `gh:run` when CI completes, `timer` after a duration, `human` on manual resolution, all
resolved by a polled `bd gate check` that the docs suggest running from cron.

Read against Hall9k, three of the five are already ours and better placed:

- `gh:pr` and `gh:run` are the closeout monitor (decisions #18 and #22). Ours polls on a gentle
  interval too, but it does considerably more than close a gate: it dispatches follow-up runs on
  failing checks and unresolved review threads, bounds those retries, parks with a reason, and
  cleans up worktrees and branches on the observed merge. Backlog 05 then defines dependency
  completion as `RunCompleted` from that monitor, which is the same edge beads draws with a
  `gh:pr` gate, minus a node to maintain.
- `human` is assignment. A task nobody has assigned is a task waiting on a human decision, which
  is the entire content of a human gate.
- `timer` we genuinely do not have, and genuinely do not want yet. See point 8 above.

The one thing gates offer that we lack is uniformity: in beads, "waiting on a person" and
"waiting on CI" and "waiting on another task" are all the same shape, so one query answers "what
is this waiting for". We express the same three states in three different places (assignment,
run state, `BlockedBy`). That is the real content of the `--explain` recommendation in 2.2: not a
new node type, but one query that composes the three honestly.

---

## 3. The messaging layer

### 3.1 What beads actually has, which is less than the README suggests

The README describes a message issue type with threading via `--thread`, an ephemeral lifecycle,
and mail delegation between agents. The documentation does not corroborate the first two:

- `bd types` documents the valid types as bug, task, feature, chore, epic, and decision, plus
  custom types configured in `.beads/config.yaml`. No message type appears.
- The full flag list for `bd create` includes `--ephemeral` and `--wisp-type` but no `--thread`,
  no `--reply-to`, and no message type under `-t`.
- `bd mail` is documented as a **delegate**: it "delegates mail operations to an external mail
  provider", configured through `BEADS_MAIL_DELEGATE` or `mail.delegate`, with `gt mail` as the
  worked example. Beads proxies; something else stores and delivers.
- What is real and documented is `replies-to`, one of four non-blocking graph links: "Creates
  message threads, similar to email or chat conversations. Automatically established when
  replying to orchestrator mail."
- The multi-agent coordination page is blunt about the rest: "No registry exists ... Inter-agent
  messaging occurs through comments and labels. Both serve as pull-based signaling rather than
  push notifications."

Honest reading: beads has thread-shaped links and a proxy to somebody else's mailbox. The mailbox
itself lives in the sibling orchestrator, not in beads. A custom type plus `--thread` may well
exist in the shipped binary and simply be undocumented on the pages read; that could not be
determined without running it, and nothing below depends on the answer.

### 3.2 Recommendation

**RECOMMENDATION:** Hall9k does not build agent-to-agent messaging. No mailbox, no inbox, no
agent addressing, now or in the coordinator-agent future.

The load-bearing reason is not complexity, it is that **a mailbox requires an addressee and
Hall9k deliberately has none.** Decision #2 and PLAN.md section 6.6 settled this: agents have no
identities, only personas, which are prompt templates plus tool policy plus verification profile
applied to a session. There is nothing to address a message to. Introducing an address would
reopen the identity decision as a side effect of a messaging feature, which is the wrong order.

Three supporting reasons:

- Our agents are detached, short-lived, single-purpose processes. A message is only useful to a
  running recipient, and ours run for minutes and exit. A mailbox for a process that does not
  exist yet is a task with extra steps and worse guarantees.
- The two real needs it would serve are covered. Handoff is the task graph plus follow-up runs.
  "I learned something the next agent needs" is `h9k learn`, and "I found work someone should
  do" is the `discovered-from` task creation proposed in 2.2.
- IDEA-coordinator-agent already constrains itself to exactly this line: "the coordinator's
  product is edges on the graph, nothing else", so that "the dumb dispatcher then executes the
  sequenced graph". Giving the coordinator a message channel gives it a second output that the
  dispatcher does not read and a human cannot audit as a graph. The constraint is what makes the
  idea safe, and messaging is the thing that erodes it.

**AGAINST (the strongest case, and it is a real one):** we have already built agent-to-agent
messaging three times and called it something else. The pre-PR review loop passes the reviewer's
findings to a fix session as its prompt (#24). The closeout monitor passes review threads and
failing checks to a follow-up run (#22). The ask/answer loop passes a human's answer into a
resumed session (#5). Each is a typed, durable, point-to-point message from one run to another,
each has its own event, its own prompt template, and its own artifact convention, and each was
built separately. Refusing "messaging" while operating three bespoke message channels is a
position about vocabulary, not architecture. A general primitive would make the fourth handoff
cheap instead of bespoke, and there will be a fourth: a coordinator explaining why it serialized
two tasks, or a discovery session clarifying a requirement to an implementer.

**AGAINST SURVIVES IF:** a fourth and fifth handoff arrive and each one again costs a new event
type, a new prompt template, and a new artifact convention. That is the signal that the shape
is real and unextracted.

**HOLD:** no mailbox. But the counterargument is right about the duplication, so the response
when it triggers is extraction, not addition. Extract a run-scoped `Handoff` from the three that
exist, with these properties, which are what keep it from becoming a mailbox:

- The sender is a **run** and the recipient is a **run**, never an agent. Runs have ids, nodes,
  owners, and lifetimes; agents have none of those by design.
- Delivery is prompt injection at dispatch, not a poll. Nothing is ever waiting on a message.
- A handoff cannot outlive its task. There is no queue, so there is nothing to accumulate.
- Every handoff lands on the run stream as a milestone with its body as an artifact, per
  decision #6, so the human can read what one agent told the next.

Two things worth recording as adjacent and unresolved. First, the gap we actually have is not
agent-to-agent but **human-to-running-agent**: today the only way to tell a working agent
something is to answer a question it thought to ask. Decision #5's exit-and-resume design makes
unsolicited mid-run injection genuinely hard, and it should be treated as its own question rather
than smuggled in under messaging. Second, `bd merge-slot` shows that some coordination needs no
messages at all, only a shared exclusive resource. When the coordinator agent arrives, prefer
that shape.

---

## 4. Compaction as a general principle

Section 0 states the rule. This section applies it to the four places where our records or views
are growing.

### 4.1 Run transcripts and IDEA-artifact-retention

IDEA-artifact-retention already contains the right idea, listed as a "keep-the-distillate option
worth discussing". Beads' experience argues for promoting it from option to rule, with one
change of timing:

**RECOMMENDATION:** a run's distillate (final result summary, decisions made, open questions,
tokens, cost) is written at **run completion**, onto the run stream, and artifact purge is
permitted only for runs that have one.

**AGAINST:** writing a distillate at completion spends effort on every run, including the
roughly ninety percent nobody will ever revisit, and PLAN.md section 4 promises the summary
anyway, so this may be work the run already does. Summarizing lazily at purge time spends the
effort only on what survives ninety days, and by then we know which runs mattered.

**AGAINST SURVIVES IF:** the distillate is expensive to produce. If it needs a summarization
session, doing it 90 days later for a fraction of runs is obviously cheaper.

**HOLD:** at completion. The distillate is not expensive, because the pieces are already in hand
at that moment: the stream's final `result` event carries the summary and the token counts, and
decision #2 already makes that event the completion signal, so the daemon is reading it anyway.
Lazy summarization at purge time has a worse failure mode than cost: at purge time the transcript
is the only source, so a failed or skipped summarization silently converts into permanent loss,
and it would be running against a 90-day-old transcript for a task nobody is thinking about. Do
the cheap thing while the evidence is warm. The purge then becomes genuinely lossless for
everything anyone consults, and `RunArtifactsPurged` records it honestly.

Corollary from the events journal: `h9k logs` on a purged run should return the distillate plus a
typed, specific statement of what is gone, in the shape beads uses for a truncated journal.
"Transcript purged 2026-11-14 per 90-day retention policy; the run summary and token counts are
below" is honest. A file-not-found is a lie by omission, and IDEA-artifact-retention already flags
this. Beads' contribution is the detail that the notice should name the boundary, not just the
fact.

### 4.2 Closed task and run streams

**Do not compact these, ever.** This is where the study most firmly parts company with its
subject. Beads compacts closed issues because Dolt holds full issue bodies and its history is its
storage. Our streams are already the compacted form: decision #6 settled that transcripts are
artifacts and streams carry milestones only, so a completed task's entire stream is on the order
of a dozen small events. They are also the accountability chain that section 6.2 requires be
queryable (this PR came from this run on this node belonging to this human), and an accountability
chain with a summarized middle is not one.

The volume problem people expect here is real but lives one layer up, in **projections and
queries**, not in streams. `h9k status` across five thousand terminal tasks is a filter problem
with a well-understood answer, and Postgres is not going to notice the streams.

### 4.3 Status and other rendered surfaces

This is where decay genuinely belongs and where we have not applied it. Every surface that
renders an unbounded set should have a budget and an honest truncation notice:

- `h9k status` should collapse aged terminal work into counts rather than rows. "142 completed in
  the last 90 days" is more useful than 142 lines, and it is compaction of the view, which is
  allowed.
- The injected lessons section, budgeted and announced, per `16-learnings-loop.md`.
- The agent prompt as a whole. Lessons, context links, sibling references, and follow-up findings
  all flow into it from different places, and nothing today owns its total size. A per-run prompt
  budget with a single honest "showing N of M" convention is the general form of the lessons
  criterion, and is probably where that convention should eventually live.
- `h9k task show` for a task with a dozen runs, and `h9k logs` across review cycles.

### 4.4 The rule to record

If any of this lands, the decisions log entry is one line plus its origin, and the origin is this
study rather than an incident:

> Compaction applies to views, never to records. Injected, rendered, and shipped surfaces get a
> budget and state what they withheld; event streams are append-only and are never summarized in
> place. On-disk artifacts are the sole purgeable record, and only behind an existing distillate
> and a recorded purge event. Adopted 2026-08-19 from the beads study, whose semantic compaction
> discards original content permanently ("permanent graceful decay") because in beads the store
> is the view. Hall9k separates the two and should keep them separate.

---

## 5. Side finding: beads as a lens on IDEA-p2p-lazy-sync

Not asked for directly, but IDEA-learnings-loop raises it and the docs answer it clearly.

Beads' multi-machine story is **Dolt remotes carried on the git remote**. Issue history lives
under `refs/dolt/data`, separate from `refs/heads/*`, so the same GitHub remote holds both the
code and the versioned issue database. `bd init` auto-detects the git origin, a new clone runs
`bd bootstrap`, and thereafter `bd dolt push` and `bd dolt pull` move the whole database. The
`.beads/issues.jsonl` export is explicitly interchange only, and the docs warn that importing it
is not a substitute for pulling, because import cannot observe deletions.

Two things follow for us.

The clever part is worth remembering: **needing no new infrastructure for multi-machine sync**,
because the git remote you already have can carry a second ref namespace. If shared Postgres
proves awkward at roadmap #5 (open decision #11), the analogous Hall9k move is shipping the event
streams as an append-only export to a ref on the existing origin, which is a genuinely simpler
option than either exotic P2P or a hosted store.

The cautionary part is stronger. Replicating everything and merging is what forces beads into
content-hashed ids to avoid collisions, and its documentation carries a whole recovery section
including a dedicated merge conflicts playbook, a circular dependencies playbook, and a database
corruption playbook. That is the visible cost of a merge-based model. IDEA-p2p-lazy-sync's
single-writer-by-lease design avoids the entire category by construction, and decision #7's
fencing token is already the enforcement mechanism. The beads docs are, read this way, the best
available argument for the choice that idea already made.

---

## 6. What this study changes

Concrete follow-ups, each small enough to be a task, in the order they earn their place:

1. **`backlog/16-learnings-loop.md` (drafted, ready to review).** The memory trio built the
   Hall9k way: `h9k learn`, prompt injection with a budget and an honest truncation notice, and
   distillation that supersedes rather than discards.
2. **Correct IDEA-learnings-loop.** Its prior-art section credits beads with keeping full history
   while compacting the view. Beads discards. One-paragraph fix so the file stops asserting an
   unobserved fact about somebody else's tool, which is the never-guess rule applied to our own
   notes.
3. **`--discovered-from` on task creation.** The strongest steal in section 2. An agent that
   finds out-of-scope work can file it with an audit edge back to the run that found it, instead
   of stalling, ignoring it, or silently expanding scope. Own task; do not fold into 05.
4. **Fold `--explain` into backlog 05.** One query that answers "why is this not running"
   across all three of its causes: unassigned, blocked by an unmet dependency, or waiting on a
   run. Extends an existing criterion rather than adding a task.
5. **Promote the run distillate in IDEA-artifact-retention.** Written at completion, onto the
   run stream; purge permitted only behind it; `h9k logs` reports the purge with its boundary.
6. **Record the compaction rule** in PLAN.md section 16 when something above ships, in the form
   given in 4.4.
7. **Later, and only in this order:** an `h9k task graph` layer view as the substrate the
   coordinator agent will edit, and the `Handoff` extraction from section 3.2, but only when a
   fourth bespoke run-to-run message appears.

Explicitly not adopted, recorded so the question does not get reopened by accident: hierarchical
ids, conditional-blocks, duplicate and supersede links with auto-close, annotation-only graph
edges, agent self-claiming, write-time cycle rejection, priority levels, timed deferral,
deletion of closed work, and any agent mailbox.

---

## 7. Sources, and what could not be determined

Read on 2026-08-19: the project README, and the documentation pages for `bd remember`, `bd
recall`, `bd memories`, `bd forget`, `bd prime`, `bd compact`, `bd admin`, `bd create`, `bd
ready`, `bd epic`, `bd defer`, `bd stale`, `bd mail`, `bd ping`, `bd swarm`, `bd context`,
Issues and Dependencies, Dependencies and Gates, Graph Links, Hash-based IDs, Sync Concepts,
Molecules, Gates, Agent Coordination, Claude Code integration, and the Events Journal reference.

The tool was not installed or run, and no source was read.

Could not be determined from the documentation, and therefore not relied on anywhere above:

- Whether beads memories are a separate table or an issue type, what scopes them, whether any
  size or count limit applies, and whether anything ranks them for injection.
- The literal text `bd prime` emits, and whether its memory section is bounded or truncated.
- What a compacted issue summary looks like. No example appears on the pages read.
- Whether semantic compaction ever triggers automatically at a threshold. Everything documented
  is an operator-run command with dry-run and force guards.
- Whether the message issue type and `--thread` exist. The README says yes; `bd types` and the
  `bd create` flag list do not show them, and `bd mail` delegates to an external provider.
- Bucket federation and multi-repo routing, which have documentation pages that were not read and
  may be relevant when open decision #11 is taken up at roadmap #5.
