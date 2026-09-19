---
name: pr-summary
description: Generate a pull-request title and description from the commits on the current branch. Primary use is a build session's own closing PR SUMMARY block, which the Hall9k daemon puts verbatim into the pull request it opens; the second use is replacing an already-open PR's body. A repo that ships its own PR-description rule wins for the prose. Output is text only; it never opens the PR.
---

# PR Summary Generator

Write the description for a pull request: what changed, and why where the why is not obvious. Write
it the way you would explain the change to a colleague who respects your time, sized to the change
rather than to the effort behind it.

The output is text and only text. **Never run `gh pr create` or `gh pr edit`, and never open a pull
request.** The Hall9k daemon opens pull requests (`PullRequestOpener`); agents are forbidden from
doing so.

## When this runs

**The primary use is a build session's own closing `PR SUMMARY:` block** (Decisions Log #163). The
headless build prompt's last step asks for it, and the daemon puts what you write straight into the
pull request it opens: your title becomes the pull request's title, and your body is the pull
request's body. Write it into the final message under a line reading exactly `PR SUMMARY:`, placed
before the `HANDOFF:` block, with `Title: <one line>` as its first line, a blank line, then the
body.

**The second use is replacing the body on an already-open pull request**, or drafting one for a
pull request opened outside Hall9k's own dispatch. Hand the drafted text back for a human to apply;
this skill never edits a pull request itself. Read the current body first
(`gh pr view <number> --json body -q .body`) so the replacement keeps whatever a human added to it
by hand. An older Hall9k pull request may still carry the parts the platform used to append, a
`**Left unfixed:**` or `Review ride-alongs:` paragraph and a ``Hall9k run `<id>` · <tokens>
tokens`` footer; those do not survive into the replacement. They are recorded on the run and
`h9k task show` reads them back, so dropping them loses nothing and keeps the rewritten body the
same shape as one the platform would open today.

## Whose rules win

