# IDEA: The skill layer - owner and project skills, states, sharing, and personas

**Status:** Draft - consolidated funnel entry, not a decision
**Consolidates (2026-08-21):** IDEA-platform-defaults.md (2026-08-17 sketch) and the
persona/skill-layer section of IDEA-ingestion-and-planning-workflow.md (2026-08-21).
Both sources now point here; this file is the single design surface for the eventual
design session.

## The idea

Skills are how judgment travels. A project's skills travel with its repo (this exists
today: `.claude/skills/`, discovered by every dispatched agent). An owner's skills
travel with the person - across every project they touch, and across nodes once P2P
lands. Hall9k owns the scoping, states, discovery, and sharing of skills; what a skill
*says* (house style, card-writing conventions, review checklists) is deliberately the
user's, never prescribed by the platform.

## Settled between the two sources

- **Two tiers, not three**: project-scoped (lives with the repo) and owner-scoped
  (lives with the person). The original sketch floated a node tier; the walkthrough
  rejected it - the owner drives the node, so node-level skills are redundant. Where
  the sketch said "platform tier," read "owner tier."
- **Precedence**: project supersedes owner, always - the repo knows its own rules.
- **Personas ride this substrate**: a persona is a named bundle of skills + prompt
  template + tool policy, no bot identity (the 2026-08-16 persona framing). Discovery
  and refinement personas (how a card gets written) are the first customers beyond
  implementation personas.
- **Skill states live in frontmatter, not folder placement**: `active` (in context),
  `inactive` (owner turned their own skill off; still theirs), `invited` (shared by
  someone else, unaccepted - acceptance, not reactivation, is what unlocks it).
- **Sharing is direct-copy with recipient-side acceptance**: `h9k skill share` prompts
  with a numbered list of owners the sharer shares projects with; the skill lands in
  the recipient's owner scope as `invited`; `h9k skill accept` activates. Declining is
  simply never accepting - no reason required.
- **Injection point is the executor seam**: ClaudeExecutor already controls
  `--settings`; owner-skill assembly is the same kind of flag/materialization work,
  composing with the slim profile (backlog 29) - skills are prompts and files,
  orthogonal to MCP declarations.

## OPEN TENSIONS - flagged for the design session, deliberately unresolved

1. **Opt-out semantics vs skill states.** The sketch had per-project opt-out of owner
   defaults ("this project: platform defaults off" or a blocklist); the walkthrough
   has owner-side `inactive`. These are different knobs (project rejects vs owner
   pauses) - decide whether both exist, and which wins when they disagree.
2. **Copy vs reference for owner skills.** Snapshot into the platform store, or
   reference live from `~/.claude`? Snapshot gives determinism and sync-ability
   (P2P: owner scope replicates per the learning-capture scope table); reference
   gives zero-friction editing. The multi-node future leans snapshot-with-sync; the
   solo present leans reference.
3. **Sharing needs identity.** `skill share` presupposes multiple owners and a way to
   route a file to one - which is P2P territory (or at minimum multi-owner projects).
   Decide the v1 scope: build states + scoping now, defer share/accept until an
   owner can actually be addressed?
4. **Skills as platform data vs filesystem convention.** The sketch wanted a
   NodeDefaults/PlatformProfile concept, possibly event-sourced, so nodes behave
   identically and defaults sync. The walkthrough treats skills as files with
   frontmatter. Likely answer: files are the storage, events record the milestones
   (shared, accepted, activated) - the transcripts-on-disk discipline - but decide
   deliberately.
5. **Learnings become skills.** IDEA-learning-capture promotes corroborated learnings
   into "a generated skill file or AGENTS.md entry." That generated artifact lands in
   exactly this layer - same scoping vocabulary (project/owner), same states? The two
   designs should share one skill store, not invent two.
6. **Persona declaration timing** (from the walkthrough's open questions): is the
   discovery/refinement persona declared at `idea add`, or resolved when discovery
   actually runs - allowing the default to change between ingestion and discovery?
7. **Does the owner tier carry settings too** (permissions, budgets, model defaults -
   the IDEA-platform-defaults question), or only skills/commands/docs? Note decision
   #33's known limit (installed daemon has no config home) is adjacent: this layer
   may BE the config home that limit is waiting for.

## Relationship map

- Backlog 29/30 (slim profile, auxiliary sessions): orthogonal capability axis - MCPs
  are launch-time capability, skills are knowledge; both compose at the spawn seam.
- IDEA-learning-capture: promotion's output lands here (tension 5).
- S1-12 install / backlog 24 doctor: setup is where "bring your skills in" happens.
- P2P corpus: owner-scope replication is the same rule as owner-scoped learnings.
- Repo-resident skills (PR #3) stay project-tier unchanged; this adds the owner tier.
