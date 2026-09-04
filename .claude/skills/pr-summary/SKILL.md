---
name: pr-summary
description: Generate a pull-request title and description from the commits on the current branch. Use to replace an already-open PR's body, or to draft one for a PR opened outside Hall9k's own dispatch — never as a build session's own closing summary, since the daemon composes a fresh PR's body itself. Output is text only; it never opens the PR.
---

# PR Summary Generator

Write the title and description the way a colleague who respects the reviewer's time would: plain
language, oriented up front, organized around what a reviewer needs to think about rather than the
order the commits happened to land in. The output is the deliverable: **never run `gh pr create`
or `gh pr edit`, and never open a PR** — the Hall9k daemon opens PRs (`PullRequestOpener`); agents
are forbidden from doing so.

**Use this skill to produce a full pull-request body, never as a build session's own closing
summary.** When the daemon opens a fresh PR it composes the body itself — the work-item mention,
the acceptance criteria, and the run/token footer are all written automatically, with whatever the
session's own final message said folded in underneath (truncated to 4,000 characters and defused
of closing keywords and control characters) as `## Agent summary`. Invoking this skill for that
final message nests a second work-item line and a second, necessarily invented footer inside the
daemon's real ones; that is not what this skill is for. Its two legitimate uses
are a description for a PR opened outside Hall9k's own dispatch entirely, and replacing the body on
an already-open PR — read the current body first (`gh pr view <number> --json body -q .body`),
because it carries the real footer and, whenever the review loop left something behind, a
`**Left unfixed:**` or `Review ride-alongs:` paragraph (Decisions Log #87) that has to survive
into the replacement's `## For the reviewer` section rather than being silently dropped. Hand the
drafted text back for a human to apply either way.

## Process

1. **Gather the change** (in parallel):
   - Commits on this branch vs the base: `git log --oneline origin/main..HEAD` (base is `main`
     unless told otherwise)
   - Full messages and bodies: `git log origin/main..HEAD`
   - Change shape: `git diff --stat origin/main..HEAD`
   - The diff itself for anything the commit messages describe vaguely — a description assembled
     from commit subjects alone reads like a changelog and tells a reviewer nothing the commit
     list didn't already.

2. **Find the linked work item, if any.** Check `task.md`'s `external-reference:` frontmatter line
   when this branch was cut for a Hall9k task — but not in the worktree itself: a task's worktree
   never contains `task.md`, which lives at `<project home>/tasks/<shortid>-<slug>/task.md`, a
   sibling of `repo/`. A dispatched session's own prompt names the project home under "Where this
   project lives"; read `task.md` from there. Otherwise check whatever the caller already knows
   about a linked card or issue. The `external-reference:` value already names which kind it is:
   `jira:<KEY>` is a Jira card, `github:<owner>/<repo>#<number>` is a GitHub issue. Use whichever
   one is present; when neither is, there is no work-item line at all — never invent one, and
   never fall back to a bare issue number with no source for it. A
   Jira key is not itself a URL: resolve it as `<site>/browse/<KEY>`, with `<site>` read from
   `h9k connection list`'s Site column — never guess the host. A GitHub reference maps directly:
   `github:<owner>/<repo>#<number>` is `https://github.com/<owner>/<repo>/issues/<number>`.

3. **Work out what a reviewer actually needs**, same two questions in this order:
   - What does this change, described so someone who hasn't read the diff understands its shape?
   - What here would a competent developer be puzzled by, disagree with, or waste time
     re-deriving? That is the only "why" worth writing down.

4. **Name what you cannot determine, rather than inventing it.** If the reason for a decision is
   genuinely not in the commits, the diff, the linked work item, or the code comments, say so under
   "For the reviewer" — a dispatched session has no one to ask; `h9k ask`/`h9k answer` are Slice 2
   and not built yet (AGENTS.md).

5. **Write the body** per "How it should read" below, then output the final title and description
   in a fenced code block so it can be copied or consumed verbatim.

## How it should read

**Audience**: a colleague reviewing this on GitHub who hasn't read the diff yet.

**The work-item line, conditional and first.** One line, `Work item: <url>`, only when step 2
found one — a task carries at most one external reference, so use whichever it carries; when
neither is present, the line is omitted entirely rather than left empty or guessed at.

**Then a sentence or two of orientation.** What this PR is and where it came from — a review, a
ticket, a bug someone hit, a field report. Get into it from there. No `## Summary` heading
restating the title.

**The change in prose, grouped by what a reviewer thinks about, not by commit order.** A run of
independent changes is a bulleted list; a single decision that needs justifying is a paragraph.
Group bullets by reviewer concern — the endpoints together, the data-shape change together, the
test changes together — rather than the sequence the commits happened to land in, which is rarely
how a reviewer reasons about the diff. One idea per bullet, written as a full sentence:
`Success responses now use ApiEnvelope<T> rather than the older ApiResponseType.`, not
`Envelope: ApiEnvelope<T>`. A telegraphic fragment isn't concision, it just moves the work of
reconstructing the meaning onto the reader.

**Spell out an acronym the first time it appears**, then use the short form afterward:
`the Pull Request (PR) queue`, then `PR` from there. Skip it for ones so ubiquitous spelling them
out reads as noise (API, URL, ID).

**Include the "why" inline, next to the change it explains, and only when it earns its place.** A
reason attached to the change it explains gets read; a reason parked in a `## Why` section three
paragraphs later doesn't. Skip it entirely when the change explains itself. Write it when the
change looks wrong without it, when an obvious alternative was rejected, when it prevents someone
"fixing" it back, or when it's subtle enough a reviewer wouldn't spot it unprompted.

**A `## For the reviewer` section**, when there's something worth flagging — this is often the
most valuable part of the description:
- **Things that will bite**: ordering constraints, a migration that must run first, a dependency on
  another PR, a follow-up deliberately left for later.
- **Deliberate omissions**, each with the card or issue that owns the rest of the work — not a
  vague "more to do here", but the actual tracking reference.
- **Judgment calls**, with the reasoning behind them, so a reviewer who'd have chosen differently
  can see why this one was made rather than re-deriving it from the diff.
- **A carried-forward residual note**, when replacing an already-open PR's body: whatever
  `**Left unfixed:**` or `Review ride-alongs:` paragraph (Decisions Log #87) the current body
  already carries survives into this section unchanged, per the note at the top of this skill.

Leave the section out entirely only when none of the four apply — including no carried-forward
residual note to place; an empty or padded heading isn't worth the reviewer's scroll.

**A short provenance note, plus the run/token footer — the note composed from what you actually
know, the footer carried forward, never invented.** Both only apply to the second legitimate use
above, replacing the body on an already-open Hall9k PR: that is the only case where you have both
a task id (from `task.md`) and an existing footer to read back.
- The provenance note verbatim, with the task id you were actually given (from `task.md`'s
  frontmatter, or the caller's own context): "Composed by an agent session from Hall9k task `<id>`."
  Compose it yourself — the daemon never writes one, on a fresh PR or an already-open one, so there
  is nothing to read back here, only the task id itself, which you do know and state plainly rather
  than guess at. It says the work was agent-assisted and points at where to find the full record
  (`h9k task show <id>` / `h9k logs <id>`), and stops there. It never narrates what the session did
  step by step, and never reproduces build or test output.
- The footer line — ``Hall9k run `<run-id>` · <tokens> tokens`` — only when replacing an
  already-open PR's body: read it back from the current body (`gh pr view <number> --json body -q
  .body`) rather than composing a new one. Unlike the provenance note, this one you cannot compose
  yourself even with the run id in hand: `PullRequestBody.Build` sums the run's own token
  accounting at the moment the daemon opens the PR, while the run is still live, so the figure is a
  snapshot that goes stale as the same PR gathers later review and follow-up work (draft task
  `9f6284bc` names the field incident this caused: a footer that kept naming the original run and
  its original token count through twelve later generations). Carry the existing figure forward
  rather than trying to recompute it, reformatting it with underscore separators (`18_401_309`
  rather than the daemon's own ungrouped `18401309`). This reformatting is this skill's own
  convention, not yet the platform's: the daemon's PR-open formatter and `h9k status`'s own
  spend-pressure line render token counts differently from each other today (ungrouped and
  comma-grouped, respectively), and unifying them is draft task `9f6284bc` — underscore-grouping
  here is a deliberate deviation from both until that lands, not a claim that either already
  matches it.

Omit both for the first legitimate use above, a PR opened entirely outside Hall9k's own dispatch —
there's no task id to compose the note from and no existing footer to carry forward.

## Keep out

- **Section scaffolding for its own sake.** No `## Summary` / `## Key Changes` / `## Technical
  Details` skeleton imposed on every PR. Use headings when the PR is big enough to need
  navigating, and name them after the actual content.
- **The acceptance-criteria checklist, restated.** The task's criteria are Hall9k bookkeeping, not
  something a reviewer checks off against the diff.
- **Run narration.** No walkthrough of what the agent did and in what order — that's what the
  provenance note replaces.
- **A build and test transcript.** "Tests pass" belongs in CI, not the description. Mention
  coverage only when it's the point of the PR, and then briefly.
- **A section per commit.** Describe the change as a whole, grouped by reviewer concern.
- **Restating the diff.** Don't walk file by file. If a file needs explaining, explain the
  decision in it, not its existence.
- **Padding.** No "This PR aims to…", no re-summarizing in a closing paragraph, no per-item
  significance ("this is important because…").
- **Any reference to Claude, Claude Code, or AI assistance beyond the provenance note above.** No
  `Co-Authored-By`, no "Generated with". The provenance note names the run, not the model.

## Example

The shape end to end: title, work-item line, orientation, grouped bullets with inline whys, a
reviewer section, and, as when replacing an already-open PR's body, a composed provenance note plus
the footer carried forward from the PR being replaced. Also note the house style this skill and
every other authored PR description follows: no em dashes (`~/.claude/CLAUDE.md`), commas or
periods or parentheses instead.

```
Title: Compose PR bodies a colleague would write, not a run transcript

Work item: https://github.com/Hallmanac/hall9k/issues/184

PR #1990's daemon-composed body read as an agent transcript: title restated, the
acceptance-criteria checklist, a run narration, then build/test output. This PR is the fix: the
canonical pr-summary skill now composes a body a colleague would write instead.

Most of the change is to the skill's own instructions:

- The process gathers the linked work item before anything else, so the body can lead with it
  instead of burying it in a footer.
- The "How it should read" section replaces the generic Summary/Why/Key Changes scaffolding with
  orientation-first prose grouped by reviewer concern, matching the house style in
  `~/.claude/commands/git/pr-description.md`.
- A new `For the reviewer` section carries the things worth flagging by name, rather than leaving
  them implicit in the diff.

The provenance note stays short by design (a pointer, not a transcript), because the run record
already holds the full one.

## For the reviewer

This only changes the skill's own text; nothing in `PullRequestBody.cs` (the daemon's automatic
PR-open formatter) changed, so the two can drift out of visual sync until that formatter also
underscore-groups its token counts, which draft task `9f6284bc` owns.

---
Composed by an agent session from Hall9k task `f99153c9`.
Hall9k run `01a06cb3-caf6-76c5-a899-105d1fb07e62` · 42_017 tokens
```

## Title

One line, describing the change rather than the activity. Include the ticket if the commits or
branch name carry one, in the usual form (`ABC-1234: <title>`). Prefer what the change does over
what was done to the code: `Send mass messages from the configured specialist address` over
`Refactor MassMessagingService`.
