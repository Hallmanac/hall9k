---
name: orchestrator-recipe-generator
description: Generate or regenerate this project's (or this node's) orchestrator recipe: the files that let a human launch a lean, disposable orchestrator session for this project from a terminal, plus the recipes for the scoped sessions it spawns. Use when a project is new, when the platform or the machine changed, or when Brian asks for the recipe to be redone.
---

You are writing the recipe for a project orchestrator session. Read this whole skill before
touching anything. Do not implement platform features; you write prompts, settings, and seed
files only.

### What an orchestrator session is

A window a human looks through, not an alarm. It runs in the project's directory, owns the
project's board (drafts, walks, park rulings, merges at the repository's bar, relaying between the
human and running sessions), and never implements a feature itself; work enters through
`h9k task add` and is built by dispatched sessions. The node orchestrator owns the machine: the
ceiling, the budget, the daemon, the binaries, `config.json`, project registration. The seam in
one sentence: the node owns the resource, the project owns the work.

### The contract the recipe must meet

1. Minimal on turn one. The recipe plus the harness must cost as little context as possible.
   Measure it with `h9k orchestrator measure` (a one-turn probe) after writing, put the number
   in the recipe's header, and cut anything that does not earn its place. Rules of thumb: no
   restating what the CLI's `--help` already says; no copying standing rules into the recipe
   (they live in the session's memory directory and are loaded from there); no history.
2. Disposable. A fresh session started from the recipe must be able to carry on from where the
   last one stopped, with no compaction and no resume, ever. That means the open loop lives on
   disk: `journal.md` (a state document, rewritten at boundaries, never appended to, under a
   stated token budget with a snapshot-to-notes rule) and `sessions.md` (the registry of sessions
   this window spawned outside the task lifecycle, each with its fresh-start command).
3. A start-up sequence that ends in a report: read the journal, read the registry, arm a
   filtered attention monitor on the daemon log, ask the daemon directly (`h9k daemon status`,
   then `h9k status`), report to the human in plain language: what is in flight, what needs
   them, what the window will do next. If the journal was not enough to re-orient, the window
   adds a line to the journal's re-orientation log so the next regeneration can carry the
   missing field.
4. Scoped sessions. Ideas and task refinement happen in their own lean sessions, each with its
   own recipe (`idea-discovery`, `task-refinement`) that reads a hand-off journal written before
   the launch. The orchestrator recipe states the spawn procedure: write the hand-off first, pick
   the recipe, launch headless in the background, add the registry row, read what it wrote when
   it ends, and never `--resume`.
5. Node-scope commands forward, never block: a project window looks for a live node orchestrator
   and sends it the command; with none up, it runs the command in place and journals that.
6. The operator voice: concise, plain, one idea per paragraph, examples offered in a line, more
   than about three concepts means one per message with a map first. Writing conventions: no em
   dashes, full sentences, no AI attribution in anything authored for people.
7. A feedback file. The recipe names where the window writes the human's feedback about the
   recipe itself, dated, the moment it is given.

### What you must discover, not assume

- Where the window runs: the project directory `h9k project show <name>` reports, and whether
  its code is the directory itself or a `repo/dev` checkout inside a project home.
- The machine: operating system, the shell the human uses, whether `tail -F` exists or the log
  tail has to be PowerShell's `Get-Content -Wait`, where `h9kd.log` is, what time zone the log
  prints. Write the monitor step for what exists.
- The project: its verify gates, its base branch and merge style, its commit conventions, the
  external trackers it links (GitHub issues, Jira), the projects it shares the node with and
  therefore the ceiling with, and the per-project ceiling if enforced on this version.
- The human: their name for the recipe's voice sections, and nothing else; do not invent
  standing rules from what you see in the repo. Rules are documented scars and arrive later,
  written to the session's memory directory when the human rules.

### What you write

- `recipes/orchestrator.md` (or `.new` beside an existing one; never overwrite): a provenance
  header, the measured turn-one cost, then the prompt half meeting the contract above, specific
  to this project and machine. Do not write a launch line into it: the launch line points at the
  shipped anchor and is read with `h9k orchestrator launch-text show --cli <name>` for each CLI
  you found installed; if a CLI needs a different line, set it with `launch-text set` and read it
  back. Never edit `launch-anchor.md`.
- `recipes/settings.json`: read from the platform (`h9k orchestrator settings show`), never
  composed; it carries the inbound-message policy, the bypass confirmation suppression, the
  model, and display preferences, and must ride on the launch line as `--settings` because a
  project-scope settings file can only make the inbound policy stricter.
- `recipes/idea-discovery.md` and `recipes/task-refinement.md` (same `.new` rule): the
  scoped-session recipes, with the same discovery applied.
- In node mode (`orchestrator-recipe-generator node`): the node recipe against the node contract
  (ceiling, budget, daemon, binaries, config, project registration), from the same discovery.
- `journal.md` and `sessions.md` seeds if none exist; never overwrite an existing journal.
- `notes/prototype-feedback.md` if none exists.

### Finish

Run `h9k orchestrator measure`, write the number into each recipe's header, and print the launch
line for the human from `launch-text show`. Say what you discovered that the contract did not anticipate; that is feedback for
the skill itself.
