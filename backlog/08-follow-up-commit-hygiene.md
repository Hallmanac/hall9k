---
project: hall9k
type: feature
objective: Teach follow-up runs to fold their fixes into the owning commits instead of appending review-feedback commits, as a user-selectable commit style
criteria:
- The follow-up prompt (AgentPromptBuilder.BuildFollowUp) instructs the agent to land fixes per the AGENTS.md "authored history" rule - git commit --fixup=<owning-commit> mapped by file ownership, autosquash rebase onto origin/main, push --force-with-lease - instead of committing "address review feedback" on top
- The prompt requires the tree-identity check (git diff <old-tip> HEAD empty after the rebase) before the force-push, so verification-gate results carry over honestly
- The commit style is a configurable preference, not hardwired: a narrative (fixup/autosquash) mode and an append mode, defaulting to narrative; config surface per project with a platform default (see IDEA-platform-defaults for where user-level defaults live)
- A force-pushed follow-up still flows through the existing pipeline: PullRequestUpdated appends, the closeout monitor's next sweep sees the new tip, CI re-runs on it
- The daemon's follow-up push path handles rewritten history: PullRequestOpener (or its follow-up equivalent) pushes with --force-with-lease when the run is a follow-up, instead of the plain push that rejects a rebased branch (origin incident, 2026-08-17: the first two automatic follow-up runs rebased per the AGENTS.md authored-history rule, the daemon's plain push failed with "failed to push some refs", and both runs - and their tasks - failed with completed, gated work stranded in their worktrees)
- The closeout monitor tolerates the rewritten history it now causes: branch cleanup and worktree reuse work against a force-pushed branch (worktree checkout resets to the remote tip, not a stale local ref)
- dotnet build and dotnet test pass
---
Origin incident (2026-08-17): the pre-merge review round on PR #6 landed as a separate
"harden closeout against monitor-vs-human reopen races" commit and was rebuilt by hand
into the two owning commits (fixup + autosquash + force-with-lease). Brian's standard:
a PR branch reads as a natural progression of the whole change - cohesive, not
consecutive - with no WIP or review-follow-up commits. The AGENTS.md Git rules now
state this; this task makes the follow-up pipeline actually produce it.

Design constraints:
- Depends on 04 (closeout monitor) and its follow-up dispatch being merged.
- The fixup mapping rule is mechanical and belongs in the prompt: a fix maps to the
  most recent branch commit that touches the same file; a fix spanning files owned by
  different commits splits into one fixup per owning commit; genuinely new scope (a
  new file no commit owns) may be a new, properly-titled commit - never "review fixes".
- Force-push safety: --force-with-lease always; never plain --force. If the lease
  fails, the branch moved (another node or a human) - requeue honestly rather than
  retrying blind.
- Consider a repo skill (e.g. absorb-review-fixes) so the mechanics live beside
  commit-plan and are reusable by humans too.
