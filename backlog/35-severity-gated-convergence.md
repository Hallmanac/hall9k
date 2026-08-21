---
project: hall9k
type: feature
objective: Split the pre-PR review into two simultaneous tracks - compliance and adversarial - with severity-graded adversarial convergence, a three-cycle compliance park rule, and out-of-scope findings routed to draft bug tasks
criteria:
- The review phase dispatches both tracks simultaneously after gates pass - the compliance review (today's conformance pass) and the adversarial review (the backlog-34 lens) - each with fresh context and its OWN cycle count; a track that comes back clean goes dormant and the other continues alone
- Every adversarial finding carries a severity - high (correctness, security, or data-integrity defect reachable in realistic use), medium (real defect with bounded or unlikely impact, or a doctrine violation that misleads without corrupting), low (polish) - with those anchors stated in the review prompt, never left to the reviewer's intuition
- Every adversarial finding carries a scope tag with a mechanical anchor: in-scope if the defective line lives in code this branch added or changed, out-of-scope if the defect is pre-existing on main; the tag is checkable against the diff
- Adversarial convergence: cycles 1 through 3, any finding of any severity is fixed and forces a fresh re-review; from cycle 4 onward only a high forces the next cycle - mediums and lows are still fixed, but do not trigger re-review; the cap is 10 cycles, and highs still appearing at the cap park the run with a reason (the human resolving the park may choose a fresh agent restart)
- Compliance convergence: no severity levels - a clean compliance review ends that track; a compliance review still returning findings after 3 cycles parks for the human (nothing automated is left to try)
- Dormant tracks stay dormant, deliberately: adversarial fix sessions after compliance went clean do not reawaken the compliance track. Accepted trade-off (Brian, 2026-08-21): compliance converges in 1-2 cycles in practice, fix sessions are small, and build/test gates plus the external reviewer and the human merge gate stand behind the loop - document it, do not fix it
- The terminal verdict stays MergeReady; the record carries how it was reached - Clean (a reviewer saw the final tip and found nothing) or Settled (the severity gate ended the loop) - with residual findings recorded (severity, scope, fixed-unreviewed vs routed) and displays distinguishing merge-ready (clean) from merge-ready (settled - N residuals fixed, M routed)
- Empty terminal case: a cycle-4+ adversarial review whose only findings are out-of-scope non-highs ends the loop immediately with no fix session - the findings route to drafts and the verdict records Settled
- Out-of-scope findings route by severity at every cycle: a high is fixed in this PR in its own commit (cleanup-as-you-touch applied by machinery, kept separable in the authored history); non-highs are NOT fixed in this PR - the daemon creates a draft bug task from the reviewer's structured finding (originating task, PR, finding text, severity, lens recorded by machinery per the observation-gates doctrine), inert until a human publishes it; every routed finding is recorded as routed, and a failed draft creation never fails the review loop
- A fix run disputing a finding OR its severity parks for the human, exactly like today's dispute lever - agents never self-downgrade their way past the gate
- Review events record track, cycle, severity, and scope per finding, so history can answer which severities and which track actually forced cycles
- The post-PR closeout reopen budget (Copilot threads, failing checks - attempt 1/2) is explicitly out of scope and stays at 2; h9k pr resolve remains its reset lever
- dotnet build and dotnet test pass
---
Brian's design (2026-08-21, orchestrator session), refined to twin tracks in the
refinement conversation the same day. Origin: the conformance-only loop at budget 2
parked work that would converge at cycle 3-4, and the first manual adversarial loops
on PR #21 (three rounds, 24 findings, two of them regressions introduced by earlier
fix rounds) proved both that the adversarial lens finds what compliance misses and
that repeated cycles converge - severities trended down every round.

Design constraints:
- Builds directly on 34's two-lens structure (merged 2026-08-21, PR #22) and touches
  the same ReviewEngine and prompt-builder files.
- The severity gate deliberately does NOT apply in adversarial cycles 1-3: early
  cycles get full rigor while the code is still converging; the gate handles the
  nit-churn tail. Cycle counts are per track, not shared.
- The 10-cap park is not failure - it is "the machine kept finding real problems;
  a human should look at what is going on", and the park reason should say exactly
  that. Restarting with a fresh agent is a resolution option, not an automatic.
- Terminal Settled fixes ship without another review pass by design; the residual
  record is what keeps the history honest about it.
- MaxAutomaticReviewFixRuns as a single knob dissolves into the per-track caps
  (adversarial 10, compliance 3); pick names that make the split legible in
  DaemonOptions.
