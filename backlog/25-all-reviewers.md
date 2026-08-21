---
project: hall9k
type: feature
objective: Treat every reviewer's threads as feedback the platform sees and dispatches on - Copilot is one reviewer among many, not the definition of review
criteria:
- The closeout monitor's inspector counts unresolved review threads from ANY author, not only Copilot; a teammate's unresolved thread triggers the same automatic follow-up dispatch (same budget, same park behavior)
- The resolve skill and follow-up prompt generalize from resolve-copilot-reviews to all unresolved threads: read each thread, judge it (accept and fix, or reply with reasoning), reply in-thread, and resolve - whoever wrote it
- Human threads are handled with more care than bot threads, and the prompt says so: a thread that is a question gets an answer, not a forced code change; a thread expressing a design disagreement the agent cannot honestly judge parks the run NeedsHuman with both positions recorded, per the review-loop rules
- The self-review discriminator is explicit and documented: agents never START review threads, they only reply within them - so a thread whose first comment is authored by the PR author's own login is a human self-note and is treated as reviewer feedback (origin incident, 2026-08-20: Brian commented on PR #20 and the machinery was structurally blind to it - the inspector filtered to Copilot authors, and agent replies under his own login made human and agent comments indistinguishable by author)
- Pending-review invisibility is acknowledged in the docs: GitHub hides unsubmitted review comments from the API, so feedback reaches the platform only on submit - the teaching surfaces say so rather than leaving silence unexplained
- The skill file is renamed or superseded honestly (resolve-review-threads), with the Copilot-specific name retired and AGENTS.md's skills list updated
- Re-requesting review after pushed fixes is a configurable option, default OFF (Brian, 2026-08-21): when enabled, the monitor re-requests the reviewer's pass after a fix follow-up force-pushes, so the reviewer countersigns that its findings were addressed. Configurable at the owner level (this owner wants it on their work) and the project level, precedence decided at design time in the model-policy shape. Bounded hard against the doom loop of refinement and re-refactoring: a re-request pass cap (sane default 2-3, its own counter beside the closeout budget) after which the PR settles on the internal review, thread replies, and CI - the existing guards that already review every fix before it pushes. Each re-request costs review quota; the option prices it knowingly
- dotnet build and dotnet test pass
---
Brian's direction (2026-08-20): teams mean teammates reviewing, not just Copilot.
The current pipeline filters review feedback to copilot-authored threads at two
layers (the closeout inspector and the resolve skill), which made a human comment
on PR #20 invisible to the machinery the same evening the question came up.

Design constraints:
- The thread-starter discriminator is reliable today precisely because of the
  no-bot-identity rule: agents author as the human but only ever reply to existing
  threads. If that invariant ever changes, this breaks - note it beside the rule.
  The eventual honest answer is the P2P identity layer (node-signed authorship);
  this task ships the heuristic and documents its dependency.
- Judgment stays bounded: the never-loop rule applies to human threads exactly as
  to review parks - one honest attempt per thread per follow-up, disagreement goes
  to a human, no re-litigating.
- Responses to human feedback land where the feedback lives (Brian, 2026-08-20): an
  inline review comment gets an in-thread reply, exactly as Copilot threads do today; a
  review BODY - which GitHub makes unthreadable - gets a top-level PR comment that names
  the review it answers and summarizes what was done, never silence and never an
  unanchored comment the reviewer has to connect themselves (origin: the PR #20 human
  review was answered only through the work itself, with no visible reply on the PR)
- Do not resolve threads from human reviewers without replying substantively;
  a resolved-without-answer human thread is worse than an open one.
