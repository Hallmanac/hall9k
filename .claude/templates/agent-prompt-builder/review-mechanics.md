===checkout-description===
- You are in the implementation's git worktree on branch `{{Branch}}`.
===final-full-pass-lead===
  This is the mandatory full-rigor pass immediately before the pull request opens
  (Decisions Log #92). An earlier full-scope pass on this run already read every
  commit up to `{{FullScopeSha}}` fresh — its findings and dispositions stand for
  that range, and you are not re-litigating them.
===final-full-pass-acceptance-criteria===
  The same goes for the acceptance
  criteria above: that earlier pass already judged them against the branch up to
  `{{FullScopeSha}}`, so judge them against the whole branch at HEAD, not against this
  scoped range alone — a criterion the earlier commits already satisfy is met, even
  though this range's own diff does not implement it.
===final-full-pass-range===
  Read only what has not yet had a
  fresh full-scope look: `git diff {{FullScopeSha}}..HEAD` (commits:
  `git log {{FullScopeSha}}..HEAD`). If that range is empty, nothing has landed since
  the last full-scope read — that is a legitimate {{MergeReadyWord}} outcome; say so rather
  than inventing scope to fill the pass. If this branch brought `{{BaseBranch}}`
  current via a merge (rather than a rebase) since that earlier pass, this range
  will include those upstream commits too — check a finding there against
  `git diff {{ScopeBoundary}}...HEAD` (the scope rule below) before treating it
  as this branch's own work. That same command is also what decides scope for you,
===final-full-pass-no-fork-point===
  so fall back to the local `{{BaseBranch}}` ref only when this worktree carries no
  `origin/{{BaseBranch}}` at all: a task worktree's local base-branch ref, when one
  exists, is shared with the project home's `dev/` worktree and is routinely stale
  relative to this task's actual base.
===discovery-lap-lead===
  This is a follow-up lap on a pull request that already cleared the full review
  chain up to `{{LapSinceSha}}` — every commit up to there was read fresh by an earlier
  run before it was pushed. This cycle's job is the lap's own change: read only what
  it added, `git diff {{LapSinceSha}}..HEAD` (commits: `git log {{LapSinceSha}}..HEAD`).
===discovery-lap-acceptance-criteria===
  The same goes for the acceptance
  criteria above: an earlier run already judged them against the branch up to
  `{{LapSinceSha}}`, so judge them against the whole branch at HEAD, not against this
  lap's own range alone — a criterion the earlier commits already satisfy is met,
  even though this range's own diff does not implement it.
===discovery-lap-scope===
  A defect you notice outside that range is still worth reporting — decide its scope
  by the same rule as everything else (below): code an earlier lap of this same
  branch added is still in-scope, since it sits inside `git diff {{ScopeBoundary}}...HEAD`
  — report it in-scope even though it falls outside this cycle's own read range. Only
  a defect that predates this branch entirely, genuinely pre-existing on `{{BaseBranch}}`,
  is out-of-scope.
  If this branch brought `{{BaseBranch}}` current via a merge (rather than a rebase)
  since the previous lap, this range will include those upstream commits too — check a
  finding there against `git diff {{ScopeBoundary}}...HEAD` (the scope rule below)
  before treating it as this lap's own work. That same command also decides scope for
===discovery-lap-fork-point-lead===
  you,
===discovery-lap-no-fork-point===
  you, so fall back to the local `{{BaseBranch}}` ref only when this worktree carries no
  `origin/{{BaseBranch}}` at all.
===stacked-diff-range===
  The diff under review: `git diff {{ScopeBoundary}}...HEAD` (commits:
  `git log {{ScopeBoundary}}..HEAD`).
===ordinary-diff-range===
  The diff under review: `git diff origin/{{BaseBranch}}...HEAD` (commits:
  `git log origin/{{BaseBranch}}..HEAD`). Fall back to the local `{{BaseBranch}}` ref only
  when this worktree carries no `origin/{{BaseBranch}}` at all: a task worktree's local
  base-branch ref, when one exists, is shared with the project home's `dev/` worktree and
  is routinely stale relative to this task's actual base.
===report-verified-findings===
- Report verified findings only. For every suspected defect, read the surrounding
  code until you can confirm it is real; discard anything you cannot confirm.
- Each finding must carry: the file and line (`path/to/file.cs:123`), a one-sentence
  statement of the defect, and a concrete failure scenario (the input or state that
  makes it misbehave, and what goes wrong).
- Do NOT modify files, commit, push, or open pull requests. You are read-only.
===no-build-foreign===
- **Do NOT build, test, or run anything that writes into this worktree.** This is
  someone else's already-open pull request, not this task's own diff to fix — there
  is nothing here for a build or test run to verify, only to disturb.
  Reading, searching, and read-only git are what this pass is made of. This session
  ends at your final message — nothing runs after it, so the same rule that keeps a
  build or fix session from backgrounding a gate applies here too, for anything else
  you run:
===no-build-ordinary===
- **Do NOT build, test, or run anything that writes into this worktree.** Another
  review pass reads this same directory during this cycle — at today's session
  cap, possibly at the same time as you. Two builds sharing one `obj/` and `bin/`
  fail each other with file-in-use errors, and a platform collision reported as a
  finding costs the cycle a fix run it needed for a real defect.
  Reading, searching, and read-only git are what this pass is made of. This session
  ends at your final message — nothing runs after it, so the same rule that keeps a
  build or fix session from backgrounding a gate applies here too, for anything else
  you run:
