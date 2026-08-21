---
project: hall9k
type: feature
objective: Review findings carry severity and scope, the review loop converges on a severity gate after three full cycles, and out-of-scope findings become draft bug tasks instead of PR scope creep
criteria:
- Every review finding (both lenses, backlog 34) carries a severity - high (correctness, security, or data-integrity defect reachable in realistic use), medium (real defect with bounded or unlikely impact, or a doctrine violation that misleads without corrupting - teaching-error standard, never-guess), low (polish - wording, consistency, a better idiom) - with those anchors stated in the review prompt, never left to the reviewer's intuition
- Every finding carries a scope tag with a mechanical anchor: in-scope if the defective line lives in code this branch added or changed, out-of-scope if the defect is pre-existing on main; the reviewer tags it and the tag is checkable against the diff
- Convergence rule: cycles 1 through 3 run the pure loop (any findings force fix then full fresh-context re-review); from cycle 4 onward a review with no high-severity findings is the terminal review - its fixes are applied, gates run, and the loop ends without another review pass. A fully clean review is MergeReady at any cycle, exactly as today
- MaxAutomaticReviewFixRuns rises from 2 to 7; parking remains for its true cases only - high-severity findings still appearing at budget exhaustion, disputed findings, and verdict failures
- Out-of-scope findings route by severity: high severity gets fixed in this PR anyway, in its own commit so the authored history keeps it separable (the cleanup-as-you-touch rule applied by machinery); medium and low are NOT fixed in this PR - each becomes a draft bug task carrying full provenance (originating task, PR, finding text, severity, lens) and the finding is recorded as routed, never silently dropped
- The draft bug task is created by the daemon from the reviewer's structured findings, not by an agent running task add - provenance recorded by machinery, per the observation-gates doctrine; the draft is inert until a human publishes it, so the funnel decides its fate
- The review events record severity and scope per finding, so the history can answer which severities and which lens actually forced cycles
- dotnet build and dotnet test pass
---
Brian's design (2026-08-21, orchestrator session), refined over the adversarial-loop
discussion while backlog 34 was in flight. Two motivating observations from the same
day: the conformance-only loop at budget 2 parks work that would converge at cycle 3
or 4, and the first manual adversarial pass on PR #21 surfaced findings in
pre-existing code that no scope rule existed to route.

Design constraints:
- Builds directly on 34's two-lens structure and touches the same ReviewEngine and
  prompt-builder files - queue behind 34, never alongside.
- The severity gate deliberately does NOT apply in cycles 1-3: early cycles get full
  rigor while the code is still converging; the gate handles the nit-churn tail.
  Considered and rejected: gating from cycle 1 (faster, but terminal fix sessions
  would ship un-re-reviewed before the code had survived a single full pass).
- The terminal fix session's changes ship without another review pass by design.
  Accepted because terminal fixes are by construction small (enumerated low/medium
  items), gates still run, and two backstops remain behind the internal loop:
  Copilot on the open PR and the human at the merge gate.
- Severity disagreements ride the existing dispute lever: a fix run that disputes a
  finding (or its severity) as wrong parks for the human, same as today.
- The daemon-created bug drafts need an owner and project: inherit both from the
  originating task. If draft creation fails, the finding is still recorded as
  routed-but-uncreated; a review loop may never fail because a side task could not
  be filed.
