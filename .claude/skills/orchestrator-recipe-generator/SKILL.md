---
name: orchestrator-recipe-generator
description: Generate or regenerate this project's (or this node's) orchestrator recipe: the files that let a human launch a lean, disposable orchestrator session for this project from a terminal, plus the recipes for the scoped sessions it spawns. Use when a project is new, when the platform or the machine changed, or when Brian asks for the recipe to be redone.
---

You are writing the recipe for an orchestrator session: a node's or a project's. Read this whole
skill before touching anything. Do not implement platform features; you write prompts and seed
files only. Nothing you write here is rendered by the platform — the platform owns exactly two
files beside what you write (`launch-anchor.md` and `settings.json`), and you never touch either.

## What an orchestrator session is

A window a human looks through, not an alarm. It runs in the project's directory (or, for the
node, at the node's home), owns its slice of the platform (drafts, walks, park rulings, merges at
the repository's bar, relaying between the human and running sessions, for a project; the
concurrency ceiling, the budget, the daemon, the binaries, `config.json`, project registration, for
the node), and never implements a feature itself; work enters through `h9k task add` and is built
by dispatched sessions.

The one-sentence seam: **the node owns the resource, the project owns the work.**

## Which mode you are in

This skill runs from two places, sharing one discovery and one set of canonical blocks, and
writing two different recipe sets:

- **Project mode.** You were run from inside a project's home directory (or a worktree beneath its
  `repo/`). `h9k project list` names every registered project but does not print where any of them
  live — resolve which one by running `h9k project show <name>` for each name it lists and matching
  its `Home` field against the working directory (or an ancestor of it, for a `repo/dev` or
  `repo/wt-*` worktree); if that matches more than one project or none, stop and ask which project
  rather than guessing. Everything from here on that says "the project" means that one.
- **Node mode.** You were run from the node's own home — `h9k config show` prints the config
  file's own path, whose parent directory is the home; it defaults to `~/.hall9k` — or the human
  told you explicitly this is a node run. There is no project to resolve.

Do not infer the mode from anything else — not the invoking agent's own working directory if it
differs from both of the above, not a guess at what "seems more useful" to generate. If neither
resolves, say so and stop.

## What you discover, once, shared by both modes

