# Idea: Platform-level defaults for agent config (skills, commands, AGENTS.md, CLAUDE.md)

Captured 2026-08-17 (mid-MVP session; design discussion deferred to its own session).

## The idea (Brian's framing)

Hall9k ships with / carries a set of defaults: when you set up Hall9k on a brand-new
machine, a user brings their personal set of Claude skills and commands into the Hall9k
application directory, and those apply across ALL projects on that node unless explicitly
opted out. A project's own skills/commands/AGENTS.md/CLAUDE.md supersede the platform-level
versions.

## Initial mapping (to refine in the design session)

- Three-tier precedence: project (.claude/ in repo) > platform (~/.hall9k/defaults/?) > user (~/.claude).
  Claude Code already implements the outer two tiers natively; Hall9k adds the middle.
- Injection point: the executor seam (ClaudeExecutor already controls --settings; --add-dir
  and skill-path assembly are the same kind of flag work). Or: daemon materializes a merged
  .claude/ into each worktree at dispatch.
- Model as platform data, not filesystem convention (per §10's connections principle):
  a NodeDefaults / PlatformProfile concept, possibly event-sourced, so the Windows node and
  the Mac node behave identically and defaults sync in the multi-node future.
- Per-project opt-out: a project setting ("use platform defaults: false" or a skill blocklist).
- Relationship to personas (§6.6): a persona is close to a named bundle of
  skills+prompt+tool-policy — platform defaults may be the substrate personas are built on.
- Relationship to h9kd install (S1-12): setup is where "bring your skills into Hall9k" happens.

## Open questions for the design session

- Copy vs reference (do defaults get snapshotted into ~/.hall9k or referenced live from ~/.claude?)
- How defaults version/sync across nodes (git repo? platform events? content-addressed store §8.1?)
- Does the platform tier carry settings (permissions, budgets) too, or only skills/commands/docs?
- Migration path for the current repo-resident .claude/skills (PR #3) — those stay project-tier.
