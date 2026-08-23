---
project: hall9k
type: chore
objective: The hall9k project itself moves into its default project home - the dogfood project becomes the first conforming citizen of the shape it built
criteria:
- The hall9k project's home is created at the default location (~/.hall9k/projects/hall9k) with the full 47 shape: generated AGENTS.md, seeded skills, ideas/, tasks/, runs/
- The bare repo and every worktree relocate under <home>/repo/ with git worktree repair covering the absolute paths; dev/ remains the primary-branch worktree; a build and full test run pass from the relocated dev/
- The project's recorded paths update to the new home, the daemon is bounced onto them during a quiet board (no live worktrees), and startup adoption reports clean (adopted or empty, zero requeued, zero failed)
- The old ~/Code/Hall9k_Platform location is left as an empty shell or removed only after the relocated build, tests, and one dispatched end-to-end task all pass - nothing is deleted before the new home has proven itself
- The repo's in-tree backlog/ directory is marked as the dogfood-era archive (a README line at its top), with new work rendering into the home per slice 48
- The orchestrator-window docs and any absolute paths in AGENTS.md or scripts that name the old location are corrected
---
The one-time cutover ruled at discovery (idea 64e4ebd2): clean start at the
default location, not adopt-in-place - the reference project honors the
defaults. This is the agent-assisted migration pattern applied to ourselves
first. Runs during a quiet board window after 47 lands and installs; reset
tolerance is the net, but the criteria above mean it is never needed blindly.