**A repository's own PR-description rule wins for the prose.** When the target repository ships
one, take the voice, the section shape, and the title convention from there, and use this skill
only for what that rule does not say. Two places count: a command file at
`.claude/commands/git/pr-description.md` (arx-platform's is the known case), and a PR-description
or `pr-summary` skill under the repository's own `.claude/skills/`. Only when the repository ships
none is this skill's own "How it should read" the whole answer. When you also find an owner voice
below, that rule keeps the structure, the shape and the title convention and the voice takes the
sentences; the two never decide the same thing.

**The owner's own voice wins for the sentences.** Find it in this order:

1. The voice skill the owner named with `h9k owner set --voice-skill <name>`. When they named one,
   the prompt that asked you for this block names it too.
2. Failing that, a skill called `my-voice`: first in the owner's own user skills directory
   (`~/.claude/skills/my-voice`, which a dispatched session can already see), then in the target
   repository's own `.claude/skills/my-voice`.

Load whichever you find together with its code-review context (`contexts/code-review.md` in the
reference skill), or whichever context it carries for a pull request description, before you write
a word, and write in it: plain language, high-level concepts, complete sentences, the way the owner
explains a change to a colleague. That settles the sentences and nothing else. The repository's own
PR-description rule still decides the structure, and the project's writing conventions still govern
the mechanics.

**When neither tier has a voice skill, say so in one line of your final summary** and write the
body in the plain colleague voice described below. Do not invent a voice, and do not fall back to
an assistant's register.

## What the platform adds

One line, and only one: `Work item: [ARX-5817](https://…/browse/ARX-5817)` for a Jira card, or
`Work item: [#123](https://github.com/owner/repo/issues/123)` for a GitHub issue, as the body's
first line when the task carries an external reference. Leave it out of a build session's own
`PR SUMMARY:` block; the daemon writes it, and a copy of your own is dropped rather than shown
twice.

Nothing else is added. The acceptance criteria, the review residuals, the token accounting, and the
run pointer all stay in the run record where they already live, and the criteria stay on the card
(Brian's ruling, 2026-09-19, after bioage-calculator pull request #4 opened with a 3,837-byte body
for a 56-line file).

## Process

1. **Read the change, not just the commit subjects.** In parallel: the commit list and bodies
   against the base branch (`git log --oneline origin/main..HEAD` and `git log origin/main..HEAD`,
   with `main` as the base unless told otherwise), the file statistics
   (`git diff --stat origin/main..HEAD`), and the diff itself for anything the messages describe
   vaguely. A description assembled from commit subjects alone reads like a changelog and tells a
   reviewer nothing they could not get from the commit list.

2. **Find the linked work item, and only for the second use.** Check `task.md`'s
   `external-reference:` frontmatter line when this branch was cut for a Hall9k task, but not in
   the worktree itself: a task's worktree never contains `task.md`, which lives at
   `<project home>/tasks/<shortid>-<slug>/task.md`, a sibling of `repo/`. A dispatched session's
   own prompt names the project home under "Where this project lives". The value already says
   which kind it is: `jira:<KEY>` is a Jira card, `github:<owner>/<repo>#<number>` is a GitHub
   issue. When neither is present there is no work-item line at all; never invent one. A Jira key
   is not itself a URL, so resolve it as `<site>/browse/<KEY>` with `<site>` read from
   `h9k connection list`'s Site column rather than guessed. A GitHub reference maps directly:
   `github:<owner>/<repo>#<number>` is `https://github.com/<owner>/<repo>/issues/<number>`.

3. **Work out what a reviewer actually needs.** Two questions, in this order:
   - What does this change, described so someone who has not read the diff understands the shape
     of it?
   - What here would a competent developer be puzzled by, disagree with, or waste time
     re-deriving? That is the only "why" worth writing down.

4. **Name what you cannot determine, rather than inventing it.** If the reason for a decision is
   genuinely not in the commits, the diff, the linked work item, or the code comments, say so in
   one sentence where it belongs. A dispatched session has no one to ask; `h9k ask`/`h9k answer`
   are Slice 2 and not built yet (AGENTS.md).

5. **Write the body**, sized per the next section and shaped per the one after it. Output the
   final title and description in a fenced code block so it can be copied or consumed verbatim. In
   a build session, that block goes into the final message under the `PR SUMMARY:` line described
   at the top; the daemon reads it back from there and tolerates the fence.

## How long it should be

**The body is proportionate to the diff, not to the work behind it.** A reviewer's scroll is the
budget, and a long body on a short change costs them more than it tells them.

- **A one-file change, or a documentation-only one**: one or two sentences of orientation, plus
  only the judgment calls and the reviewer actions that actually exist, which is often neither.
  Three or four lines in total is a complete, finished body for one of these. See the worked
  example at the end.
- **A change spread across several files or concerns**: the orientation sentence, then the grouped
  bullets and the paragraphs described below, as many as the change actually has and no more.

Every element in the next section is conditional on having something to put in it. There is no
section you fill in because it is on a list, and a heading with one padded sentence under it is
worse than no heading.

## How it should read

**Audience**: a colleague reviewing this on GitHub who has not read the diff yet. Full sentences,
plain language, no filler. That is the whole rule; the rest of this section is what it means in
practice.

**Lead with a sentence or two of orientation.** What this pull request is and where it came from: a
review, a ticket, a bug someone hit, a field report. Then get into it. No `## Summary` heading
restating the title.

**Use bullets for lists of independent things, and prose for anything that needs an explanation.**
A run of five unrelated changes is a list; forcing it into paragraphs buries it. A single decision
that needs justifying is a paragraph; chopping it into fragments makes it unreadable. Most
descriptions want both: a short paragraph setting up a group of changes, then the bullets. Group by
what a reviewer thinks about rather than by the order the commits happened to land in, which is
rarely how a reviewer reasons about a diff.

**One idea per bullet, written as a sentence.** `Success responses now use ApiEnvelope<T> rather
than the older ApiResponseType.` Not `Envelope: ApiEnvelope<T>`. Telegraphic fragments are not
concision, they just move the work of reconstructing the meaning onto the reader.

**Spell out an acronym the first time it appears**, then use the short form afterward: `the pull
request (PR) queue`, then `PR` from there. Skip it for the ones so ubiquitous that spelling them
out reads as noise (API, URL, ID).

**Explain a bug as the sequence that triggers it, one bullet per step.** The fastest way to convey
a defect is usually the steps that reproduce it, and a reader should be able to tell whether it
affects them without reading the cause. Give each step its own bullet. Numbering is optional, since
the order is already implied by the list. This is the one place where breaking an explanation into
pieces makes it easier to read rather than harder, because a reproduction run together as prose
forces the reader to re-segment it themselves:

```
Open the note on the top message and start typing. Switch to another window
and come back: the conversation refetches on window focus, and if a colleague
replied while you were away, that reply is now the top message, so your draft
is sitting on it and saving writes the note there.
```

reads much better as:

```
- Open the note on the top message in a thread and start typing.
- Switch to another window and come back. The conversation refetches on focus.
- If a colleague replied while you were away, that reply is now the top message.
- Your half-typed draft is sitting on it, and saving writes the note there.

Nothing errors, and the note is now attached to a message you never opened.
```

Put the consequence after the list rather than in the last bullet. The steps are what the reader
follows; the outcome is what they were reading for.

**Include the "why" inline, where it belongs, and only when it earns its place.** A reason attached
to the change it explains is read; a reason parked in a `## Why` section three paragraphs later is
not. Skip it entirely when the change explains itself, since nobody needs to be told why tests were
added or why a typo was fixed. Write it when the change looks wrong without it, when you rejected
an obvious alternative, when it prevents someone "fixing" your code back, or when it fixes
something subtle enough that a reviewer would not spot it.

**Say what a reviewer has to do or watch out for, inline, and only when there is something.**
Ordering constraints, a migration that must run first, a dependency on another pull request, a
follow-up you deliberately left with the card or issue that owns it. Put it next to the change it
belongs to, or in a short closing section named after what it actually is (`## Before merging`)
when it stands apart from everything above it. There is no standing `## For the reviewer` heading:
a section that exists on every pull request gets padded on the ones that had nothing for it, which
is how a three-line change acquires a thirty-line body.

## Keep out

- **Section scaffolding for its own sake.** No `## Summary` / `## Why` / `## Key Changes` /
  `## Technical Details` skeleton imposed on every pull request. Use headings when the change is
  big enough to need navigating, and name them after the actual content.
- **A section per commit.** The commit list is already on the pull request. Describe the change as
  a whole, grouped by what a reviewer thinks about.
- **Restating the diff.** Do not walk file by file. If a file needs explaining, explain the
  decision in it, not its existence.
- **A build and test transcript.** "Tests pass" belongs in CI and in the run record, not in the
  description. Mention coverage only when it is the point of the change, and then briefly.
- **The acceptance criteria.** They are the task's contract, they live on the card, and a reviewer
  is reading the diff rather than checking boxes against it.
- **The handoff.** Whatever you are writing under `HANDOFF:` is for the next session, not for a
  reviewer, and a body that repeats it is the same text twice in one message.
- **Run narration.** No walkthrough of what the session did and in what order, and no token or run
  accounting. The run record holds all of it.
- **Padding.** No "This PR aims to…", no re-summarising in a closing paragraph, no per-item
  significance ("this is important because…").
- **Any reference to Claude, Claude Code, or AI assistance.** No `Co-Authored-By`, no "Generated
  with". This applies to the title and the body both.

## Two examples of a larger change

The same change, written badly and then well. The file and document names are one repository's own;
what carries over is the shape.

**Bad**, fragments pretending to be brevity, scaffolding, a transcript, and a "why" nobody asked
for:

```
## Summary
Fixes to mass messaging API.

## Key Changes
- Envelope: ApiResponseType -> ApiEnvelope<T>
- Errors: Problem Details
- Route: added audience segment
- Status codes: 422/503
- Skip reasons: value object
- SentOrderIds: json column
- Tests: 10 added

## Why
Consistency with the API standards. Improves maintainability.

## Technical Details
Modified MassMessagesApiController.cs, MassMessagingService.cs,
MassMessagingContracts.cs, MassMessageSkipReason.cs, and added
AlterMassMessageBatchSentOrderIdsToJsonMigration.cs.

## Verification
MSBuild succeeded on AgelessRx.Services, AgelessRx.Entities,
AgelessRx.Migrations, Web/Admin. VSTest 47/47 passed.
```

**Good**, orientation, bullets for the list, a paragraph for the one decision that needs it, and
the thing a reviewer has to know:

```
While reviewing #1834 after it merged I found a handful of things worth
cleaning up, and this PR is those changes.

Most of it is bringing the four endpoints in line with
`docs/architecture/api-standards.md`, that repository's own house standard:

- The actions were calling `.IgnoreCapturedContext()`, which drops the request
  context and is the one thing that standard treats as blocking, so those are
  gone.
- Success responses now use `ApiEnvelope<T>` rather than the older
  `ApiResponseType`.
- The route picks up its audience segment and becomes
  `rest/v1/admin/mass-messages`. The React service's paths follow along, since
  without them the page just 404s.

The more interesting fix is in the status codes. A send that got refused before
anything went out always came back as a 400, whether the operator could do
something about it or not. Telling someone their request was malformed when the
real cause is a bad app setting sends them off to re-check a list that was never
wrong. So a subject that isn't available for mass messaging is now a 422, and a
missing sender account or an unwritable audit row is a 503.

## Before merging

This won't build until #1841 is rebased onto main, since it calls
`ProblemDetails.UnprocessableContent`, which is on main but not yet on this PR's
base branch.
```

## One example of a small change

A single added file, documentation only. This is the whole body, and it is finished. No headings,
no criteria, no reviewer section, because there was nothing to put in one:

```
This adds an `AGENTS.md` at the root of the repo so that a fresh session (a
person or an agent) doesn't have to reverse engineer how this app builds and
runs by reading the solution. It's the one file to read first: what the app is,
the `nuget restore` and `msbuild` commands that build it, how to run it, where
things live, and the handful of things that look wrong but are deliberate.

While checking the approved draft against the code, I found three claims that
weren't quite right, so I corrected them in place rather than dropping them:

- The app doesn't need a database at start-up. Only the form submit, the stored
  result lookup, and the site health page open a connection, which means a boot
  failure is never the connection string.
- `packages\` is only partly ignored. Seventeen of the package folders were
  committed before the ignore rule existed, and an ignore rule doesn't apply to
  files git already tracks, so those stay in the repo and restore only fetches
  the rest.
- The `Site.css` cache-busting timestamp is read once at start-up in a release
  build, but per request under a debug build.

One thing I deliberately left alone: `nuget restore` reports NU1903 advisories
for `Newtonsoft.Json`, `RestSharp`, and `System.Text.Json`. Those are
pre-existing and need their own card, since bumping three packages on a docs
branch is the wrong place for it.
```

## Title

One line, describing the change rather than the activity. Prefer what the change does over what you
did to the code: `Send mass messages from the configured specialist address` over
`Refactor MassMessagingService`.

Include the external key when the commits or the branch name carry one, written in the same form
this repository's own commit subjects use. Read `git log` on the base branch and copy what you see:
`ARX-1234: <title>` where the subjects use a colon, and `ARX-1234 <title>` where they do not. Write
it in yourself rather than leaving it to the platform. For a Jira-referenced task the daemon puts
the key there when it is missing, idempotently, but a title you wrote the key into is a title in
your own wording throughout; one it had to repair is not.

A GitHub issue reference is never prefixed at all, by you or by the daemon: the work-item line
cross-references it, and a bare `#42` on a squash-merge subject is an issue link nobody meant to
make.

## House conventions

Every word of the title and the body follows the project's own writing conventions, which
`h9k project show` prints and `h9k project set <project> --writing-conventions` changes, and which
every prompt asking for a `PR SUMMARY:` block carries verbatim. Today that means no em dashes
(U+2014), with commas or semicolons or parentheses instead; full sentences over telegraphic
fragments; and no AI attribution anywhere. The platform re-checks the mechanical half immediately
before it posts, so an em dash that slips through is rewritten rather than published, but a body
that needed rewriting is a body that read as somebody else's.
