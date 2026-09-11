===opening-no-gates-recompose===
- **Self-review phase.** This project configures no verification gates, so its
  suite is vacuously green already (the recompose step below states the same
  thing); once the work itself is done and every checkpoint is committed, and
  before the recompose below, hunt your own branch for defects. It runs here —
  after the work is finished and the tree is clean, so the hunt's diff actually
  shows the newest work rather than missing whatever is still sitting
  uncommitted, and before the recompose, so the recompose composes the tree the
  hunt leaves behind rather than the tree that predates it.
===opening-no-gates-no-recompose===
- **Self-review phase.** This project configures no verification gates, so its
  suite is vacuously green already; once the work itself is done and every
  checkpoint is committed, and before you finish, hunt your own branch for
  defects. It runs here — after the work is finished and the tree is clean, so
  the hunt's diff actually shows the newest work rather than missing whatever
  is still sitting uncommitted, and before you finish, so your own checkpoint
  commits — left as this branch's own history, unrecomposed — are the ones the
  hunt leaves behind rather than the ones that predate it.
===opening-gates-recompose===
- **Self-review phase.** Once the full verification suite named below is
  green and every checkpoint is committed, and before the recompose below, hunt
  your own branch for defects. It runs here — after the suite passes and the
  tree is clean, so the hunt's diff actually shows the newest work rather than
  missing whatever is still sitting uncommitted, and before the recompose, so
  the recompose composes the tree the hunt leaves behind rather than the tree
  that predates it.
===opening-gates-no-recompose===
- **Self-review phase.** Once the full verification suite named below is
  green and every checkpoint is committed, and before you finish, hunt your own
  branch for defects. It runs here — after the suite passes and the tree is
  clean, so the hunt's diff actually shows the newest work rather than missing
  whatever is still sitting uncommitted, and before you finish, so your own
  checkpoint commits — left as this branch's own history, unrecomposed — are
  the ones the hunt leaves behind rather than the ones that predate it.
===hats-and-cap===
  Change hats for this phase: you are no longer the author, you are the hunter.
  Assume the branch contains defects you wrote, and go looking for them the way
  someone hostile to this diff would, not the way its author would.
  Finding nothing is an expected, honest outcome of a genuine hunt — inventing a
  finding so the round has something to report is the failure this phase is
  guarding against, not the clean round.
  The loop is capped at two rounds, hard.
===round-one-stacked===
  Round one starts from a fresh `git diff {{StackedForkPointCommit}}...HEAD`,
  read in full — not from memory of what you wrote. The range names this branch's
  recorded fork point off `{{EffectiveBaseBranch}}` as a literal commit rather than
  `origin/{{EffectiveBaseBranch}}`: this branch is stacked on that one, and a parent
  branch force-pushed while this session runs moves that ref out from under the
  range, folding the parent's own rewritten delta into what would read as this
  branch's work. A diff you already believe you know is not a diff you
  actually reviewed. Before hunting, record the current tip so a round two, if
===round-one-unstacked===
  Round one starts from a fresh `git diff origin/{{EffectiveBaseBranch}}...HEAD`,
  read in full — not from memory of what you wrote. A worktree's local
  base-branch ref is routinely stale relative to this task's actual base, so name
  `origin/` in the range; a diff you already believe you know is not a diff you
  actually reviewed. Before hunting, record the current tip so a round two, if
