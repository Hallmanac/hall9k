---
project: hall9k
type: feature
objective: Make the idea a first-class concept with its own discovery phase - captured with zero friction, optionally project-assigned, and promoted into a draft task once discovery gives it intent
criteria:
- h9k idea add "<text>" captures an idea with nothing but the text; --project <name> is OPTIONAL at capture (assign it when known, omit it when not - an idea may precede its project or become one), and h9k idea assign <id> --project <name> sets or changes it later
- Ideas are owner-scoped with their own small aggregate and stream (IdeaCaptured, IdeaRevised, IdeaAssignedToProject, IdeaPromoted, IdeaDiscarded), tiny-slice layout
- Each idea owns a discovery workspace: a directory under the platform home (revealed by h9k idea show) where research notes, gathered files, and prototypes accumulate during discovery; the stream records milestones only, never file contents, and promotion carries the workspace pointer forward to the task
- h9k idea list shows ideas newest-first with age and project (or the honest absence of one); h9k idea show <id> shows text, project, workspace path, revision history, and what the idea became if promoted
- h9k idea revise <id> "<text>" rewrites the note as discovery sharpens it (history stays on the stream)
- h9k idea promote <id> [--project <name>] creates a draft task: the idea's text seeds the draft (first sentence as objective unless --objective overrides; remainder as context), the discovery workspace pointer rides along as agent context, and provenance is recorded in both directions (IdeaPromoted names the task; the task's stream names the source idea). Promotion requires a project - supplied now or already assigned
- Promotion to a PROJECT is recorded when it happens or explicitly deferred with a teaching message if project registration cannot compose cleanly yet; decide during design and document the choice
- h9k idea discard <id> --reason closes an idea honestly (recorded, never deleted)
- The vocabulary is deliberate and used consistently in help text and docs: ideas undergo DISCOVERY (what is this?); draft tasks undergo REFINEMENT (how does this become executable?) - a task is an idea with intent, and the decisions log records this vocabulary
- Teaching prompts throughout: idea list's footer teaches promote; promote teaches the draft ceremony it hands off to
- dotnet build and dotnet test pass
---
Brian's direction (2026-08-20, expanded): an idea can start with or without a
project - either way it is just a thought ingested quickly so it is not lost. The
idea then has a discovery phase: research, digging around, creating files, maybe
prototyping. Discovery gives the idea intent, and an idea with intent is a task -
promotion lands it as a draft, where the work shifts from discovery to refinement
(honing it into something an agent or a human can act on). Both phases may feel
similar in activity; they differ in question. The backlog's own IDEA-*.md naming
convention is the origin story, and those files become the platform's first ideas
when this lands.

Design constraints:
- Capture is the sacred path: one command, one argument. --project is the only
  optional nicety at capture; anything REQUIRED beyond the text defeats the feature.
- The discovery workspace is a directory, not an event payload: streams carry
  milestones only (the transcripts-on-disk discipline). Full per-file provenance
  events belong to the attachments feature and can generalize to ideas later; the
  workspace pointer is the honest v1.
- Promotion composes with the existing lifecycle rather than duplicating it: the
  product is an ordinary draft entering ordinary refinement. Dispatching agents to
  work ON an idea during discovery is the refinement-runs idea generalized - out of
  scope here, noted there.
- On-the-go capture from a phone is future territory (P2P/multi-node); this builds
  the concept and the CLI, not the mobile path.
- Never-guess at promotion: objective comes from the idea's text or the human's
  flag; first-sentence extraction is mechanical and visible, never inference.
