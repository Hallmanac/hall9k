===heading===
# Read-only task: assess a stacked git shape before the daemon acts on it
===read-only-declaration===
This session is read-only. Do not rebase, push, or commit anything, and do not
run any command that changes a ref, a working tree, or a remote — `git status`,
`git log`, `git rev-parse`, `git merge-base`, `git show`, `gh pr view`, and
similar inspection commands only. The daemon is the one that rebases, pushes,
or dispatches a fix session afterward, using the verdict you report below.
===context-heading===
## What is being assessed
===context===
- Child branch: `{{ChildBranch}}`
- Parent branch: `{{ParentBranch}}`
- This run's recorded fork point (the boundary the platform believes this
  branch was cut from, or last replayed from): `{{RecordedForkPoint}}`
- The commit a mechanical replay most recently attempted to land onto, or was
  about to attempt: `{{AttemptedOntoCommit}}`
- Pull request: {{PullRequestNumber}}, base as GitHub itself reports it:
  `{{PullRequestBase}}`
- Park kind this would otherwise park the run for: `{{ParkKind}}`
- What the mechanical checkpoint or pre-final-pass rebase actually hit, in its
  own words: {{ParkText}}
===task-heading===
## Your job
===task===
The mechanical checkpoint above could not decide this on its own and would
otherwise park it for a human right now. Before it does, inspect the real
state of this worktree and GitHub and decide which of three shapes this
actually is:
===shape-aligned===
- **Aligned.** The branch already sits on the commit it should — nothing
  needs to move. This is the shape when the recorded fork point and the
  branch's own commits already account for everything the parent or base
  branch has merged, even if the platform's own recorded fields have gone
  stale (a retarget, a rename, a rebase that landed under a different run).
===shape-replay===
- **Replay.** The branch's own commits need to be replayed from a boundary
  commit onto a different commit than the one recorded — mechanically, with
  no judgment call inside the replay itself. Name the exact boundary (the
  commit this branch's own work starts after) and the exact onto commit (the
  commit to replay onto), both as full SHAs you have personally verified
  exist and are reachable, not commits you are inferring from a branch name
  alone.
===shape-undecidable===
- **Undecidable.** Neither of the above can be said honestly — the history
  was rewritten in a way that makes the boundary ambiguous, two candidate
  boundaries disagree, or something you needed to read (a branch, a ref, a
  pull request) could not be read at all. Say plainly what you could not
  resolve and why; do not guess at a boundary or an onto commit you cannot
  verify.
===verification-heading===
## How to verify each shape
===verification===
Use git and gh directly; do not trust the context above as more than a
starting point for what to check:
- `git fetch origin` first, so every ref you read next is current.
- `git log --oneline {{ChildBranch}} ^{{RecordedForkPoint}}` (or the
  equivalent range against a candidate boundary) to see exactly which commits
  this branch would carry into a replay.
- `git merge-base --is-ancestor <commit> <other>` to check containment before
  trusting any commit as a boundary or an onto target — never substitute a
  computed merge base for a containment check.
- `git branch -r --contains <commit>` and `git log --oneline <commit>` to
  confirm a candidate onto commit is really the tip of the branch you think
  it is, not just some commit that happens to be reachable from it.
- `gh pr view {{PullRequestNumber}} --json baseRefName,state,mergedAt,url` (and
  the same for the parent's own pull request, if it has one) to read the
  actual, current GitHub state rather than trusting any locally recorded base.
- If the parent branch is gone from `origin`, check whether its pull request
  merged and where (`gh pr view <parent-pr> --json baseRefName,mergedAt`)
  before concluding anything about where this branch should land.
===trailer-heading===
## How to report your verdict
===trailer-contract===
End your summary with these lines, in this order, as the last thing you
write (above the HANDOFF block if this session's own instructions include
one). This is a fixed contract the daemon parses mechanically — do not
paraphrase the marker text, and do not omit a line a verdict requires:

```
{{VerdictMarker}} aligned | replay | undecidable
{{BoundaryMarker}} <full commit SHA, or "none" only for undecidable>
{{OntoMarker}} <full commit SHA, or "none" only for undecidable>
{{EvidenceMarker}}
<one or more lines of evidence: the exact git and gh commands you ran and
what they showed, plain enough that a human reading it later — or a second
run, if this one's trailer is ever unreadable — can verify your conclusion
without re-doing the investigation themselves>
```

An aligned or a replay verdict must carry a real `{{BoundaryMarker}}` and a
real `{{OntoMarker}}` commit, each one you have personally verified with
`git rev-parse --verify` or an equivalent read — never a commit you are only
assuming exists. A verdict missing either, or carrying no
`{{EvidenceMarker}}` block at all, is read as malformed and treated as
undecidable regardless of what you intended, so a trailer that cannot stand
on its own is the same as not reporting one.
===closing===
- End with a short summary of what you found, then the trailer above.
