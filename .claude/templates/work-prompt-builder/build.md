===title===
# Task
===handback-heading===
## A human began this work interactively
===handback-body===
An operator started this task with `h9k task work`, worked directly in this
branch's worktree, and handed it back (`h9k task handback`) for you to finish
headlessly. `h9k task handback` only refuses on tracked files it finds modified
or staged — it never checks untracked files, and skips the check entirely if git
could not be read — so their work may be committed on the branch, sitting
uncommitted in the tree (tracked or not), or both. Before writing anything,
review what is there (`git status`, `git log`, `git diff`), judge it against the
acceptance criteria, and continue from it to completion. Do not start over;
redoing finished work is the failure mode this note exists to prevent.
===handback-reason===
Why they handed it back, in their own words: {{ResumeReason}}
===resume-causeless-heading===
## A previous attempt worked here first
===resume-causeless-body===
This run resumes an existing branch in a retained worktree. That is not
necessarily because anything failed — it may be a deliberate hand-off, or an
operator simply picking their own work back up. The previous attempt's work may
already be present — committed on the branch, uncommitted in the working tree,
or both. Before writing anything, review what is there (`git status`, `git log`,
`git diff`), judge it against the acceptance criteria, and continue from it.
Do not start over when usable work exists; redoing finished work is the
failure mode this note exists to prevent.
===retry-reason-is-handback-causeless===
Why this run resumes here, in the requester's own words: {{RetryReason}}
===acceptance-criteria-heading===
## Acceptance criteria
===context-heading===
## Context
===project-links-heading===
## Project links (fetch yourself as needed)
===project-links-line===
- {{Name}}: {{Url}}
===working-rules-heading===
## Working rules
===worktree-self-registration===
- **This task's worktree is `{{WorktreePath}}`, on branch `{{Branch}}`.** This
  prompt may have been pasted into a session started anywhere — before anything
  else, `cd "{{WorktreePath}}"` and confirm with `git branch --show-current` that
  it reads `{{Branch}}`. Work only there for the rest of this session.
===worktree-plain===
- You are in an isolated git worktree on branch `{{Branch}}`. Work only here.
===implement-objective===
- Implement the objective so every acceptance criterion is satisfied.
===commit-clear-messages===
- Commit your work with clear messages. Do NOT push, do NOT open a pull request —
===interactive-delivery-line===
  delivery is `h9k task {{Deliver}}`, run by the operator explicitly; nothing pushes or
  opens a pull request until then.
===interactive-writing-conventions-lead===
**How anything you write for people reads.** This project's writing conventions govern every commit message, and every word you draft for the operator to post anywhere under their own login:
===delegated-contractor-intro===
  nothing supervises this run once it starts, and verification and delivery are
  a human's to trigger by hand once you finish, not yours:
  `h9k task {{Deliver}}` pushes the branch and opens the pull request through the
  ordinary review pipeline (`h9k task verify` checks the gates first if they want
  to look before delivering). Both commands refuse when run from inside this very
  session, so do not attempt them yourself — end with your summary once the work
  below is done.
===deliberate-headless-start-intro===
  once your session ends, the platform checks the worktree itself: a clean,
  committed tree is delivered automatically through the ordinary review pipeline
  (the same push-and-open-the-pull-request `h9k task {{Deliver}}` would otherwise do
  by hand), and anything else — uncommitted files, or no commits beyond the base
  branch — is left exactly as you leave it and flagged for a human instead.
  Verification and delivery are still not yours to trigger: `h9k task {{Deliver}}` and
  `h9k task verify` both refuse when run from inside this very session, so
  do not attempt them yourself — end with your summary once the work below is
  done, and leave the tree exactly how you want it found.
===headless-dispatch-line===
  the platform verifies and opens the PR after you finish.
===skills-heading===
- This repo ships Claude skills; invoke the matching one instead of improvising its workflow:
===ambiguous-interactive===
- If something is genuinely ambiguous, ask the operator at this terminal rather than
  guessing — they are attached to this session for exactly this reason.
===ambiguous-headless===
- If something is genuinely ambiguous, make the most reasonable choice and record
  the assumption in your final summary (the ask-a-human loop is not available yet).
===end-summary===
- End with a short summary: what you did, decisions made, assumptions, open questions.
