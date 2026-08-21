# P2P layer — Claude Code kickoff prompt

Paste this into a fresh Claude Code session with `HALL9K-P2P-DESIGN.md` in the repo.

---

## The prompt

You are picking up the peer-to-peer layer for Hall9k (roadmap #5). Read
`HALL9K-P2P-DESIGN.md` in full before doing anything else, plus `PLAN.md` §6.2, §10, §14, §15 and
§16 (decisions #38–#56), and `AGENTS.md`.

**Nothing in that design is built.** It is the output of four long design conversations and it has
not survived contact with the codebase. Your first job is to attack it, not to implement it.

### Step 1 — challenge the design before writing any code

Work through the design and tell me where it is wrong, underspecified, or more expensive than it
looks. Specifically:

- **Where does it collide with what already exists?** `OwnerAggregate` and `NodeAggregate` are thin
  and carry no key material today. `NodeBootstrap.EnsureAsync` registers Owner (from git config),
  Node (keyed on `Environment.MachineName`) and a default GitHub connection. Does the design's
  identity model fit those, or does it want them reshaped? Say so now rather than discovering it in
  slice 1.
- **Where is a decision load-bearing on an assumption nobody checked?** Flag anything that would be
  expensive to reverse. Two were investigated on 2026-08-19 and both are now partly settled — treat
  the residuals as live:
  - **The QUIC verification hook exists** (§8.4, verified note). Residual: whether rejection is
    observably silent, and the `QuicListener` issue that can surface a connection which already failed
    client-certificate validation. The accept path must re-check.
  - **Marten distinguishes stream `Version` from store-global `Sequence`** (§12.6). The sync position
    is stream ID plus `Version`; `Sequence` is local and must never cross the wire. Residual: confirm
    the store is not running in `EventAppendMode.Quick`, which does not populate `Version` at append
    time and would break the position marker outright.
- **What is missing entirely?** The six messages are explicitly a floor. Revocation distribution,
  succession statement distribution, and awaiting-materialisation surfacing all need messages and
  none are designed (§17 items 1, 3, 4).
- **What is over-designed for v1?** The design was written without an implementation budget. If a
  slice can be cut or deferred without losing the shape, say which.

Do not soften this. A design that survives review unchanged usually means the review was shallow.

### Step 2 — verify the external assumptions

Two facts in the design were never confirmed and both are load-bearing:

1. **Azure Container Instances is not deprecated** (§11.4, §17 item 11). Check current Azure docs.
2. **`System.Net.Quic` is viable on the target platforms.** The API surface and the
   client-certificate configuration are confirmed present (§8.4). Still to check: **MsQuic
   availability on the actual target platforms**, including Windows-via-WSL and whichever Linux
   distributions matter, since MsQuic is a native dependency rather than pure managed code.

If either is wrong, the affected decisions need reopening before any code is written.

### Step 3 — propose the slice 1 breakdown

Slice 1 is **keys and enrolment** (§15): owner/node keypairs on the existing aggregates, certificate
chain, enrolment token, signed events, succession statements minted during enrolment, and
`h9k node invite` / `h9k owner promote` with the promotion prompt.

It is deliberately **pure local work with no network**, which makes it the right place to start and
the cheapest place to be wrong.

Produce a task breakdown with:

- one task per independently shippable unit, sized to a single agent run where possible;
- **acceptance criteria** for each, written so they can be verified without reading the diff;
- explicit dependencies between tasks;
- a note on which tasks are reversible and which are not.

Do not create the tasks in `h9k` yet. Show me the breakdown first.

### Step 4 — only then, scaffold

After I have agreed the breakdown, propose the solution structure — projects, namespaces, where the
crypto lives, how signing is injected so it can be tested without real keys.

### Standing constraints

- **This work goes through `h9k`.** Per the flip (2026-08-16), new work is added with `h9k task add`
  and manual coding needs a stated reason.
- **No `Co-Authored-By` trailers, no bot attribution.** Work is authored by the human.
- **Never guess at unobserved facts.** If you have not read the file, say so and read it. If a design
  claim cannot be verified, flag it as unverified rather than restating it as settled.
- **`HALL9K-P2P-DESIGN.md` supersedes** `P2P-DESIGN.md`, `OWNER-KEY-LIFECYCLE.md` and
  `TRANSPORT-AND-WIRE-PROTOCOL.md`. If those files are still present, they should be deleted — do not
  reconcile against them.

### What I want out of this session

A design I trust more than the one I walked in with, and an agreed slice 1 breakdown. Not code.
