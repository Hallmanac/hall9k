> **PARTIALLY ABSORBED 2026-08-23: the idea/discovery/promotion half shipped as decision #35, the persona half consolidated into IDEA-skill-layer. STILL LIVE here: idea-side ingestion sources (--from-jira/--from-issue/--from-file/--from-url on idea add) and the async Ingesting state with reachability checks - nothing of that exists in src.**

# IDEA: Ingestion, discovery, and refinement — the day-to-day planning flow

**Status:** Draft — funnel entry, not a decision
**Origin:** Day-to-day UX walkthrough (voice), 2026-08-21
**Builds on:** Task lifecycle split (Draft → Refinement → Published → Assigned); agent identity & persona decision, 2026-08-16 (personas are prompt templates + tool policy, not bot identities)
**Would become:** Decisions Log entry (number TBD)

## The gap

Hall9k has ingestion for tasks (`h9k task add --from-jira`, `--from-github`) and a real assignment flow, but two things are still undecided: how an **idea** (as opposed to a task) gets into the system from the same range of sources, and what Hall9k's backbone actually owns versus what's left to the AI process running on top of it. Both came up while walking through the day-to-day use case rather than the architecture in the abstract.

## The core split: backbone vs. AI process

Hall9k is deliberately not opinionated about *how* discovery or refinement happens — that's where the AI does the work, and it's meant to be swappable (see persona/skill section below). What Hall9k *does* own is the shape:

- **Idea → Discovery → one or more Draft Tasks.** An idea always goes through discovery. No flag skips this. If you don't want discovery, you don't add an idea — you add a task directly.
- **Draft Task(s) → Refinement → Published.** Refinement is a separate pass, potentially over multiple tasks at once, and is what already exists today.

This keeps the funnel honest: the *presence* of an idea in the system is a commitment to running it through discovery, not a lighter-weight task with a different label.

## Ingestion source parity

`h9k task add` already supports `--from-jira` and `--from-github`. The same source flags should exist on `h9k idea add`:

- `--from-jira`
- `--from-github`
- `--from-file` (e.g. a markdown writeup from a prior AI conversation — already mostly fleshed out)
- `--from-url` (a raw web page)

The mechanism is identical between `idea add` and `task add`; the only difference is which state the ingested content lands in (idea awaiting discovery vs. draft task awaiting refinement). A two-sentence idea, a fully-fleshed markdown dump, and a directly-pulled Jira card all enter through the same doors — they just carry different amounts of pre-existing structure into whatever comes next.

## Async ingestion for `--from-url` / `--from-file`

Ideas ingested from a URL (or a file that needs interpretation) need an objective/title generated, and the user shouldn't have to write that themselves. Proposed flow:

1. **Reachability check first.** Before spending any agent tokens, Hall9k verifies the source is actually reachable (URL resolves, file exists). If not, fail fast and tell the user directly — let them troubleshoot before an agent gets involved.
2. **Hand off asynchronously.** Once reachability is confirmed, Hall9k creates the idea immediately in an `Ingesting` state with a reference ID, and returns a short "ingestion in progress" message to the user rather than blocking on the agent.
3. **Agent does the work.** A dispatched agent fetches the URL/file, converts it to markdown (stored the same way discovery transcripts and attachments already are), and generates the objective statement. It retries with a bounded timeout/attempt count if the source is flaky, and flags the idea as failed-to-ingest if it exhausts retries.
4. **Progress via `h9k idea show`.** No separate "check ingestion status" command — `idea show` on the reference ID just reports whatever state the idea is actually in: `Ingesting`, ready-for-discovery, or (on failure) flagged with a reason. This reuses the same "one place to ask where something stands" pattern as `task show`.

## Persona/skill layer for discovery & refinement

> Consolidated (2026-08-21) with IDEA-platform-defaults.md into IDEA-skill-layer.md,
> which is now the single design surface for this section. Retained here as written.

Separately from the ingestion mechanics above: discovery and refinement are where the actual planning judgment happens, and that's deliberately not baked into Hall9k's backbone. It should be a swappable persona/skill, the same shape as the existing role-persona decision (prompt template + tool policy, no bot identity) — just applied to *how a card gets written* rather than *how code gets implemented*. This lets someone bring their own house style (verbosity, required sections, Jira/Confluence conventions, etc.) without that style being prescribed by Hall9k itself.

### Skill scoping

- **Project-scoped**: lives with the repo, travels with the project.
- **Owner-scoped**: lives with the person, travels with them across nodes once P2P lands. (No separate node-level tier — the owner drives the node, so node-level skills would be redundant with owner-scoped ones.)

### Skill states

A skill's front matter carries a status flag rather than the skill living in different folders depending on state:

- `active` — available to be pulled into context.
- `inactive` — the owner has their own skill turned off for a while; still theirs, just not live.
- `invited` — a skill shared to this owner by someone else, sitting unaccepted. Distinct from `inactive` because acceptance (not just reactivation) is what unlocks it.

### Sharing

`h9k skill share` — direct copy, no approval gate on the sender's side. Prompts with a numbered list of other owners (across projects the sharer has in common with them) and accepts space-separated selections. On the receiving end, the skill lands directly in the recipient's owner-level skill directory with `status: invited`; `h9k skill accept` flips it to `active`. Declining just means it never gets accepted — no reason required.

## Flagged direction (not fleshed out yet): `h9k status` as a dashboard

Came up naturally while discussing invited skills: `h9k status` is trending toward a real dashboard — ready tasks, blocked tasks, pending skill invites, eventually dependent counts from the dispatch-ordering idea. Worth fleshing out soon, but intentionally out of scope for this document.

## Open questions

- What's the bounded retry/timeout policy for the URL/file ingestion agent — fixed attempt count, exponential backoff, wall-clock ceiling?
- Does a failed ingestion (source never became reachable) get archived automatically, or sit as a flagged idea until a human clears it?
- Should `--from-file` support anything beyond markdown (e.g. plain text, PDF export of a doc)?
- Does the persona/skill applied during discovery need to be declared at `idea add` time, or is it resolved later when discovery actually kicks off (allowing the default to change between ingestion and discovery without re-ingesting)?
