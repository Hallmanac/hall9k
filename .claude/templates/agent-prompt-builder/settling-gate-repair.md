===heading===
# Fix the mandatory gate failure this branch's rebase left behind
===rebase-summary-recovered===
This branch was rebased onto its base (from {{FromCommit}} to {{OntoCommit}}), which needed a narrow recovery session's own judgment to resolve a real conflict, immediately before the platform's own
===rebase-summary-clean===
This branch was rebased onto its base (from {{FromCommit}} to {{OntoCommit}}), which git applied cleanly with no conflict, immediately before the platform's own
===gate-failed-intro===
mandatory final gate — and that gate then failed. Your job is to make it pass again.
Nobody has separated whether the rebase itself is what broke it, from a coincident
commit, from a changed verify command: read the failure yourself and fix what is
actually broken, not to redo the original work.
===human-guidance-heading===
## A human's guidance on this repair
===human-guidance-intro===
An earlier repair round could not make the gate pass, and a human weighed in
before this round was dispatched. Apply their guidance below.
===gate-output-heading===
## The gate's own failure output
===original-objective-heading===
## Original objective (context, already implemented)
===project-links-heading===
## Project links (fetch yourself as needed)
===working-rules-heading===
## Working rules
===worktree-lead===
- You are in this run's own git worktree, checked out on its own in-progress
===worktree-with-pr===
  branch `{{Branch}}` — already pushed and open as the pull request above. Work only here.
===worktree-without-pr===
  branch `{{Branch}}` — not yet pushed anywhere. Work only here.
===reproduce-and-fold===
- Reproduce the gate's own failure locally, find the real cause, and fix it. Fold the
  fix into the branch's own history per the commit style below rather than leaving it
  as a separate "fix" commit.
===no-need-to-run-gate===
- You do not need to run the gate yourself before finishing — the platform runs it
  again, in full, immediately after this session ends, and judges the repair by
  whether that run passes, not by anything you write here. Still worth confirming
  your own fix locally before you stop, so you are not guessing.
===no-push-with-pr===
- Do NOT push (the platform pushes after re-verifying), and do NOT open a new
  pull request — the existing PR updates in place.
===no-push-without-pr===
- Do NOT push, and do NOT open a pull request — the platform's own mandatory
  gate and review pass run over the tree you leave behind, and the daemon pushes
  and opens the pull request itself once everything is green.
===closing-summary===
- End with a short summary: what was actually broken, why, and what you changed.
