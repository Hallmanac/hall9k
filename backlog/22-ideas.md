---
project: hall9k
type: feature
objective: Make the idea a first-class concept - captured with zero friction, owned by no project, and promotable into a draft task or a project when it grows up
criteria:
- h9k idea add "<text>" captures an idea with nothing but the text: no project, no type, no objective phrasing - capture friction is the whole point (Brian may be on the move, or the project may not exist yet, or the idea may BECOME a project)
- Ideas are owner-scoped, not project-scoped: a new small aggregate and stream (IdeaCaptured, IdeaRevised, IdeaPromoted, IdeaDiscarded) following the tiny-slice layout
- h9k idea list shows captured ideas newest-first with age; h9k idea show <id> shows the text, any revisions, and what the idea became if promoted
- h9k idea revise <id> "<text>" rewrites the note as thinking evolves (history stays on the stream)
- h9k idea promote <id> --project <name> turns an idea into a draft task: the text seeds the draft (objective from the first sentence or an explicit --objective, remainder as context), and IdeaPromoted records the created task id while the task's stream records the source idea - provenance in both directions
- Promotion to a project is recorded when it happens (h9k idea promote --to-project <name> registering the project and linking it) or explicitly deferred with a teaching message if project registration cannot be composed cleanly yet - decide during design and document the choice
- h9k idea discard <id> --reason closes an idea honestly (discarded is recorded, never deleted)
- Teaching prompts throughout: idea list's footer teaches promote; promote teaches the draft ceremony it hands off to
- dotnet build and dotnet test pass
---
Brian's direction (2026-08-20): ideas are definitely different from tasks. A draft
task solved the readiness gradient (a task not yet ready to dispatch); an idea
lives earlier, on the homeless gradient - no project chosen, maybe no project in
existence, capture needed NOW with zero ceremony. The backlog's own IDEA-*.md
naming convention is the origin story: those files existed precisely because their
contents were not tasks yet. The migration morning splits accordingly: task-shaped
backlog files become draft tasks; IDEA files wait for this feature and become the
platform's first ideas.

Design constraints:
- Capture is the sacred path: one command, one argument, done. Anything that adds
  a required decision at capture time defeats the feature.
- Promotion composes with the existing lifecycle rather than duplicating it: the
  product of promote-to-task is an ordinary draft entering the ordinary ceremony.
- On-the-go capture from a phone is future territory (P2P/multi-node sync ideas);
  this task builds the concept and the CLI, not the mobile path.
- Never-guess applies to promotion: the objective seed comes from the idea's text
  or the human's flag, never from platform inference beyond first-sentence
  extraction, which is mechanical and visible.