Everything below is observed, never assumed. Where a fact cannot be observed, say so honestly in
the recipe rather than filling in a plausible guess (AGENTS.md's "never guess at unobserved
facts" applies to what you write here exactly as it does to the platform's own audit trail).

**The machine.**

- Operating system: `uname -s`/`-m` (or `$PSVersionTable.OS` on Windows) — this decides which
  shell the launch line and the monitor step render for. `h9k` itself already renders the
  computed default launch line for the OS it runs on (`LaunchTextDefaults.Render`), so you do not
  need to hand-adjust the launch line for this alone; the flag is for the recipe's other
  shell-shaped content (the monitor command, any paths).
- The operator's shell: `$SHELL` on POSIX; `$PSVersionTable.PSEdition`/`.Version` on Windows. A
  Windows machine's orchestrator runs `h9k` through PowerShell 7, never Git Bash: even where Git
  Bash's own `tail -F` works (GNU coreutils on PATH), the console codepage under Git Bash mangles
  `h9k`'s own box-drawing and glyphs. Confirm PowerShell 7 (not Windows PowerShell 5.1) is what is
  actually on the machine before writing a recipe that assumes it. `h9k` also wraps its own output
  near 80 columns whenever stdout is not a TTY, which is every invocation an agent session itself
  makes — on Windows and everywhere else alike, not a Windows-only trait — so a recipe's start-up
  sequence should say to read a task's own rendered `tasks/<id>/task.md` (or widen with `h9k task
  show`) rather than trust wrapped CLI text for anything long.
- Whether `tail -F` exists and behaves: decide from the operator's actual shell, not from probing
  `tail` itself. BSD tail (macOS) and GNU coreutils tail (Linux, and Git Bash on Windows) both
  support `-F`; only a shell with no `tail` at all — PowerShell — needs the substitute,
  `Get-Content -Path <path> -Wait -Tail 0`. A Windows machine still gets the PowerShell substitute
  even on the rare chance `tail -F` would technically work in a Git Bash window nobody uses to run
  `h9k`. Do not run `tail --help` to confirm this: BSD tail (macOS) has no `--help` and exits
  non-zero on it, which proves nothing either way, and do not fall back further to running bare
  `tail -F` against an existing, unchanging file as a substitute probe — on both BSD and GNU tail
  that blocks forever waiting for the file to grow, with nothing to timeout, background, or kill
  it, and the fact two sentences up already answers the question that probe was trying to ask.
- Where `h9kd.log` lives: `h9k daemon status` prints the path. Confirm the timestamps it writes
  are local time and which zone, by reading the newest line and comparing it to `date` run in the
  same breath — do not assume the log's zone matches the operator's; compare, do not infer.
- Which agent CLIs are installed: check `PATH` for `claude` and note whatever else you find (for
  example `codex`, `cursor`, `gemini`) by name in the recipe's header, without composing a launch
  line for any of them — only `claude-code` has a computed default today
  (`LaunchTextDefaults.For`), and inventing one for a CLI the platform has never launched is
  exactly the guess this skill forbids elsewhere.

**The project (project mode only).**

`h9k project show <name>` for: the home directory, whether the code lives in the home directory
itself or in a `repo/dev` worktree beneath it (check `<home>/repo/dev` for a checkout), the verify
gates, the base branch, the commit style, the branch template, the linked trackers (a bound Jira
board, GitHub as the connection's own provider, any context links), and `h9k project list` for the
other projects registered on this same node — they share this node's concurrency ceiling and
spend budget, which is why the project recipe's node-seam section names them rather than treating
the node as this project's alone. There is no single "merge style" setting to read; observe it
instead from a few recent Done tasks (`h9k task list --project <name> --state done`, then
`h9k task show <id>`): does this project typically publish pre-approved (the daemon rebase-merges
on its own once gates and review are satisfied) or does the owner merge each pull request by hand.
State what you actually saw, not a rule you inferred from it — a pattern in five tasks is not a
standing policy, and this skill does not mint one (see *Never invent a standing rule* below).

**The node.**

`h9k config show` for the concurrency ceiling, the spend budget and its period, and the model
policy; `h9k daemon status` for whether a daemon is running, its pid, and where its log is (already
used above for the clock and monitor discovery); `h9k project list` for every project registered
on it. In project mode, this is what the project recipe's node-seam section points at, not what it
duplicates — the node's own facts belong in the node recipe, generated separately, and a project
recipe never restates them.

## Never invent a standing rule

Do not turn anything you observe in the repository, its `AGENTS.md`/`CLAUDE.md`, or its git
history into a standing rule written into the recipe. A standing rule is a documented scar: it
exists because a human ruled on something, with the incident that produced it named alongside it,
and it is written to the session's own memory directory the moment it is made, not synthesized by
a generator reading code. A brand-new project's recipe carries no standing rules at all, and that
is correct, not incomplete. The recipe you write states the contract (what every orchestrator
window's start-up sequence, journal, registry, and voice look like) and the facts you discovered
above (this machine, this project); it never states "always do X" for anything a human has not
actually ruled on. Where the discovery above turns up an observed pattern worth a human's
attention (the pre-approval pattern, an unfamiliar log timezone), report it as an observation in
your own summary to the human, not as a rule baked into the recipe.

## Leanness is a ceiling, not just a floor

Every contract below says "not thinner than this" — that names a floor, what a recipe must cover,
never a license to keep adding. A recipe is lean when every sentence in it earns its place on the
turn a fresh window reads it: cut anything that does not. Concretely, that means no restating what
the CLI's own `--help` already prints (point at the command instead of paraphrasing its output),
no narrating this project's or this skill's own history (why a rule exists belongs in
`AGENTS.md`/the Decisions Log/a memory file, not copied into the recipe), and no covering the same
fact in two sections when one pointer between them would do. Before you write a recipe over an
existing one (producing the `.new` file below), compare word counts against the file you are
replacing: a regeneration that grew substantially without a corresponding new start-up step, a new
discovered fact, or a new platform capability to cover is a sign you paraphrased instead of
pointing, not a sign the project got more complex. Say the before/after word count in your summary
either way, so a human reviewing the `.new` file can see the delta before deciding whether to
adopt it.

## The two canonical blocks

These two blocks are defined exactly once, here. Copy them verbatim, unedited, into every recipe
you write (the orchestrator recipe, `idea-discovery.md`, `task-refinement.md`, and the node
recipe) — this is what "rendered from one place" means: the wording lives in this skill, not
independently re-typed into each recipe, so the four copies cannot drift apart from each other or
from what this skill says next time it runs.

### Writing conventions (copy verbatim)

> No em dashes (U+2014); use commas, semicolons, colons, periods, or parentheses instead. Full
> sentences over telegraphic fragments. No AI attribution in anything authored for people: no
> "Generated with Claude", no Co-Authored-By trailer in commits, PR titles, or PR bodies. These
> rules live here, in the recipe itself, rather than only in the operator's own user-level
> `CLAUDE.md`, because `--setting-sources project` drops that file from every session this recipe
> starts.

### Talking to the operator (copy verbatim)

> This is the default voice for every session this recipe or its scoped siblings start: for a
> project or node the operator has never opened a window on, exactly as much as one they have run
> for months.
>
> Lead with the answer. Write one idea per paragraph, in plain, human-readable language, the way
> you would say it out loud rather than as a dense multi-clause summary. Offer an example or a
> scenario in a line rather than pasting one in unasked. When there are more than about three
> concepts to cover in one reply, send the first message with a one-line map of what follows and
> deliver the rest one at a time as the operator asks for them.
>
> Opening a walk is the one place a scenario is not optional: before breaking a task or an idea
> down criterion by criterion, assume the operator has forgotten what it says, and open with a
> plain-language recap of what the work is for and one acted-out scenario of the world after it
> ships. Only then move into the breakdown.
>
> A board report is a list grouped by state (needs you, in flight, queued, and so on); every row
> is the item's id plus a plain description of what it actually is, never a bare id: the operator
> reads the board through the descriptions, not the hex.
>
> This voice is a default the operator may edit in place, in their own copy of this recipe. Record
> a hand edit like that in this file's own provenance header, under `hand-edited-since`, the
> moment it happens. A later regeneration of this recipe never overwrites a hand edit: it arrives
> as `<this file>.new` beside the current one, for the anchor's own start-up step to compare and
> reconcile, exactly like any other change this skill makes to an existing recipe. Nothing about
> that flow ever touches `launch-anchor.md`.

## The provenance header every recipe you write carries

At the top of every file you write under *What you write* below (not the seeds — see that
section), before anything else:

```
---
generated-by: orchestrator-recipe-generator, run by <agent CLI and model, e.g. "Claude Code, sonnet-5">
generated-at: <date '+%Y-%m-%d %H:%M %Z' — run it, do not hand-type it>
skill-version: shipped with h9k <output of `h9k --version`>
hand-edited-since: no
turn-one-cost: not yet measured, see *Finish* below
---
```

Once *Finish* below has run `h9k orchestrator measure` for a CLI, go back and fill in that CLI's
own `turn-one-cost` line with what it reported and the date, so "minimal" is a number sitting
right in the file the operator opens, not a promise. (A recipe covering more than one CLI, once
that is possible, would need one such line per CLI; today there is only ever `claude-code` to
measure.) When you are regenerating a recipe a human has since hand-edited (you can tell because
the existing file's own header already says `hand-edited-since: yes`), your `.new` file's header
carries your own new generation line but leaves the existing file's `hand-edited-since` note
completely alone — that note is the current file's history, not something a `.new` sibling
inherits or resets.

## What you write

Every path in this section is relative to the home *Which mode you are in* above resolves to: the
project's home directory in project mode, the node's home directory in node mode (`h9k config
show`'s config-file path, parent directory — not a hand-typed `~/.hall9k`, which is only its
default). If you were invoked from anywhere else under that home (a `repo/dev` or `repo/wt-*`
worktree, in particular — README.md documents running this skill from there), resolve the home
first and read and write every path below against it, never against your own working directory.

**Check the platform-owned files exist before writing anything.** `recipes/launch-anchor.md` and
`recipes/settings.json` render on `h9k project init`/`h9k project add` (project mode) or
`h9k install`/`h9k update` (node mode) — not on any background render loop — so a project or node
whose home predates this skill, or whose anchor has not been (re-)rendered since, may not have
them yet. If either is missing, stop and tell the operator to run `h9k project init <name>` (or
`h9k install`) once, first; do not write recipe content that a session will never actually load.

**Never overwrite an existing recipe file.** If `recipes/orchestrator.md` (or any recipe file
below) already exists, write `recipes/orchestrator.md.new` instead and say so in your summary;
the platform's `launch-anchor.md` is what compares `.new` files against their siblings and walks
the human through adopting, keeping, or merging them at the next launch — you do not do that
comparison yourself, and you never rename a `.new` file over the original.

**Check your own draft before it touches disk, mechanically, not from memory.** The *Writing
conventions* block above binds every recipe this skill writes to write, and it binds you, right
now, writing them. Before you save any file under this section, search the draft's full text for
the em dash character (U+2014, `—`) and rewrite every hit; do not rely on having kept the rule in
mind while composing. Do this once per file, immediately before that file is written, not as a
single pass at the end over everything.

Project mode:

- `recipes/orchestrator.md` (or `.new`): provenance header, then the project orchestrator's
  contract below, specific to this project and this machine.
- `recipes/idea-discovery.md` and `recipes/task-refinement.md` (or `.new`): the scoped-session
  contracts below, same discovery applied.
- Seed `journal.md` and `sessions.md` at the project home's own root — beside `notes/`, never
  inside `recipes/` — **only when absent**: never regenerate these, never give them a `.new`
  sibling, because they are the operator's own live state the moment a real window has written to
  them once. One rule per folder is why: `recipes/` is the platform-owned launch folder `install`
  and `init` overwrite outright and the anchor's own `.new` rule governs, while the journal and the
  registry are the window's own state, seeded once and never touched by the platform or this skill
  again (Brian's ruling, 2026-09-07 16:10 EDT). A seed is a minimal bootstrap (see the journal and
  registry contracts below for their shape), not a summary of anything you found in the project's
  history; an idle project's seed says plainly that nothing is in flight yet.
- Seed `notes/prototype-feedback.md` **only when absent**: a short header explaining what the file
  is for (a dated log of feedback about this project's own generated recipes, newest entry last,
  written by whichever window receives it, the moment it is given) and nothing else. This is a
  different file from any idea workspace's own feedback file from before the recipe was
  generated; do not merge the two.

Node mode: the same shape, one file, `recipes/orchestrator.md` (or `.new`) against the node
contract below, plus the same journal/sessions/notes seeding rule at the node's own home root as
resolved above (`<node home>/journal.md`, `<node home>/sessions.md`,
`<node home>/notes/prototype-feedback.md` — `~/.hall9k` only where that is what discovery actually
found, never assumed).

Never write `recipes/settings.json`, never write or edit `recipes/launch-anchor.md`, and never put
a launch line inside any recipe you write — see *Launch text* below for what you do instead, and
see *Spawning a scoped session*'s own stated exception below for the one launch line a project
orchestrator recipe does write.

## The project orchestrator recipe's contract

The recipe you write for `recipes/orchestrator.md` states, in your own words but not thinner than
this:

**What you are.** The project orchestrator for this project, running from its home directory (the
platform's own launch line always `cd`s there — never into a `repo/dev` worktree, even for a
project whose own code lives in one), disposable: nothing it remembers is
authoritative if a file or the database disagrees, and a fresh window started from this recipe
must be able to carry on from where the last one stopped. State the node seam sentence and name
the sibling projects discovery found sharing this node, since they compete for the same ceiling
and budget.

**Start-up sequence**, in this order, ending in a report:

1. Read `journal.md` (at the project home's own root, beside `notes/`) — the open loop: what is in
   flight, what is expected to land, what to do first. Trust it over anything remembered.
2. Read `sessions.md` (same root) — the registry of sessions this window has spawned.
3. Arm a filtered attention monitor on `h9kd.log`, using whichever tail mechanism discovery found
   for this machine (`tail -F`, or PowerShell's `Get-Content -Path <path> -Wait -Tail 0` where
   there is no `tail` at all), filtered to
   `parked|failed|error|merged|closeout complete|reopened|dispute|adopted|fatal|unhandled|\[2001\]|PR opened|pushed to existing PR`,
   matched case-insensitively. The daemon's own log capitalizes freely (`Unhandled exception`,
   `Failed to connect`, `outcome Disputed`), so a case-sensitive filter misses exactly the crash
   and dispute lines this monitor exists to catch: pipe `tail -F` (or `Get-Content -Wait`) through
   `grep -Ei --line-buffered '<filter>'` on POSIX, or `Select-String -Pattern '<filter>'` on
   PowerShell (case-insensitive by default; do not add `-CaseSensitive`). `\[2001\]` is
   `DaemonLogEvents.PullRequestOpened`'s own structural id (the default console
   formatter prints it inline as `Category[2001]`); key on it, not only on the prose beside it,
   because the prose is free to reword and only the bracketed id is guaranteed to survive that.
   `needs you` is a `h9k status`/`h9k task show` board label, never a line the daemon itself
   writes to `h9kd.log`, so it has no place in a filter matched against that log. `Monitor` is a
   deferred tool in a session shaped like this one — fetch its schema by name with `ToolSearch`
   before arming it. It dies with the session, so this is a start-up step every time, never
   something to assume is still armed from before.
4. Run `h9k daemon status`, then `h9k status`. Ask the daemon directly rather than inferring its
   health from a quiet pane: a stopped daemon queues work silently and the pane says nothing about
   the daemon itself unless it is down.
5. Report to the operator in plain language (the voice block above): what is in flight, what needs
   them, and what this window will do next. If the journal was not enough to re-orient you and you
   had to ask the operator something the journal should have told you, add one line to the
   journal's re-orientation log so the next rewrite of this recipe can carry that field.

**The journal (`journal.md`, at the project home's own root).** A rewritten state document, never
an append-only log: rewrite it whenever a ruling is made, a walk finishes, a pull request merges,
a session spawns or ends, or the operator leaves the terminal — not on a timer. State a token
budget (about 1,500 tokens; the seed and every later window should treat exceeding it as the
trigger to write a dated snapshot into `notes/` and shrink the journal back down, pointing at the
snapshot). Name these sections: what is in the middle of happening; what the next session should
do first; a re-orientation log (one line per thing a fresh window had to ask the operator to
re-establish); and, only once it applies, a dated "decisions made on the operator's behalf" list —
one line per decision, trimmed hard, with the full reasoning in that day's snapshot note rather
than in the journal itself — for whenever the operator has granted this window standing authority
over a class of decision. Do not create that section pre-emptively in a fresh seed; add it the day
it is actually needed.

**The registry (`sessions.md`, at the project home's own root).** One row per session this window
has spawned outside the task lifecycle (design walks, idea discovery, task refinement): when it
started, its kind, its subject, its status, and the exact one-line command that opens a fresh
session on the same subject. Liveness comes from `ListAgents`, never from this file; the registry
holds intent and the fresh-start command, nothing else. It never holds the daemon's own dispatched
runs — `h9k task show` already does that.

**Spawning a scoped session.** When work belongs in its own context (an idea walk, refining a
draft's criteria), spawn a lean session instead of doing it here:

1. Write the hand-off first: the subject's own `journal.md` (in the idea's workspace, or the
   task's directory) with what has been said so far and what the session should do. This is the
   safety net — if the operator disappears mid-sentence, the context is already on disk.
2. Pick the recipe (`recipes/idea-discovery.md` or `recipes/task-refinement.md`) and launch it
   headless, in the background, so this window is notified when it ends. Never `--resume`; a fresh
   session reads the hand-off journal, which is the whole point. Write the exact command in this
   section of the recipe, built the same way the platform builds any launch line, but pointed at
   the scoped recipe instead of the anchor:

       cd "<this project's working directory>" && claude --strict-mcp-config --setting-sources project --settings recipes/settings.json --dangerously-skip-permissions -p --append-system-prompt-file recipes/<idea-discovery.md|task-refinement.md> "<opening message naming the subject and its hand-off journal path>"

   This is a **stated exception** to *never write a launch line into a recipe* above, not a second
   rule: the orchestrator window's own launch line is a platform setting because the platform
   prints and measures it, but idea-discovery and task-refinement sessions have no anchor of their
   own and no `launch-text` entry to read one from, so the only place their line can live today is
   here, in the spawning window's own recipe (Brian's ruling, 2026-09-07 16:10 EDT, on the first
   run of this skill against the real hall9k project — see idea 0567af93's own
   `workspace/prototype-feedback.md`, not the seeded `notes/prototype-feedback.md` this skill
   writes into a project or node home; PLAN.md §16 #155 records the same ruling). Retire
   this exception the day the platform stores scoped-session launch text of its own; until then,
   write the real command, not a description of one. The working directory and the opening
   message both sit inside double-quoted shell segments, exactly like the platform's own launch
   line, and carry the identical hazard: an opening message that names an operator-supplied
   subject can itself contain a `"`, `` ` ``, or `$` that would otherwise close the quoted segment
   early or trigger command substitution the moment this line is pasted or run. Escape both values
   the same way `LaunchTextDefaults.EscapeForDoubleQuotes` escapes the platform's own launch
   line before substituting them in: on POSIX, backslash-escape `\`, `"`, `$`, and `` ` ``; in
   PowerShell (Windows), backtick-escape `` ` ``, `$`, and `"`. Never paste operator-supplied text
   into either quoted segment unescaped.
3. Add the row to `sessions.md` before the launch returns, including the interactive form
   of the same command (drop `-p`) so the operator can open a fresh one themselves.
4. When it ends, read what it wrote into the workspace, report the result to the operator in plain
   language, and mark the row.

**The node seam.** State the one-sentence seam and name the sibling projects sharing this node.
A node-scope command typed here (an install, a daemon restart, a ceiling change) forwards: look
for a live node orchestrator with `ListAgents` first; if one is up, send it the command with
`SendMessage` and relay the outcome; if none is up, run the command here, in project scope, and
say in the journal that this window did it. Never refuse or block a node-scope command; that
boundary was tried and dropped.

**Dispatching headless.** `h9k task start` turns the task's interactive-mode flag on for that run
(a human-triggered start is treated as the human staying at the wheel, per its own `--help`), so
it parks at every review boundary for a proceed. A dispatch meant to run straight through,
headless, needs `h9k task start` followed by `h9k task revise <id> --clear-interactive-mode`, not
`task start` alone. This is a fact about what the two commands do, true for every project on this
platform; it is not a standing rule about when to use either one, and states none.

A queued Rebase follow-up being claimed the instant a slot frees, and what that means for a window
that merges pull requests by hand, is exactly the shape of thing *Never invent a standing rule*
above forbids writing here as an "always do X": whether this project's operator merges by hand at
all is itself only an observed pattern (see the merge-style observation in *What you discover*
above), not something ruled on for this project. If discovery's own observation surfaces this,
report it to the human in your own summary rather than writing a merge-order rule into the recipe;
it becomes a standing rule, held in this window's memory directory, only on the operator's own
word.

**Clocks.** `h9kd.log` and `h9k status` print local time in whatever zone discovery found. Never
hand-type a stamp: run `date` in the same command that writes it — `$(date '+%Y-%m-%d %H:%M %Z')`
substituted directly into the file or message being written, not composed first and dated after —
because a stamp written even a few minutes after the clock was last checked is already wrong, and
every miss recorded against this recipe so far came from composing the prose before running the
command that reads the clock.

**Standing rules.** Live in this window's memory directory, loaded from there, and never restated
here — a rule copied into two places drifts the moment one of them changes. See *Never invent a
standing rule* above for what this recipe may not do instead.

**Ideas and drift.** When the operator says "log this idea," run `h9k idea add` immediately and
write what is said into the idea's own `workspace/journal.md` as the conversation continues — no
background session catches this otherwise. When a conversation started here grows past what a
board decision needs, offer once to move it to a scoped session, keep going here, or write it down
and restart fresh; offer once per topic, then let the operator's answer stand.

**Feedback about this recipe.** When the operator says something in this recipe (a start-up step,
a stated fact, a section here) was wrong, missing, or awkward to follow, write a dated entry into
`notes/prototype-feedback.md` (seeded by this skill; see *What you write* above) the moment the
feedback is given, one entry per point, in enough detail that a later run of this skill can act on
it without asking the operator to repeat themselves. This is the only record a later regeneration
of this recipe has of what earlier windows learned; an observation never written here cannot be
carried forward.

**The two canonical blocks.** Copy the *Writing conventions* and *Talking to the operator* blocks
above, verbatim, as their own sections.

## The node orchestrator recipe's contract

Same shape as the project recipe above, with these differences:

- **What you are.** The node orchestrator for this machine's install: the concurrency ceiling, the
  spend budget and model policy, the daemon's lifecycle, the installed binaries, `config.json`,
  connections, the owner record, and registering or deregistering projects. Never merges a pull
  request, rules on a park, walks a task's criteria, or drafts work into any project's board — a
  project orchestrator does those, and name where its recipe lives for each project discovery
  found registered on this node.
- **Start-up sequence step 4** is `h9k daemon status`, `h9k config show`, and `h9k project list`
  instead of `h9k status` (there is no single project's board to check from here).
- **The node seam** is stated the other direction: nothing stops a node-scope command running
  here, and the node recipe never forwards anywhere — it is the destination a project window
  forwards to. If a project needs attention and no project orchestrator is up, name which
  project's recipe would start one; the node window never drafts work into a project's board
  itself.
- No "Dispatching headless" section: the node never dispatches a task or merges a pull request,
  so neither fact applies here.
- Node-only standing facts are held to the exact same bar as *Never invent a standing rule* above:
  a fresh node's recipe states none of them, because nothing here counts as a rule until the
  operator has actually ruled on it for this node. Do not hardcode a sentence like "the ceiling
  and the model policy move only on the operator's own word" or "a daemon is never restarted under
  a running task" into every node recipe as though every node inherits it sight unseen — if the
  operator has actually ruled on either for this specific node, it already lives in this window's
  memory directory, exactly like any other standing rule, and needs no restating here.
- No idea-discovery or task-refinement recipes; no spawn procedure of its own beyond reaching a
  project orchestrator with `ListAgents`/`SendMessage`.
- Journal and registry contracts are identical in shape (state document, token budget,
  snapshot-to-notes, re-orientation log; one row per spawned session with its fresh-start command),
  scoped to node-level activity instead of one project's board.
- The two canonical blocks, copied verbatim, exactly as in the project recipe.

## The scoped session recipes' contract

`recipes/idea-discovery.md` and `recipes/task-refinement.md` are shorter, single-purpose recipes
launched by the project orchestrator (usually headless), never spawning further scoped sessions of
their own. Both:

- State what the session is for in one paragraph (discovery answers "what is this, and does it
  become a task, an epic, several tasks, a ruling, or nothing"; refinement answers "how does this
  become executable: an outcome-phrased objective, checkable criteria, agent-facing context that
  carries pointers rather than restatements, the right type, its dependencies, whether it belongs
  to an epic").
- State where the session runs (the project's home — the platform's own launch line always `cd`s
  there, never into a `repo/dev` worktree) and where its working state lives (the idea's
  `workspace/`, or the draft's own directory under `tasks/`) — nothing it remembers is
  authoritative if that directory disagrees.
- State the boundary: discovery turns a settled idea into a draft with `h9k idea promote
  <idea-id> [--project <name>] [--objective ...]` (`--project` is required unless the idea is
  already assigned to one), never `h9k task add`, because promotion is the
  platform's own hinge (Decisions Log #35) and the only way a draft's provenance carries the idea
  it came from (`TaskAggregate.SourceIdeaId`, rendered by `h9k task show` as "From idea") rather
  than a hand-typed id in free text; once promoted, discovery revises the draft with
  `h9k task revise` the same as refinement does. Neither discovery nor refinement ever publishes,
  assigns, or touches the daemon, the board, or a running task; refinement revises with
  `h9k task revise` and reads with `h9k task show`. Publishing waits for the operator to walk the
  criteria.
- State "read by pointer": start from the idea's `idea.md`/`workspace/journal.md`, or the draft's
  `h9k task show <id>` output and its own `journal.md` if one exists; read what is pointed at
  rather than restating it. When a pointer names a run, prefer the task's first run for the
  original build prompt and a later run for its review cycles — a follow-up run's own `prompt.md`
  is the follow-up prompt, not the original one, and a pointer that does not say which run means
  is an easy way to read the wrong prompt. When investigating code, read the rendered output first
  (a task's own `task.md`, a run's own `prompt.md`) and drop into the source that builds it only
  for what the rendered artifact cannot show — a dispatch site, a line number — rather than reading
  the builder first.
- `h9k task revise --file` (there is no `--file` on `h9k idea promote`) reads through
  `FrontmatterYaml`, the same real-YAML-scalar parser the machine-readable record block reads
  through, so a quoted value in the source text arrives without its quote characters rather than
  carrying them through literally; there is no platform gotcha to design around here.
- Keep their own `journal.md`, in the idea's workspace or the draft's directory: a rewritten state
  document, not appended to, under about 1,500 tokens, naming what is settled, what is open, and
  what the next session should do first.
- If the operator is in the terminal, open with a plain-language recap and one acted-out scenario
  before any criterion-by-criterion breakdown (the *Talking to the operator* block already says
  this; do not restate it a second way here). If headless, do the work the opening message names,
  write the result into the workspace, rewrite the journal, and end with a short summary and the
  open questions the operator still has to settle.
- Carry both canonical blocks, verbatim.

## Launch text

You never write a launch line into any recipe — except the one stated exception in *Spawning a
scoped session* above, the scoped-session command itself, which you do write, in full, into
`recipes/orchestrator.md` — and you never edit `launch-anchor.md` or
`settings.json` — both are platform-owned, always overwritten, and carry no fact a recipe would
ever need to restate. Instead, for each agent CLI discovery found installed:

- Read what the platform already has: `h9k orchestrator launch-text show --cli <name>` in node
  mode, or `... --cli <name> --project <name>` in project mode. Before anything has ever been set,
  this prints the platform's own computed default for `claude-code` (there is no computed default
  for any other CLI yet, so this prints nothing to work from for one and that is expected, not a
  bug to route around).
- For `claude-code` specifically (the only CLI `show` ever prints a real line for today):
  `h9k orchestrator measure` refuses to run against a line nothing ever asked to store — that
  guard exists so a bare probe cannot freeze the computed default into `config.json` (or the
  project's own settings) ahead of a later release's own flag changes. So on a fresh node or
  project, `show`'s output is a computed default, not a stored one, and there is nothing yet to
  measure: store it explicitly, even when the discovered machine needs no change at all —
  `h9k orchestrator launch-text set --cli claude-code '<the exact line show just printed>'` (add
  `--project <name>` in project mode) — before moving on to *Finish* below. `show` wraps its own
  output near 80 columns whenever stdout is not a TTY (*The machine* above), which is every
  invocation this session makes, so the line it just printed may already be broken across several
  lines; reassemble only those wrapped launch-line lines into one line before pasting it into
  `set`, by joining them with a single space, never a raw newline (the wrap drops the space that
  was already there, so a plain space restores it). `show` on a fresh node or project prints a
  trailing "Last measured: not measured. This is the computed default, not a stored setting..."
  explanation right after the wrapped launch line, in the same paragraph; that sentence is never
  part of the launch line itself, in this first read or the read-back below, so stop reassembling
  once you reach it and never fold it into what you paste into `set`. Never paste a
  multi-line value into the quoted argument. Single-quote the
  argument, not double-quote it: the printed line itself already contains embedded double quotes
  and a shell `&&`/`;` operator (`LaunchTextDefaults.Render`'s own output), and wrapping it in
  double quotes a second time lets your shell reparse those instead of passing the whole line
  through as one argument — the shell would receive `cd` as the stored text and then execute the
  rest of the launch line as a second command in your own session. Single-quoting is safe unless
  the printed line itself contains a single quote (an operator's home directory or project name
  with an apostrophe in it would carry one straight through, since the platform's own escaping
  only covers the double-quoted segments it renders): if you see one in what `show` printed, or in
  a corrected line you compose by hand, stop and ask rather than hand-escaping it. If the
  discovered machine genuinely needs a different line than what `show` printed (the shown default
  already renders correctly for the local operating system, so this is the exception rather than
  the routine case), single-quote and set
  that corrected line the same way, instead of the unchanged default. Read the result back with
  `show` afterward rather than assuming the set succeeded — but `show`'s own printout wraps under
  the same condition, so a second look at it cannot by itself prove the stored value is a single
  line. Capture that second `show` into a shell variable instead of only reading it on screen, and
  check the variable itself for an embedded newline (for example, in bash,
  `[[ "$captured" == *$'\n'* ]]` on just the launch-line portion, excluding the trailing "Last
  measured" line); a hit means the reassembly above went wrong, not that the platform stored
  something broken — fix the line and set it again before moving on. For any other CLI discovery
  found installed, `show` has no computed default to store in the first place (bullet above); leave
  it alone rather than inventing a line to set — there is nothing for that CLI to measure yet.

## Finish

For each CLI with a stored launch text (yours from the step above), run `h9k orchestrator measure
--cli <name>` (add `--project <name>` in project mode) and let it stamp the observed turn-one cost
onto that record. Then print the launch line for the operator with `h9k orchestrator
launch-text show`, one line per CLI, so they can paste it into a fresh terminal. Say plainly, in
your own summary, what you discovered that this skill's own contract did not anticipate — that is
feedback for the skill itself, the same file this contract tells every generated recipe to write
dated entries into.
