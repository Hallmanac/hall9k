---
project: hall9k
type: feature
objective: Tasks and ideas render as markdown files in the project home - the store stays the record, the files are a browsable mirror rewritten on every event, and edits flow back only through the explicit revise gate
criteria:
- Every task owns a directory under <home>/tasks/, named by short id plus a slug from the objective (98ac05ef-project-home/), holding task.md - the rendered contract in the established format (frontmatter, prose context below), rewritten whenever a task event lands - and a workspace/ beside it for refinement materials (design files, attachments, whatever the shaping accumulates); ideas render identically under <home>/ideas/ (idea.md beside the existing discovery workspace), so discovery and refinement are the same shape at two lifecycle stages
- Drafts render exactly like published tasks - the browsing surface is where the thinking lives - mirroring the tracker split: visible locally from the first keystroke, visible externally only at publish or explicit opt-in (backlog 51)
- The render is one-way: no file watcher, no automatic ingestion; a human edits a file and applies it with the existing h9k task revise <id> --file (or idea revise), and the render then reflects what the store accepted - the render-not-record ruling from the discovery doc
- Each rendered file says what it is in one header line: generated from the store, edit and apply via the revise command, direct edits are overwritten on the next event
- Rendering is driven from the daemon's event handling and is best-effort: a failed file write never fails the event it renders, and the next event repairs the file
- A render pass on daemon start reconciles the directory (missing files written, orphaned files for abandoned or absorbed tasks marked or removed), so a home created after tasks existed backfills itself
- dotnet build and dotnet test pass
---
Slice 2 of the project-centred structure (idea 64e4ebd2). Additive and
non-breaking: lands after 47 gives files a home. Opening the project directory
in VS Code and browsing every task one file at a time is the acceptance
experience this exists for.
