===heading===
# Read-only task: compose this project's run skill
===read-only-declaration===
This session is read-only and composes text. Do not commit, push, branch, or
change a single file in this worktree, and do not install anything, start a
server, run a migration, or touch a database: you are reading a repository and
writing a description of how somebody else would stand it up, not standing it
up yourself. Inspection is what you have — reading files, `git log`, `git
rev-parse`, `ls`, and the like. You do not write the answer anywhere either;
you report it in your summary and the daemon records it. There is no file for
you to save and no ledger for you to write.
===context-heading===
## The project
===context===
- Project: {{ProjectName}}
- Repository worktree you are reading: `{{WorktreePath}}`
- Commit this composition is against: `{{HeadCommit}}`
- Base branch: `{{BaseBranch}}`
===evidence-heading===
## What a mechanical scan already found
===evidence-intro===
The daemon scanned this repository before starting you, so you do not have to
go looking for these. Read them. The scan sorted them by what it guessed they
were, and its guess is a starting point you are expected to overturn wherever
the files themselves disagree: a file listed as launch coverage may turn out to
describe a deployment nobody can reproduce locally, and a file listed as
ordinary documentation may turn out to be the only place the launch command is
written down.
===evidence-launch-heading===
Files the scan read as already covering how to launch this project:
===evidence-documentation-heading===
Other documentation the scan found:
===evidence-build-heading===
Build and run manifests the scan found:
===evidence-empty===
(nothing in this category)
===evidence-widen===
The scan is deliberately shallow: the repository root, `docs/`, and
`.claude/skills/`. If what you read points somewhere it did not look — a
`scripts/` directory, a nested service's own README, a compose file under an
`infra/` directory — go and read that too. Cite whatever you actually read.
===shape-heading===
## Your first decision: pointer or full text
===shape-choice===
Decide which of two shapes this run skill is, and say so in the trailer. This
is your call, made from the files, not from the scan's guess:
===shape-pointer===
- **pointer** — this repository already carries skills or documentation that
  cover launching it. The run skill you compose points at them by path and adds
  only what they leave out. Do not restate what those files already say: a
  reader can open them. What you add is what they assume and never state (the
  tool versions, the environment variable nobody wrote down, the step that only
  works after some other step), and the order to do them in.
===shape-full-text===
- **full-text** — this repository carries no such coverage. The run skill you
  compose holds the whole procedure, and every step cites the file you derived
  it from, in parentheses at the end of the step, as a repository-relative path
  (for example: `(docker-compose.yml)`). A step you cannot cite is a step you
  are guessing at; see the human-steps rule below for where a guess actually
  goes.
===shape-boundary===
Do not choose between the two on how much documentation exists. Choose on
whether a reader following the repository's own files, by themselves, would get
this project running. If they would, it is pointer. If they would not, it is
full-text, however many documents the repository has.
===document-heading===
## The document to compose
===document-intro===
Every project's run skill in this platform carries the same six sections, in
this order, with these exact headings, so somebody standing up a project they
have never seen reads the same document every time. Use `##` for each. A
section whose honest content is "none" still gets its heading and says so:
===document-sections===
1. `## Prerequisites` — what has to already be on the machine (runtimes,
   tool versions, a container runtime, an account), and how to check each one.
2. `## One-time setup` — everything done once per machine or per clone, in
   order: restore, install, seed, generate, configure.
3. `## Launch` — the command or commands that actually start it, with the
   directory each is run from. If there is more than one thing to start, say
   which order and whether each blocks.
4. `## How to know it is up` — the observable signal. A log line, a health
   endpoint and what it returns, a port that starts listening, a window that
   appears. Something a reader can check, never "wait a bit".
5. `## Address or entry point` — where the reader goes to actually use it: a
   URL and port, a CLI binary's path, a socket, a dashboard. For a library
   with no entry point of its own, say that and name what does exercise it.
6. `## Human steps` — see below. Never omitted, even when it is empty.
===human-steps-heading===
## The human-steps rule, which is the one that matters most
===human-steps===
Anything you could not determine goes under `## Human steps`, with what is
needed, and is never guessed at. A secret or credential you have no value for.
A login to a service. An account somebody has to be added to. A service you
could not reach and so could not confirm. A step the repository implies but
never states. Write each one as what the reader has to obtain or do, and where
it goes once they have it.

A plausible-looking value you invented is worse than an admitted gap: the
reader trusts this document precisely because it distinguishes the two. If you
find yourself writing a connection string, a port, or a command you did not
read somewhere in this repository, stop and move it to human steps instead.

Empty is a legitimate answer for this section, and so is "everything": a
repository whose launch story is entirely undocumented produces a run skill
that says so plainly rather than one that invents a procedure.
===quality-heading===
## What good looks like
===quality===
- Commands are copy-pasteable and say what directory they run from.
- Anything version-specific names the version the repository itself pins,
  with the file it is pinned in — never a version you happen to know.
- Platform differences (a command that differs on Windows) are named where
  the repository names them, and not invented where it does not.
- Every path in the document is repository-relative. This skill is read on
  every member's machine, not only this one, so the worktree path above and
  anything else rooted in this machine's own filesystem belongs nowhere in it:
  a reader elsewhere has that repository at a different path entirely.
- The whole document is under {{MaximumLength}} characters. A reader reads all
  of it before doing anything, so length spends their attention. Point at
  files rather than quoting them.
===trailer-heading===
## How to report what you composed
===trailer-contract===
End your summary with these lines, in this order, as the last thing you write.
This is a fixed contract the daemon parses mechanically: do not paraphrase the
marker text, and do not wrap the markdown in a code fence.

```
{{ShapeMarker}} pointer | full-text
{{MarkdownMarker}}
## Prerequisites
...the whole document, every section, plain markdown...
```

Everything after the {{MarkdownMarker}} line, to the end of your summary, is
taken verbatim as the run skill. Do not add a sign-off after it. Do not write
the shape as a first line of the document itself; the daemon writes that line
from the trailer above, so a document that opens with one just has it stripped.

A trailer the daemon cannot read, or a document missing one of the six
headings, is recorded as a failed discovery rather than guessed at, and a human
has to ask for discovery again. Getting the trailer right is the one
mechanical thing this session owes.
===closing===
- Report nothing else after the document. The daemon takes it from here.
