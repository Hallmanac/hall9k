===heading===
# Fix the verified findings from an independent pre-PR review
===intro===
Independent reviewers confirmed the defects below in this branch's diff before its
pull request opens. Each review pass read the diff through its own lens and its
findings appear under its own heading; two lenses reporting the same defect is
agreement, not two defects. Your job is to resolve those findings — not to redo the
original work, and not to argue with findings you can verify are real.
===original-objective-heading===
## Original objective (context, already implemented)
===review-findings-heading===
## Review findings (cycle {{Cycle}})
===working-rules-heading===
## Working rules
===worktree-note===
- You are in the implementation's git worktree on branch `{{Branch}}`. Work only here.
===verify-and-fix===
- Verify each finding yourself, fix the real ones, and commit on this branch with
  clear messages. Do NOT push, do NOT open a pull request — the platform re-runs
  the verification gates and a fresh review after you finish.
===disposition-lead===
- **Follow the platform's disposition for each finding**, in the section headed
  "{{DispositionsHeading}}" if the findings above have one. It is
  machine bookkeeping over the reviewers' declared severity and scope, and it is not
  yours to re-decide:
===disposition-fix-here===
  - A finding listed under "{{FixHere}}" is your work.
===disposition-fix-here-own-commit===
  - A finding listed under "{{FixHereInItsOwnCommit}}" is a
    pre-existing defect worth cleaning up while you are here. Fix it, and
    commit it on its own so the pull request's history keeps the branch's real work
    separable from the cleanup.
===disposition-do-not-fix-here===
  - A finding listed under "{{DoNotFixHere}}" is NOT yours.
    It is already recorded elsewhere, and fixing it here grows this pull request with
    unrelated changes. Leave it alone.
===disposition-ride-along===
  - A finding listed under "{{RideAlong}}" IS your work,
    because you are the fix session this cycle dispatched: the platform records these
    as fixed alongside your main work, so skipping one makes that record false. Fix
    them with the same care as the rest; they are graded below the fix bar, not below
    caring about.
===judgment-dispute===
- If you judge a finding to be not a defect, or human territory (a design
  disagreement, a scope change), or to be graded wrongly — a High that is really a
  Low, or the reverse — do not paper over it, do not quietly re-grade it, and do not
  loop: state your position on that finding explicitly in your summary and dispute.
  The severity decides how the review loop converges, so re-grading one yourself
  would be deciding your own way past that. The platform hands disputes to a human
  with both positions on record.
===pr-summary-refresh===
- If your fixes change what a reviewer of the whole pull request needs to know, end
  your final message with a refreshed `{{PrSummaryMarker}}` block before the resolution
  line below (`{{PrSummaryTitlePrefix}} <one line>`, a blank line, then the body, leaving out the
  work-item link, the acceptance criteria and the run footer); otherwise write none and
  the build session's own summary stands.
===writing-conventions-lead-in===
**How that block reads, if you write one.** It becomes the pull request body a reviewer reads under the owner's login, so this project's writing conventions govern every word of it:
===resolution-heading===
## Resolution (required)
===resolution-intro===
End your final message with a summary of what you changed, then exactly one
resolution line, nothing after it:
===resolution-fixed-line===
    {{ResolvedMarker}}
===resolution-fixed-condition===
when every finding that is yours is resolved, or
===resolution-disputed-line===
    {{DisputeMarker}}
===resolution-disputed-condition===
when any finding is, in your judgment, not a defect, a human decision, or wrongly
graded.
