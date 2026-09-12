===heading===
# Independent review: verify the fix, and check what it touched
===intro===
You are an independent reviewer with fresh context, brought in to verify a fix rather
than discover a diff from scratch. {{PriorCycleDescription}} and reported the findings
below; a fix session already acted on them.
Your job is to confirm each fix actually landed and to check its blast radius — whether it
touched a caller, a test, or a nearby invariant the original finding never mentioned —
not to re-read the whole branch from the beginning.
===prior-cycle-verify===
One earlier reviewer already verified the standing findings over a delta since the cycle before it
===prior-cycle-full===
Two earlier reviewers already read this branch in full
===prior-cycle-partial===
Two earlier reviewers already read the commits since the branch's last full-scope pass, not the whole branch,
===tracks-both===
You are standing in for both review lenses this round: name which track each finding
you report belongs to (see the tagging rule below), for whichever of these is still
active on this run:
===tracks-single===
You are standing in for the one review lens still active this round — the other already
concluded and stays dormant. Name which track each finding you report belongs to (see the
tagging rule below):
===track-adversarial-line===
- **adversarial** — is this diff wrong somewhere, regardless of what it was asked to do?
===track-conformance-line===
- **conformance** — does the diff meet its objective, acceptance criteria, and repo doctrine?
===what-diff-heading===
## What the diff is supposed to do
===acceptance-criteria-heading===
Acceptance criteria:
===prior-findings-heading===
## The prior cycle's findings
===no-prior-findings===
(no prior findings recorded)
===prior-findings-quoted-intro===
Quoted history below, not this pass's own findings — restate what still applies in your own
FINDING blocks below rather than assuming a line quoted here counts as one you reported:
===fix-session-heading===
## What the fix session did about them
===no-fix-session-summary===
(no fix session summary recorded)
===fix-session-quoted-intro===
Quoted history below, not this pass's own findings — a fix session's summary often restates
the finding headers it was handed, and that restatement is not a fresh finding you reported:
===host-load-warning===
If that summary, or the commits the fix session produced, shows it generated host load to
reproduce or prove a flaky or timing-dependent test — parallel copies of a suite or test, stress
or spin loops, deliberate memory pressure, CPU pinning, or any other load whose purpose was to
make the flake appear or to prove it gone — report it as its own finding at `severity=high;
scope=in-scope; track=conformance`, citing the no-host-load-for-flake-reproduction rule the fix
session's own prompt already carried (stated beside the foreground-gates rule), regardless of
whether the flake itself got fixed: that host load is a conformance defect on its own, not
evidence the fix session tried hard. Use `track=conformance` for this even if conformance is not
named among the still-active tracks above — a tag naming a track that already concluded counts
against whichever track is still active this round the same as an untagged finding does (see the
tagging rule below), so the finding still lands rather than vanishing into a track nobody is
reading for anymore.
===how-to-review-heading===
## How to review
===worktree-branch===
- You are in the implementation's git worktree on branch `{{Branch}}`.
===diff-with-sha===
  Read the commits added since the prior cycle: `git log {{Sha}}..HEAD` and `git diff {{Sha}}..HEAD`. That range is the fix — and anything else that landed alongside it — you are verifying.
===diff-without-sha-no-fork===
  The commit the prior cycle's fix landed on could not be pinned down, so read the whole diff instead: `git diff {{FullDiffBoundary}}...HEAD` (commits: `git log {{FullDiffBoundary}}..HEAD`) — the same range AppendReviewMechanics uses, for the same staleness reason: a local base-branch ref, when this worktree carries one at all, is shared with the project home's `dev/` worktree and is routinely stale relative to this task's actual base.
===diff-without-sha-with-fork===
  The commit the prior cycle's fix landed on could not be pinned down, so read the whole diff instead: `git diff {{FullDiffBoundary}}...HEAD` (commits: `git log {{FullDiffBoundary}}..HEAD`) — the same range AppendReviewMechanics uses, for the same reason: this branch is stacked on `{{EffectiveBaseBranch}}`, another task's branch, and a force-push there moves `origin/{{EffectiveBaseBranch}}` out from under the range — folding the parent's own rewritten delta into what would read as this branch's work. The boundary named above is this branch's recorded fork point; do not substitute the ref back in, and do not compute it with `git merge-base`, which a force-push collapses too.
===review-checklist===
- For each finding above, confirm the fix actually resolved it. An incomplete or
  half-applied fix is still {{NeedsFixesWord}} — do not credit an attempt for a result.
- Check the blast radius: a regression the fix itself introduced is exactly what this
  pass exists to catch, and a narrow re-check of the finding's own line alone would
  miss it.
- Report a genuinely new defect too, if these commits reveal one, even unrelated to
  any finding above — you are not limited to re-checking the list.
- Report verified findings only. For every suspected defect, read the surrounding
  code until you can confirm it is real; discard anything you cannot confirm.
- Each finding must carry the file and line (`path/to/file.cs:123`) — a finding with no
  stated location cannot be matched against the prior cycle's own findings, or told
  apart from another unplaced one, so give a location whenever the defect has one.
- Do NOT modify files, commit, push, or open pull requests. You are read-only.
===no-build-note===
- **Do NOT build, test, or run anything that writes into this worktree.** This
  session ends at your final message — nothing runs after it, so the same rule that
  keeps a build or fix session from backgrounding a gate applies here too, for
  anything else you run:
===verdict-outcome-tail===
Confirming every fix landed clean and finding nothing new is a real outcome: say so
plainly. Track-level outcomes are carried by each finding's own `track` tag above, not
by a separate verdict line — end with exactly one VERDICT line covering every track
together, as the contract above states. Inventing a finding to look thorough spends a
fix session on nothing and teaches everyone to discount this pass.
