---
project: hall9k
type: bugfix
objective: A build session that finishes its work cannot lose it by waiting - gates run in the foreground, and a session that ends with uncommitted work in the worktree is recorded as interrupted work worth resuming, never as an empty attempt
criteria:
- The dispatch prompt (AgentPromptBuilder) instructs the session explicitly that verification commands (build, test) run in the foreground and that the session must never background a required gate and end its turn waiting on a completion notification - in headless mode ending the turn ends the session
- The dispatch prompt instructs the session to commit completed work before running long verification, so a death during the gate loses the gate run and not the work
- When a run ends without commits, the verification runner distinguishes a dirty worktree from a clean one - "no commits, N modified files" is recorded as interrupted work and the failure message says the work may be recoverable, while "no commits, clean tree" keeps today's honest nothing-happened failure
- The retry briefing for a run that ended with a dirty tree names the modified-file count so the resuming session knows to review and continue rather than start over
- dotnet build and dotnet test pass
---
Origin (2026-08-23, twice in one evening): task 45's build session implemented
the whole closeout-budget feature (17 files, 825 insertions with tests), then
backgrounded dotnet test and ended its turn saying it would wait for the
completion notification. In headless claude -p mode ending the turn ends the
session, so it died with everything uncommitted; the gates ran against an
unmodified tree and failed the run as "Agent produced no commits", twice, and
the work was only recovered by a manual rescue session that verified and
committed the tree. Hours later task 42's killed session left the same shape
behind: 17 modified files, zero commits. The platform read both as empty
attempts when both were nearly finished work.

Relationship: rides beside 53 and 54 in the machinery-hygiene family. The
retained-worktree retry flow already carries a "previous attempt worked here
first" briefing; this slice makes the failure record honest enough for that
briefing to say what is actually there.
