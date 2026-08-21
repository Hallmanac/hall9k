# SUPERSEDED (2026-08-20): the learnings-loop design now lives in IDEA-learning-capture.md,
# which arrived from Brian's beads deep-dive session far more developed (inert-by-default
# quarantine, scope-based replication, absorb/reinforce/retract). This file is retained for
# what the successor does not carry: the honesty-principle notes in section 1 (including the
# KNOWN VIOLATION in NodeBootstrap) and the origin-incident convention history.

# Idea: The learnings loop — platform-native lessons that feed future runs

Captured 2026-08-17 (inspired by Collaboard's honesty/lessons mechanics; Hall9k version is
platform data, not gitignored markdown). PLAN.md already reserves the slot: §4 "runs emit
summary, decisions, open questions — the memory for future tasks"; §1 trust-in-the-loop.

## Three stacked pieces

1. **The honesty principle (foundation)**: record only what was observed; represent the
   unobserved as explicitly unknown — never plausibly filled in. ("An audit trail that
   guesses at provenance is worse than one that admits the gap.") Mostly free with event
   sourcing; apply deliberately at the edges:
   - Retention purges: record the purge, render "purged per policy," never a bare not-found
     (see IDEA-artifact-retention).
   - KNOWN VIOLATION to fix: NodeBootstrap falls back to Environment.UserName for the GitHub
     ExternalAccountId when gh is unavailable — a guess dressed as a fact. Make it an
     explicit unknown (Connection VO supports sentinels already).
   - Adopted external items: never fabricate platform events for pre-adoption history.

2. **Origin-incident convention (adopted now, in AGENTS.md)**: every standing rule records
   the incident that created it. The rulebook is an accumulation of documented scars, so
   readers know why a rule exists and when it might not apply.

3. **The learnings loop (the build)**:
   - `LearningRecorded` event — probably on the Project stream (project-scoped lessons),
     maybe Owner-scoped for cross-project habits. Carries: lesson text, source run/task id,
     recorded-by (agent run or human).
   - Producers: agents via `h9k learn "<lesson>"` mid-run (cheap, explicit); run summaries
     mined at closeout; the Slice-3 reviewer agent (findings that recur become lessons);
     the human directly.
   - Consumer: AgentPromptBuilder injects the project's current lessons into every run's
     prompt (bounded — top N, curated; stale lessons retired via a LearningRetired event).
   - Because it's event-sourced platform data: queryable, auditable, syncs to every node
     (Windows tower gets the Mac's lessons), and survives any individual session — all the
     things Collaboard's gitignored LESSONS.md files structurally cannot do.

## Prior art: beads (added 2026-08-19)

Steve Yegge's beads (github.com/gastownhall/beads) ships this exact pattern as a
standalone tool and is the reference implementation to crib from: `bd remember
"insight"` stores persistent project memory, `bd prime` injects memories plus
workflow context into any agent's session, and **compaction** semantically
summarizes old closed work instead of deleting it. Mapping to our design:

- `bd remember` / `bd prime` ≈ `h9k learn` + AgentPromptBuilder injection - same
  producer/consumer split we sketched, proven at scale.
- Compaction answers our curation question: retire lesson VOLUME by periodic
  semantic distillation (a summarization run) rather than caps or LRU - decay,
  not deletion, which also fits the honesty principle (the full history stays in
  the streams; only the injected view compacts).
- What beads lacks and we keep: the runtime (dispatch, gates, review, closeout)
  and event-sourced audit. We adopt the memory pattern, not the tool - adopting
  the tool would fork the source of truth (same verdict as Collaboard).
- Their dependency-ready graph also independently validates backlog 05's
  BlockedBy design. Their multi-machine story is Dolt remotes - git-style
  push/pull of the whole versioned database through a shared remote, NOT
  peer-to-peer - an alternative lens for IDEA-p2p-lazy-sync: they replicate
  everything and merge; we would move metadata shells and let leases prevent
  conflicts instead.

## Open questions

- Curation: who prunes lessons so the prompt doesn't bloat? (Human via CLI? Periodic
  distillation run? Cap + LRU?) Prompt budget per project?
- Scope granularity: project vs owner vs task-type (lessons per persona, §6.6?).
- Relation to verification profiles: a recurring lesson ("agents keep forgetting X gate")
  might belong as a gate, not a prompt line — lessons as a funnel INTO harder enforcement.
- Roadmap fit: #3 daily-driver hardening; reviewer-agent synergy at Slice 3.
