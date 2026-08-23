> **VERIFIED LIVE 2026-08-23: no ContextAttached/ContextDetached events, no attach command, no dispatch manifest anywhere in src. Backlog 48 pre-places the BYTES (a workspace/ beside every task render); this file now owns only the provenance events and the manifest.**

# Idea: task attachments (context accumulation with provenance)

Captured 2026-08-19 from Brian's workflow walkthrough: an idea gets jotted as a
draft task, but the thinking around it keeps happening - a Claude conversation,
a colleague's repo pointer, research notes. Those artifacts need a way INTO the
task, each addition carrying where it came from, without pasting everything
into one ever-growing context blob.

## The shape

- `h9k task attach <id> <file> --source "<where this came from>"` attaches any
  file to a Draft or Published task (post-assignment attachment follows the
  same immutability question as revision - probably unassign first, decide
  with 05's rules).
- Storage follows the established discipline (streams carry milestones, bytes
  live on disk): a `ContextAttached` event records name, content hash, source,
  and timestamp; the file lands in the task's artifact directory. The event
  stream gives per-addition provenance the single AgentContext blob cannot.
- At dispatch, AgentPromptBuilder hands the agent the attachment manifest and
  paths (inlining small ones), so accumulated research actually reaches the
  run that does the work.
- Detach is an event too (`ContextDetached`, reason recorded) - the honesty
  principle: removal is observed history, not deletion.

## Fits

- **Backlog 05**: attachments are the natural companion to Draft-mode
  refinement - revision handles the task's own text, attachments handle
  everything gathered around it. Could land as a fast-follow to 05 (see the
  pointer added in 05's design constraints).
- **Ideas (decision #35, backlog 22)**: an idea's discovery workspace is the
  same problem with the honest v1 answer - a directory, with the stream carrying
  milestones only. When `ContextAttached` exists it should generalize to ideas,
  which would give per-file provenance for the research that produced a task
  instead of a single workspace pointer. The pointer stays useful either way.
- **IDEA-p2p-lazy-sync**: attachments are exactly the "content" a task's
  metadata shell leaves unhydrated until a node takes the lease or a human
  browses.
- **IDEA-artifact-retention**: attachment bytes age out with the same policy
  as run artifacts; the ContextAttached events (hash, source) remain -
  "purged per policy," never a bare not-found.
- **Beads comparison**: beads accumulates context as issue comments/threads
  inside its database; our split (events for provenance, artifact dir for
  bytes) keeps the store lean and the sync story lazy.
