---
project: hall9k
type: bugfix
objective: Harden the pre-PR review loop against reviewer flakes, agent no-ops, and parked-run lease decay
criteria:
- A parked run holds its task - lease expiry on a ReviewParked (or CloseoutParked) run never requeues the task; the park itself keeps the lease refreshed or the requeue sweep explicitly skips parked current runs (origin incident: a review-parked task was requeued by lease expiry and the platform rebuilt the same feature from scratch across generations 2-4, roughly tripling the token cost, before gen 5 completed)
- The review prompt forbids ending without a verdict: the session must print the VERDICT line even when its checks are still running (wait, then conclude); a session that ends verdict-less gets ONE re-prompt in the same session before the engine parks (origin incident: the first live review ended with "I'll deliver findings and the verdict when it completes" - a promise, not a verdict - and parked a finished, correct implementation)
- h9k review resolve <task> unparks a review-parked run with a human verdict: --merge-ready proceeds to the PR, --needs-fixes <reason> dispatches the fix session; the deferred lever from decision #24, now proven needed (its absence left a parked run with no path forward except abandonment)
- A run whose agent session ends with zero commits on the branch fails fast with an honest reason ("agent produced no commits") before gates ever run, instead of passing gates on an unmodified tree and dying later at PR creation (origin incident: task 08's agent completed all its work uncommitted; gates passed vacuously and the failure surfaced two stages late as "No commits between main and branch")
- The retry and follow-up prompts tell the agent a previous attempt's work may already be present in the worktree and must be reviewed before starting over (the retained-worktree resume carries uncommitted stranded work by design)
- dotnet build and dotnet test pass
---
All four defects come from one origin story (2026-08-17/18): the first fully-automated
cycle, tasks 08 and 09. The review loop, closeout monitor, and retry lever all worked
as designed; what failed was everything around agent output shape and lease lifetime.
The pipeline trusted what it should verify.

Design constraints:
- The parked-lease fix is the priority: it is the only one that multiplies cost.
  Decision #24 already claims "a parked run keeps its task Claimed and its lease alive
  (adoption refreshes the heartbeat)" - the claim was false in practice; make it true
  and note the correction in the decisions log.
- One re-prompt, then park. Never loop on a verdict-less reviewer (log #11 spirit).
- h9k review resolve records the human verdict as an event with the reason; it is the
  review-side sibling of h9k pr resolve and follows its manner.
- Fail-fast on zero commits must not break the legitimate empty case, if one exists
  (a research-type task whose deliverable is its transcript); check TaskType before
  assuming every task produces commits, and document the choice.
