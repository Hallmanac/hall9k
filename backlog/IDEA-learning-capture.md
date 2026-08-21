# IDEA: Learning capture and consolidation

**Status:** Draft, funnel entry, not a decision
**Origin:** Beads comparison session, 2026-08-20 (`bd remember` / `bd prime`)
**Builds on:** Decision #34 (task lifecycle split). Independent of it, but adopts its conventions
**Would become:** the next free decisions-log entry (#35 and #36 were claimed by the ideas and context-routing merges, 2026-08-20)

## The gap

`.claude/skills/` is durable, curated, version-controlled project knowledge, and it
beats Beads' free-append memory store on every axis that matters once knowledge is
authoritative. But there is no path for an agent to record something it learned
*mid-run*. The transcript is the only place that knowledge lands, and nobody reads
transcripts.

So a run discovers that a build step needs a particular flag on this machine, or
that this repo enforces nullable reference types more strictly than the compiler
does, and that discovery dies with the session. The next agent rediscovers it,
burns the tokens again, and dies with it again.

## The shape

A new aggregate, `Learning`, with its own stream, its own decider, and its own CLI
verbs, following the shape `Task` already has after #34.

```
h9k learn "<statement>" --scope node|project|owner [--project <p>]
```

Task, run, node, and owner metadata are attached at record time rather than typed
by the caller, and `LearningRecorded` is appended. One path, two callers: a human
records a learning with the same command an agent uses, exactly as with every other
verb.

**A recorded learning is live immediately.** It reaches the priming context of the
next run at its scope, with no gate and no approval step.

### Why there is no quarantine

An earlier draft of this file made recording inert and required corroboration before
a learning could reach an agent, on the grounds that this parallels #34: `TaskAdded`
means "this exists," not "dispatch this," so `LearningRecorded` should mean "an agent
believed this once," not "this is true."

The parallel is elegant and it does not survive contact with the cost model.

A task dispatched before it was ready costs an agent run, a worktree, and a branch. A
learning primed before it was corroborated costs one line of a prompt. Those are
different by orders of magnitude, and the same gate is not warranted for both.

The decisive objection is sharper. The proposed evidence for admitting a learning was
frequency across independent runs. But a second independent sighting only happens
because a second agent rediscovered the same thing the hard way, which is precisely
the waste this feature exists to prevent. **The gate's admission criterion requires
the cost it was built to avoid.** At solo-user volume many true observations would
never reach a third sighting at all, so the most useful case, a rare and expensive
lesson learned once, is the case the gate would silently discard.

Noise protection comes from being able to retire a bad learning cheaply, not from
holding good ones back. See "Staleness" below.

### Scope determines travel

Scope is not a filing convenience. It decides whether a learning crosses a node
boundary at all.

| Scope | Example | Travels |
| --- | --- | --- |
| `node` | "this machine cannot run the native bash step" | Never. Meaningless elsewhere. |
| `project` | "this repo enforces nullable; do not suppress" | With the project. Wants to become a skill or an AGENTS.md entry. |
| `owner` | Brian's personal style preferences | To every node and project he owns. |

This is also the P2P replication rule, unchanged: `node`-scoped streams stay local,
`project` and `owner` replicate.

Note the scope vocabulary lines up with `AssignedOwnerId` from #34: an `owner`-scoped
learning follows the same identity the claim guard already uses.

Scope is cheap to get wrong and cheap to fix, and the failure is asymmetric. Too
narrow means one other project misses something useful. Too broad means a wrong or
irrelevant statement rides in every prompt on every project. So `project` is the
default and widening to `owner` is the deliberate act, matching how #34 treats
assignment.

## Consolidation

The word is deliberate. Consolidation is not a quality gate and grants nothing that
recording did not already grant. It is **compression**: several statements saying
roughly the same thing collapse into one that says it better, and the constituents
leave the injected view.

A periodic pass, an agent dispatched through the normal lifecycle like anything else,
reads unabsorbed learnings and looks for clusters.

The learnings will not match word for word. They will be semantically close and
differently phrased, which is what an agent is good at and what a `GROUP BY` is not.
The agent clusters them and proposes a synthesis: one statement covering what the
cluster is collectively saying.

The pass **proposes**; it does not decide. It surfaces "these four are saying one
thing, here is that thing" and a human approves. House rule, unchanged: never loop on
judgment without a human. This is the same shape as `h9k review resolve` from #28, an
agent producing a verdict-shaped artifact that a human converts into a fact.

On approval:

- **`LearningConsolidated`** carries the synthesis and the IDs of every learning it
  absorbed. Origin trail, applying the principle used everywhere else. If the
  consolidated statement turns out to be wrong, the trail says whether the synthesis
  was bad or the underlying observations were.
- **`LearningAbsorbed`** is appended against each constituent, carrying the
  consolidation ID. The pointer runs the opposite way from the consolidation's source
  list, and it is what makes the pass idempotent: the next run asks for unabsorbed
  learnings and gets a clean working set instead of re-clustering the same four.

  Strictly this is derivable from the consolidations, so it is a read-model
  convenience rather than a correctness requirement. Decide which side of that line to
  do the work on, but note `TaskDependencyResolver` already chose "compute it in the
  daemon rather than store a counter," so there is precedent for either.

Nothing is deleted at any point. Absorbed learnings stay queryable with their full
provenance; they simply stop being injected, because the consolidation now speaks for
them.

### Reinforcement vs. refinement

A new learning matching something already consolidated is information, not noise. It
is another independent sighting, and discarding it throws away confidence.

- **Reinforcement.** The new learning says the same thing. It absorbs into the
  existing consolidation, whose evidence count grows.
- **Refinement.** The new learning adds an edge case the consolidation does not
  cover. The consolidation is revised, which in event terms is a new version
  superseding the old.

Telling those apart is a judgment call, so the pass flags it rather than deciding.

## Staleness

Consolidation solves volume. It does nothing about wrongness: four wrong learnings
consolidate into one confident wrong learning. Staleness needs its own answer.

### Two signals that look useful and are not

**Age.** Old is not false. A rule about force-pushing rebased branches will be true
in five years. Age carries no evidence about truth and must never be a retirement
trigger on its own.

**Non-reinforcement**, meaning "nothing has re-observed this in fifty runs, so it is
probably dead." This one is actively harmful, and the reason is the whole point:

> **A learning that works suppresses its own evidence.** Once "always force-with-lease
> on rebased branches" is going into every prompt, no agent hits that failure again,
> so no agent re-learns it, so it is never reinforced.

Ranking by recent corroboration retires exactly the learnings doing the most work.
Anything built here must not use silence as evidence of death.

### The signal that does work: contradiction

A new learning that disagrees with an existing one is real evidence, because an agent
in the field just observed the opposite of what the platform told it.

It is also nearly free, because it is the same operation the consolidation pass
already performs. Detecting "these four agree" and detecting "this one contradicts
that one" are one clustering problem read two ways. So the pass gains a second output
alongside its cluster proposals: **conflicts**, each presented as two statements with
their full provenance, for a human to settle.

The loop closes in the field rather than on a schedule. Every injected learning
carries its short ID in the prompt, so an agent burned by a learning that is now
wrong can file a contradicting learning naming that ID, at the moment the evidence
exists. Nothing has to go looking for staleness; the pass collects what was reported.

### Verification against the code: deferred, deliberately

The pass runs in a worktree, so in principle it could check claims against the
repository. "This repo enforces nullable" is checkable against the csproj.

Decided (Brian, 2026-08-20): **not in the first build.** Verifying every learning
against the codebase turns a clustering pass into a much longer and more expensive
run, and it only helps for falsifiable claims. "Brian prefers ternaries split across
lines" is true or false about a person, and no amount of reading the repo settles it.
Contradiction detection ships first; verification earns its way in once there is a
real pile to look at.

### The healthiest death is graduation

Most learnings should not end by being wrong. They should end by being **enforced
somewhere stronger**: an `AGENTS.md` rule, a verification gate, a repo skill. Still
true, now guaranteed rather than suggested, so injecting it is redundant.

This is the funnel the original `IDEA-learnings-loop` flagged as an open question
("lessons as a funnel INTO harder enforcement"), and it is the intended end state for
anything that consolidates with strong evidence behind it.

### Retraction

If a learning proves wrong, append a superseding event rather than editing a markdown
file. History then shows what was believed and when it stopped being believed, which
is what you actually need when debugging why an agent went sideways three weeks ago.

## What priming reads

Unabsorbed learnings plus consolidations, at node, project, and owner scope for the
claiming node. Never absorbed constituents, and never full history.

This is Beads' compaction reframed. They shrink to save context; this consolidates to
raise signal, and shrinking is a byproduct. The difference that matters: their
semantic compaction discards the original content permanently, and nothing here
discards anything.

## Draft acceptance criteria

- [ ] `h9k learn "<statement>" --scope node|project|owner` appends `LearningRecorded` with task, run, node, and owner metadata attached at record time; a learning requires only a statement and a scope, and `project` is the default scope
- [ ] Provenance is observed, never inferred: a learning recorded inside a run carries that run and task; one recorded by a human from a shell carries explicit nulls for both, per the never-guess rule
- [ ] A recorded learning reaches the next run's priming context at its scope immediately, with no approval step
- [ ] Each injected learning carries its short ID, so an agent can cite, contradict, or retract it
- [ ] `h9k learn list` filters by `--scope`, `--project`, and `--state recorded|absorbed|consolidated|retracted`
- [ ] `h9k learn review` dispatches an agent that clusters unabsorbed learnings at a given scope and writes a proposal artifact naming each cluster's members and a proposed synthesis
- [ ] The same pass reports **conflicts**: pairs of learnings that contradict each other, each with its provenance, for a human to settle
- [ ] The pass proposes only; it never appends `LearningConsolidated` itself, and it never retracts anything
- [ ] `h9k learn consolidate <cluster>` is the human act: it appends `LearningConsolidated` carrying the synthesis and the IDs of every absorbed learning, plus `LearningAbsorbed` against each constituent carrying the consolidation ID
- [ ] A learning carrying `LearningAbsorbed` is excluded from the next pass's working set and from priming, and remains queryable with full provenance
- [ ] A new learning matching an existing consolidation absorbs into it rather than starting a new cluster; the pass flags rather than decides when the match adds an uncovered edge case
- [ ] `h9k learn retract <id> --reason` appends a superseding event; history shows both the original belief and its retraction
- [ ] Nothing in the loop deletes, and no path retires a learning on age or on absence of reinforcement
- [ ] `node`-scoped learnings are excluded from replication; `project` and `owner` scopes replicate
- [ ] Every new CLI verb has teaching `--help` and self-correcting errors, matching the #34 command surface
- [ ] PLAN.md gains Decisions Log #35 and a `LEARNING-MODEL.md` (or TASK-MODEL.md section) records the state split, event catalogue, scope semantics, and the staleness rules
- [ ] `dotnet build` and `dotnet test` pass

## Open questions

- Where do consolidated `project`-scoped learnings physically land: a generated skill
  file in `.claude/skills/`, an AGENTS.md section, or a projection the prime step
  reads directly? A generated file is reviewable in a PR, which fits the rest of the
  model, and makes graduation visible in git history rather than only in the ledger.
- Is there an injection budget, and what happens when it binds? If the injected set is
  ever truncated, the prompt should say what it withheld rather than cutting silently.
- What triggers the pass: cadence, unabsorbed count, or manual only. Manual is the
  safe default, consistent with `h9k pr resolve` and `h9k task retry`, where the human
  asking is the grant.
- Does the clustering agent see each learning's origin incident, or only the statement?
  A rule stripped of its scar looks arbitrary, and an agent that cannot see why a rule
  exists is the reader most likely to propose dropping it.
- Retirement path for singletons that will never recur. They stay in the working set
  forever otherwise. Cheap, but unbounded.

## Comparison note

Beads' `bd remember` writes straight into the memory store and `bd prime` picks it up
on the next run. One step, no gate, immediately live. This design keeps that speed
deliberately, and differs from Beads in what happens afterward: their semantic
compaction summarizes old material and discards the original ("permanent graceful
decay"), while consolidation here supersedes without deleting, so the injected view
shrinks and the ledger does not. Their `bd forget` removes; `h9k learn retract`
records that a belief ended.