===tip-file-and-hunts===
  one runs, can diff only its own fixes instead of the whole branch again. A
  shell variable does not survive between separate tool calls, so setting one
  here and reading it back several tool calls into round two gets nothing —
  `git diff $EMPTY HEAD` silently degrades to `git diff HEAD`, which prints
  nothing and exits 0 against the clean tree this phase requires, so round two
  would review an empty diff and call it clean. Write the tip to a file outside
  this worktree instead, where it survives the gap. The filename is suffixed
  with this worktree's own directory name so a concurrent session in a sibling
  worktree on the same node never clobbers this one's tip:
  `git rev-parse HEAD > "{{TipFile}}"`. Remove that file once this phase
  ends, whichever round it ends on — like the hunt-3 scratch directory below,
  it is scratch state for this phase alone and does not belong on the node
  afterward.
  Three hunts are mandatory every round:
  1. **Refactor once-over.** Reread everything the diff touched as if it were
     someone else's pull request: naming, structure, dead code, duplication, a
     change that should have been smaller or cleaner.
  2. **Blast-radius sweep.** For every behavior this branch changed, enumerate
     every sibling site with the same shape and check each one actually got the
     same treatment, rather than trusting your memory of having handled it. This
     is the class that cost two full review laps in one afternoon here: a fix
     landed on one of two branch-creating arms that needed it, and a two-escape
     finding closed one escape and left the other open.
  3. **Execute your own instructions.** Any skill step, command sequence, or
     documented procedure in this branch's diff — whether you wrote it this
     session or it arrived already in the diff you resumed — run it, do not proofread
     it. A step that reads correctly and fails the moment it is actually run is a
     real defect a re-read never catches. Where a procedure's commands
     mutate state, exercise it somewhere the side effects are safe — a scratch
     directory made with `mktemp -d`, outside this worktree entirely —
     never against this session's own live worktree. The scratch directory is a
     deliberate, temporary exception to "work only here" — for exercising a
     procedure's side effects safely, not for leaving work in progress. Clean it
     up once the hunt is done. A relocated directory only contains a procedure
     whose side effects stay local to it — it does nothing for one that mutates a
     resource this session does not own outright: a live daemon or its database, a
     machine-wide install (`h9k install`, `h9k update`), a destructive maintenance
     command (`h9k uninstall --purge-data`), or a write to an external service
     (`gh`, a registered connection). A procedure in that shape is read in
     enough functional detail to be confident it does what it claims —
     never actually run. A procedure you conclude is correct this way produces no
     finding, so record why relocation could not make it safe in your final
     summary and the handoff below instead — the same vehicle this phase already
     uses for a suspicion that never rises to a stated finding — rather than
     silently falling back to a proofread with nothing said about it.
  Every finding this phase surfaces, in round one or round two, ends in one of
  its dispositions before you move on: a correctness-or-behavior finding is
  fixed and checkpoint-committed, or
  left with a stated, checkable reason it is not actually a defect. The cap
  bounds how many rounds you hunt in, not what you owe once something is found,
  so a real finding is never legal to defer instead — including one that
  round two turns up: fix and commit it there, same as round one,
  without that alone starting a round three.
  A style-only finding needs no such reason: it is fixed in place and
  checkpoint-committed, or skipped outright — a skip produces no edit, so it
  earns neither a checkpoint commit nor a suite re-run.
  Deferring a real finding to a note for later is not a third option; the one
  thing that does carry forward unresolved is a genuine suspicion that never
  rose to a stated, checkable finding — something noticed but not pinned down
  enough to act on. Record that in your final summary and in the handoff below:
  the audience for both is whatever task depends on this one and the human
  reading the run, not the review that follows.
  Whenever a fix does land,
===rerun-no-gates-recompose===
  the loop continues or the recompose begins directly — this project
  configures no verification gates, so there is no suite to re-run, and the
  recompose downstream still holds its own guarantee (the tree it composes is
  the tree the fix left behind) regardless of gates.
===rerun-no-gates-no-recompose===
  the loop simply continues — this project configures no verification gates,
  so there is no suite to re-run, and your own checkpoint commits, left
  unrecomposed, already are the tree the fix left behind regardless of gates.
===rerun-gates-recompose===
  the full verification suite runs again — after every fix this phase makes,
  style-only included, not only a correctness-or-behavior one — before the loop
  continues or the recompose begins. A fix that broke something is itself a
  defect regardless of how the finding that prompted it was graded, and the
  recompose downstream only holds its own guarantee (the tree it composes is the
  tree that passed the suite) if the suite ran after this phase's last fix, not
  just before this phase started.
===rerun-gates-no-recompose===
  the full verification suite runs again — after every fix this phase makes,
  style-only included, not only a correctness-or-behavior one — before the loop
  continues. A fix that broke something is itself a defect regardless of how
  the finding that prompted it was graded, and your own checkpoint commits,
  left unrecomposed, only stand for a tree that actually passed the suite if it
  ran after this phase's last fix, not just before this phase started.
===round-two-and-cap===
  A style-only finding never by itself earns a round two — that is not what the
  cap is for. A finding round one dismisses rather than fixes does not earn one
  either: nothing landed, so the recorded tip and the current tip are identical,
  and a round two would review an empty diff and call it clean — the exact
  failure the tip-file mechanic exists to prevent. Only when round one actually
  fixed something above the behavior-or-correctness bar does a round two run,
  scoped to only the diff of those fixes —
  `git diff "$(cat "{{TipFile}}")" HEAD` — rather than the whole
  branch again, with the same three hunts scoped to it. A round that fixes
  nothing above that bar — including round one — ends the loop right there.
  After round two the loop ends unconditionally either way: no third round —
  and the only thing still open when it ends is a suspicion that never rose to
  a stated finding; a real finding is never legal to leave unresolved, round
  cap or not.
