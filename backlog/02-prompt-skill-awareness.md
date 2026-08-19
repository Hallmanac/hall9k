---
project: hall9k
type: feature
objective: Teach dispatched agents which repo skills exist and when to reach for them via the prompt working rules
criteria:
- AgentPromptBuilder's working rules name the repo's skills with one-line when-to-use guidance (e.g. use commit-plan before committing multi-part work)
- The skills section appears only when the worktree actually contains .claude/skills
- Unit tests cover both the with-skills and without-skills prompt shapes
- dotnet build and dotnet test pass
---
The prompt builder is src/Hall9k.Daemon/Execution/AgentPromptBuilder.cs. It already
composes objective, acceptance criteria, agent context, project context links, and
working rules — this task extends the working-rules section.

Keep it lean: name each skill and its trigger in one line each. Do not paste skill
content into the prompt — skills load on invocation; the prompt only needs the pointer.
Task-type-specific guidance (personas, PLAN.md §6.6) is out of scope for this task.
