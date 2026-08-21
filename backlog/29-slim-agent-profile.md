---
project: hall9k
type: feature
objective: Dispatched sessions launch with a slim profile by default - MCPs are declared per task when a mission needs them, never inherited wholesale
criteria:
- Platform-dispatched sessions (build, review, fix, follow-up, refinement) launch with a slim settings profile that disables the owner's MCP servers by default; the spawn already passes --settings, so this extends the existing seam
- CLIs remain the on-demand capability path and the prompts say so: git, gh, and the Atlassian CLI are always available and cost nothing when idle - the recorded history is that every platform session to date worked entirely through CLIs
- A task can DECLARE the MCPs its mission needs (task-level, with a project-level default), resolved most-specific-wins like the model policy; a push-to-jira run or a browser-testing task gets its declared servers, everything else stays slim
- An agent that discovers mid-run it lacks a capability follows the existing conversation flow: ask, park NeedsHuman with the reason naming the missing access - never silently fail
- The declared-MCP set is recorded on the dispatch event like the model (an observed fact of the run's capabilities)
- dotnet build and dotnet test pass
---
Origin (2026-08-21): the kernel-panic postmortem found the memory multiplier -
decision #25's full-config inheritance means every headless session may spawn the
owner's entire MCP fleet (Atlassian, Chrome, Slack, Wispr), none of which any
platform session has ever used; concurrent runs multiply it. Brian's concern,
folded in as the design: capability must not be lost - MCPs are launch-time-only
(no hot attach), so the answer is declare-when-needed plus CLIs as the true
on-demand path, with the ask-and-park flow as the net for missed declarations.

Design constraints:
- Backlog 18's Jira writes explicitly ride agent MCP/skills - those runs declare
  the Atlassian MCP (or migrate to the Atlassian CLI, which may be the better
  headless citizen; decide during 18).
- The slim profile keeps includeCoAuthoredBy false and every existing settings
  behavior; this narrows MCPs only.
