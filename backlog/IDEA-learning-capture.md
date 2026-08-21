# IDEA: Learning capture and promotion

**Status:** Draft — funnel entry, not a decision
**Origin:** Beads comparison session, 2026-08-20 (`bd remember` / `bd prime`)
**Builds on:** Decision #34 (task lifecycle split) — independent of it, but adopts its conventions
**Would become:** Decisions Log #35

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

A new aggregate — `Learning` — with its own stream, its own decider, and its own
CLI verbs, following the shape `Task` already has after #34.

```
h9k learn "<statement>" --scope node|project|owner [--project <p>]
```

The daemon attaches task, run, node, and owner metadata and appends
`LearningRecorded`. One path, two callers: a human records a learning with the same
command an agent uses, exactly as with every other verb.

A recorded learning is **inert**. It is queryable, it is in the ledger, and it
reaches no agent's priming context.

This is the same move #34 made for tasks. `TaskAdded` no longer means "dispatch
this"; it means "this exists." `LearningRecorded` means "an agent believed this
once," not "this is true." Recording is identity; promotion is the readiness gate.
The vocabulary is deliberately parallel, and the guards should be too.

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

Note the scope vocabulary lines up with `AssignedOwnerId` from #34 — an
`owner`-scoped learning follows the same identity the claim guard already uses.

## Promotion

A periodic pass — an agent, dispatched through the normal lifecycle like anything
else — reads unabsorbed learnings and looks for corroboration.

The learnings will not match word for word. They will be semantically close and
differently phrased, which is what an agent is good at and what a `GROUP BY` is
not. The agent clusters them and proposes a synthesis: one statement covering what
the cluster is collectively saying.

Frequency across *independent* runs is the evidence. Several separate sightings
means the observation survived a change of context. A singleton means nothing yet —
it sits in the ledger, harmless and queryable, and is never injected.

The pass **proposes**; it does not decide. It surfaces "these four agree, should
this become a skill?" and a human approves. House rule, unchanged: never loop on
judgment without a human. This is the same shape as `h9k review resolve` from #28 —
an agent produces a verdict-shaped artifact, a human converts it into a fact.

On approval:

- **`LearningPromoted`** — carries the synthesis and the IDs of every learning it
  absorbed. Origin trail, applying the principle used everywhere else. If the
  promoted learning turns out to be wrong, the trail says whether the synthesis was
  bad or the underlying observations were.
- **`LearningAbsorbed`** — appended against each constituent, carrying the
  promotion ID. The pointer runs the opposite way from the promotion's source list,
  and it is what makes the pass idempotent: the next run asks for unabsorbed
  learnings and gets a clean working set instead of re-clustering the same four and
  proposing a skill that already exists.

  Strictly this is derivable from the promotions, so it is a read-model convenience
  rather than a correctness requirement. Decide which side of that line to do the
  work on — but note `TaskDependencyResolver` already chose "compute it in the
  daemon rather than store a counter," so there is precedent for either.

### Reinforcement vs. refinement

A new learning matching something already promoted is information, not noise — it
is another independent sighting, and discarding it throws away confidence.

- **Reinforcement** — the new learning says the same thing. It absorbs directly
  into the existing promotion. No new skill; the promotion's evidence count grows.
- **Refinement** — the new learning adds an edge case the promotion does not cover.
  The promotion is revised, which in event terms is a new version superseding the
  old.

Telling those apart is a judgment call, so the pass flags it rather than deciding.

### Retraction

If a promoted learning proves wrong, append a superseding event rather than editing
a markdown file. History then shows what was believed and when it stopped being
believed, which is what you actually need when debugging why an agent went sideways
three weeks ago.

## What priming reads

Promoted learnings, plus unabsorbed singletons at the relevant scope. Never full
history.

This is Beads' compaction reframed: they shrink to save context, this synthesizes
to raise confidence, and shrinking is a byproduct.

## Draft acceptance criteria

- [ ] `h9k learn "<statement>" --scope node|project|owner` appends `LearningRecorded` with task, run, node, and owner metadata attached by the daemon; a learning requires only a statement and a scope
- [ ] A recorded learning is inert: it appears in `h9k learn list` and never in any run's priming context
- [ ] `h9k learn list` filters by `--scope`, `--project`, and `--state recorded|absorbed|promoted`
- [ ] A promotion pass command (`h9k learn review`) dispatches an agent that clusters unabsorbed learnings at a given scope and writes a proposal artifact naming each cluster's members and a proposed synthesis
- [ ] The pass proposes only; it never appends `LearningPromoted` itself
- [ ] `h9k learn promote <cluster>` is the human act: it appends `LearningPromoted` carrying the synthesis and the IDs of every absorbed learning, plus `LearningAbsorbed` against each constituent carrying the promotion ID
- [ ] A learning carrying `LearningAbsorbed` is excluded from the next pass's working set
- [ ] A new learning matching an existing promotion absorbs into it rather than starting a new cluster; the pass flags rather than decides when the match adds an uncovered edge case
- [ ] `h9k learn retract <id> --reason` appends a superseding event; history shows both the original belief and its retraction
- [ ] The priming step reads promoted learnings plus unabsorbed singletons at node, project, and owner scope for the claiming node — never full history
- [ ] `node`-scoped learnings are excluded from replication; `project` and `owner` scopes replicate
- [ ] Every new CLI verb has teaching `--help` and self-correcting errors, matching the #34 command surface
- [ ] PLAN.md gains Decisions Log #35 and a `LEARNING-MODEL.md` (or TASK-MODEL.md section) records the state split, event catalogue, and scope semantics
- [ ] `dotnet build` and `dotnet test` pass

## Open questions

- Where do promoted `project`-scoped learnings physically land — a generated skill
  file in `.claude/skills/`, an AGENTS.md section, or a projection the prime step
  reads directly? A generated file is reviewable in a PR, which fits the rest of the
  model, and makes promotion visible in git history rather than only in the ledger.
- Retirement path for singletons that will never recur. They stay in the working set
  forever otherwise. Cheap, but unbounded.
- What triggers the pass — cadence, unabsorbed count, or manual only.
- Does a promotion need a `Draft`/`Published` split of its own, or is
  recorded → promoted enough? Probably enough: the clustering proposal *is* the
  draft, and it lives in an artifact rather than the aggregate.

## Comparison note

Beads' `bd remember` writes straight into the memory store and `bd prime` picks it
up on the next run. One step, no gate, immediately live. Their model optimizes for
speed and accepts that some memory will be wrong. This one quarantines by default
and publishes only on corroboration — the same split #34 just made for tasks, and
the same split that runs through every other comparison with Beads.
