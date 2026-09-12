===self-check-lead===
- **Self-check phase.** Once every finding above is fixed or disputed, and before
  you conclude, run one pass — not a loop — over your own fix: assume you left
  something half-applied, or that your own fix introduced a regression, and go
  looking for it the way a hostile reviewer would. Both have already escaped a fix
  session here and cost a full extra verify-plus-fix lap. Catching either yourself
  now is the more reliable check: the verify pass that would otherwise have to
  catch it may itself be running on the cheaper fix-role model rather than the
  review model, and a subtle half-fix or self-introduced regression is exactly
  the failure mode a cheaper model is least equipped to catch.
  This phase is one pass, not a loop: whatever is still merely suspected once it
  ends belongs in your final summary, not a second pass here. Say so plainly even
  though the next review dispatch is not always a verify pass, and only a verify
  pass reads a fix session's summary back — naming it is still the only chance
  that suspicion has of reaching a reviewer at all, not a guaranteed handoff.
===class-sweep-lead===
  1. **Class sweep, mandatory per finding.** For every finding you actually fixed
     this session — never one under "{{DoNotFixHere}}",
     which stays someone else's to fix — treat its stated line as one instance of
     its defect, not the boundary of it: enumerate every other site sharing the same
     shape, wherever it lives — inside this branch's own changes or pre-existing on
     the branch's base — not only the ones your own fix reaches; a sweep bounded to
     your own fix cannot catch a sibling site your fix never touched. Draw that line
===draw-line-stacked===
     from `{{SweepBoundary}}` — this branch's own recorded fork point off
     `{{EffectiveBaseBranch}}`, named as a literal commit rather than
     `origin/{{EffectiveBaseBranch}}` because this branch is stacked on that one and a
     force-push there moves the ref out from under the range, folding the parent's own
     rewritten delta into what would read as this branch's changes. Do not substitute
     the ref back in, and do not compute the boundary with `git merge-base`, which a
     force-push collapses too: a
===draw-line-unstacked===
     from `origin/{{EffectiveBaseBranch}}`, not your worktree's local base-branch ref —
     the same staleness reason the rebase and review-verify mechanics use it too: a
===site-classification===
     site touched by `git diff {{SweepBoundary}}...HEAD` is inside this
     branch's own changes; anything else is pre-existing on the base. Fix or
     explicitly clear each site inside this branch's own changes — a site you looked
     at and judged fine counts as cleared, one you never looked at does not. A
     pre-existing site outside this branch's own changes is not yours to fix here;
     fixing it would grow this pull request with unrelated changes the same way the
     disposition rule above forbids — unless this document itself separately
     dispositions that exact sibling site as a finding of its own, in which case an
     explicit disposition always beats the sweep's own default, regardless of which
     finding's sweep surfaced the sibling or how that finding is itself dispositioned:
     a sibling listed under "{{FixHereInItsOwnCommit}}" gets
     fixed here, in that same separate commit — that disposition has already decided
     this defect's shape is worth cleaning up now, and naming it instead of fixing it
     would cost exactly the lap this phase exists to remove; a sibling listed under
     "{{DoNotFixHere}}" stays routed away — a finding this
     document already routed away does not become yours to fix just because it shares
     a shape with one you are; and a sibling listed under "{{FixHere}}"
     or "{{RideAlong}}" is already your work by that listing
     alone, swept or not. A pre-existing site this document does not separately
     disposition is still fixed here, in that same separate commit, when the finding
     you are sweeping is itself dispositioned "{{FixHereInItsOwnCommit}}"
     — that disposition already decided this defect's shape belongs in its own commit,
     so an undispositioned sibling sharing that same shape belongs there too, rather
     than merely named. For every other pre-existing site — one this document does
     not separately disposition, swept from a finding that does not itself carry that
     disposition — it is still yours to name and not to fix: leave it
     out of your fix, but name it in your final summary anyway — if the next review
     dispatch is a verify pass, naming it there is the only path this sibling has of
     ever reaching a reviewer and being reported as a finding of its own; a
     pre-existing sibling left off the sweep never reaches even that path.
     Name every site you swept in your final summary — fixed, cleared, or
     pre-existing and named — and why each, so whoever reads it next can
     check your enumeration instead of rediscovering it from a blank slate.
===regression-comparison===
  2. **Regression comparison, mandatory per replaced behavior.** For every finding
     whose fix replaced, removed, narrowed, or widened existing behavior, state in
     your summary what the old code did that the new code no longer does, and confirm
     that difference is intended. A narrowing or widening you cannot justify that
     way is a finding against your own fix, not a note for later — fix it before
     you conclude, the same as any other real finding this phase surfaces.
===no-tests===
  3. **No tests to run.** This project configures no
     verification gates, so there is no suite to run here — move on
     rather than inventing a command to satisfy this sub-rule.
===run-touched-tests-lead===
  3. **Run the touched tests, in the foreground.** Run the tests that touch the
     code you changed and wait for them to finish before you conclude; do not
     background them, and do not skip this because the platform re-verifies after
     you finish — this phase exists so an escape is caught here instead of costing
     that separate lap. This project's own verification gates are:
===foreground-timeout-note===
     Request an explicit timeout up to the foreground ceiling stated above
     (`BASH_MAX_TIMEOUT_MS`, {{ForegroundCeilingMinutes}} minutes today): a foreground run left
     on a tool's short default timeout does not fail loudly, it dies mid-suite, and a
     session that notices tends to background the run instead and then end the
     session still waiting on a result nothing will ever {{DeliverWord}}.
===session-not-done===
- **The session is not done while `git status` shows anything modified, staged,
  or untracked.** Commit everything before your final message, including whatever
  this phase's own hunt just fixed — a completed fix left uncommitted is not a
  finished fix.
