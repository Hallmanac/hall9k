# Idea: P2P sync — metadata everywhere, content on lease (lazy hydration)

Captured 2026-08-17 (quick idea for the multi-node era; talk through at roadmap #5).

## The idea (Brian's framing)

When nodes sync peer-to-peer, sync ALL tasks as metadata only — essentially what's in the
Postgres database — without the content that backs them. Every user sees what tasks exist.
Like Dropbox/OneDrive placeholder files: the entry is visible, but the bytes aren't local.

Hydration trigger is the LEASE, not a click: when a node lays a lease on a task, the
originating planning docs and additional content (discovery records, attachments, artifacts)
get pulled to that machine. From then on, ONLY the lease-holding machine can make edits and
updates to the task and everything around it — single-writer by lease, so no conflicts,
while files still flow between nodes.

Secondary trigger: investigating a task more deeply without claiming it also syncs the
content down read-only — browsing is allowed; the lease is what grants write authority.

## Initial mapping (to refine at roadmap #5)

- Two tiers fall straight out of existing decisions:
  - Metadata tier = events + projections (already lean because transcripts stay out of
    streams, log #6). This is what replicates everywhere.
  - Content tier = the content-addressed store (§8.1) — which was explicitly designed for
    this: "do you have hash abc123?" is the sync primitive. Events reference content by
    hash; hydration is fetching missing hashes.
- The lease generalizes beautifully: today it means "this node runs the task"; in P2P it
  also means "this node is the single writer for the task's content." The fencing token
  (log #7) already rejects stale writers — the same mechanism, extended to edits.
- Single-writer-by-lease avoids CRDTs/merge resolution entirely — the big simplification.
  Conflict-free by construction, matching the accountability principle (§6.2).
- Read-without-lease = read-only hydration; no write authority granted.
- Open questions: where does a task's content live before anyone leases it (originating
  node as seed; availability when that node is offline)? Eviction of hydrated content on
  lease release/expiry (ties into IDEA-artifact-retention)? How the remote-lease takeover
  flow (log #12) interacts — takeover must also transfer write authority and trigger
  hydration on the new node.
