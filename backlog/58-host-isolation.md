---
project: hall9k
type: bugfix
objective: An agent session keeps its hands inside its worktree - the machinery it tests is exercised against scratch roots, never against the host's real install, so a session can never break the machine it runs on
criteria:
- The dispatch prompt (AgentPromptBuilder) states the isolation rule - a session writes only inside its worktree and freshly created scratch directories, and never creates, retargets, or deletes anything in real PATH directories (/opt/homebrew/bin, /usr/local/bin, ~/.local/bin), the real ~/.hall9k install, or any other host location
- The per-run settings.json the daemon writes for a session denies writes to those host locations, so the rule is enforced rather than merely requested - a denied call surfaces to the session as a refusal it must respect
- Tests and smoke checks of install, update, and link machinery take the PATH roots they operate on as parameters and are exercised against scratch directories, and the existing install and update code paths accept an injected root for exactly this reason where they do not already
- dotnet build and dotnet test pass
---
Origin (2026-08-23): task 42's build session, smoke-testing the new update
flow, ran it against the machine's real PATH: it retargeted the genuine
/opt/homebrew/bin/h9k symlink at a temp staging directory, and after the temp
directory vanished it judged the now-dangling link to be its own test debris
and deleted it - "confirmed removed, nothing left". The operator's h9k CLI
went command-not-found mid-session. When the orchestrator restored a link at
~/.local/bin/h9k, the same session swept that one too and attempted rmdir on
~/.local/bin itself, which also holds the operator's claude shim. The session
was killed to stop the loop, and the lane retried with the isolation rule
carried in the retry reason - a per-attempt patch for what should be a
standing, enforced rule.

Relationship: rides with 57 in the machinery-hygiene family. Enforcement in
settings.json matters more than the prompt line: the session that did this was
acting in honest good faith on a wrong premise, which is exactly the case
instructions alone do not catch.
