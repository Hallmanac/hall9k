# CONSUMED (2026-08-20): merged into PLAN.md as decisions #38-#58 (+13 renumber from the
# original #25-#45; the P2P sessions drafted against a pre-fork PLAN ending at #24), section
# 15 rows 12-29, and the section 14 roadmap amendment. Retained as the merge's source record.

# PLAN.md additions — P2P design session (2026-08-18)

Paste-ready fragments. Four decisions-log entries, three open-decision rows plus one amendment,
one roadmap amendment, one AGENTS.md line. Full rationale lives in the new `P2P-DESIGN.md`.

---

## A. Append to §16 (v0 Decisions Log)

> Note on placement: these are post-v0 decisions recorded in the v0 log, following the precedent
> of #12 (locality-aware lease expiry) — recorded now so the foundations carry them, built later.

25. **P2P identity is a two-tier key hierarchy; the peering unit is the project** (2026-08-18, design session; full detail in `P2P-DESIGN.md` §2–§4). **One owner keypair per human** (root of trust, stays cold on the primary machine, signs node certificates and nothing else) and **one node keypair per machine** (generated locally, never leaves, signs every event). The owner key signs each node's public key; a peer verifies two links — event signed by node key N, node key N certified by owner key O — and knows it is Brian's work from Brian's laptop. Per-node revocation is therefore free (lose a laptop, revoke that certificate; the owner identity survives). Keys are **self-sovereign**, generated at `h9k owner init`: no OAuth, works offline, trust root stays owned; a signed GitHub-username attestation is purely additive later. **Rejected: a keypair per project** — it buys unlinkability, the exact opposite of the accountability model (§6.2), and multiplies key management for nothing. **The peering unit is the project, not the node**: a node hosts many projects and authenticates as one owner; an owner may run several nodes; a project may span many owners or one; and **some projects are personal, so peers must never learn they exist** — which forbids wholesale node-to-node peering and puts membership in the project, opt-in per project, with the node as merely a host. **Enrolment** resolves how the owner key signs a remote machine's key without traveling: a short-lived (~1h) token exported from an existing node, carried to the new machine, fed to `h9k init`, carrying node A's addresses, public key, and a shared secret — a one-time ceremony, not a key copy. Its limit is what makes discovery mandatory rather than optional: human-mediated enrolment scales to your own machines and no further, and a token's baked-in addresses go stale on any network change. **Trust and reachability are separate problems** and are solved separately (#26, #27).

26. **Metadata gossips eagerly; payloads materialise lazily on claim; claims belong to the owner** (2026-08-18; `P2P-DESIGN.md` §10–§11). All nodes stay synchronised on **which projects and tasks exist**; discovery artifacts, transcripts, and attachments are **not replicated by default** — claiming a task pulls its content from a peer that has it. This is why §8.1 chose content addressing: the claimer already knows the hashes it needs, and "do you have hash abc123?" is the right primitive either way. **Claiming while the source node is offline holds the claim and syncs when the source returns** — the claim lives in the event log regardless; the payload is only a cache to warm. Implies an *awaiting materialisation* state so an agent is never dispatched into a half-empty working directory (surfacing is open, §15 #12). **Claims are recorded against the OWNER, not the node**: Sarah needs to know *Brian* holds task ABC, not which machine — the node key signs the event (who typed this), the payload records the claiming owner, and a peer resolves node → owner to reject a duplicate claim from the same owner's second machine. `TaskClaimed(Id, NodeId, OwnerId, LeaseGeneration, RunId, ClaimedAt)` already carries exactly this shape; no change needed. Remote lease expiry is unchanged by any of this (#12).

27. **Discovery: one Hall9k-wide network and an announce/expire seed, never a registry** (2026-08-18; `P2P-DESIGN.md` §5–§7). **All nodes join one shared address book regardless of project**, inverting the obvious per-project-rendezvous design: finding a project's peers becomes a *query over connections you already have*, not a criterion for finding peers at all. No per-project setup, strangers work fine, and **the seed never learns which projects exist** — it sees only addresses; membership is proven peer-to-peer with keys. Bootstrap needs something out-of-band, so the binary ships **a hardcoded URL, not an IP** (a URL lets the service move hosts or repoint DNS without a release). The endpoint **announces and queries in one call**: a node POSTs who and where it is, the response is the current list of other announcers; every querying node also populates the table; no curation. **The recorded address is the one the service observes the connection arriving from**, never what the node claims — that is the post-NAT address and port that actually works. **The seed never initiates anything**: nodes re-announce on a ~15-minute heartbeat and entries expire after ~1h of silence. *Rejected: probing nodes for liveness* — it hits every NAT problem the peers have and turns a dumb table into a monitoring service holding state; liveness is the node's job to assert, not the service's to verify. The table is deliberately churning and never complete, just currently-warm. **It is a seed, not a dependency**: nodes cache the address book on disk and use peer exchange thereafter, falling back to the seed only when everything they know is stale — **if the hosting bill lapses, existing networks keep running**. Storage is **in-memory, deliberately** (a few hundred hourly-expiring entries is a cache, not a record; loss on restart refills itself within 15 minutes). **Peer exchange** caps active connections at ~8–10 while the on-disk address book grows well beyond that, and deliberately **diversifies which peer each address came from** — Bitcoin's network-group bucketing as an anti-eclipse measure, and note Bitcoin has *no* locality preference and actively resists one. **Finding a project** is a bounded gossip query over established connections — hop-capped, deduplicated by query ID — and silence is a real answer (nobody reachable has it; retry on the heartbeat). **Rejected**: BitTorrent mainline DHT (throttled or blocked on corporate networks by DPI on its handshake signature, breaking peering exactly where it is needed — the useful inverse lesson being that Hall9k's own protocol is unrecognisable and TLS/443 looks like ordinary HTTPS); git-remote rendezvous (imposes repo overhead on every project owner, and publishes home IPs); Bitcoin's crawler model (works only because Bitcoin nodes are expected to be publicly reachable — developer machines are not). Far-future scale is shape-compatible without protocol change: announce **when lonely rather than on a timer**, with a reachable opted-in minority still announcing, tuned by a "check back in N" field in the announce response.

28. **Reachability: LAN first, hole punching second, relay on 443 as fallback** (2026-08-18; `P2P-DESIGN.md` §8–§9). Try in order of cost and **cache per peer what actually worked**. (1) **Same LAN — mDNS multicast**, no external service, works with no internet uplink at all; full LAN speed makes materialising a large attachment set instant, and the trust check is trivial when both certificates chain to the same owner key. Also what makes the #25 enrolment ceremony pleasant. (2) **Hole punching**: outbound traffic creates a router mapping that briefly admits replies; both nodes learn each other's *observed* external address from the seed, the seed nudges both, and they fire UDP simultaneously — the first packet is dropped (the other side hasn't sent yet, expected), the second arrives at a router that recognises the address it just sent to and lets it through. **QUIC over the punched path** (TLS, streams, reliability for free), keys verified against the project member list, keepalive every 20–30s to hold the mapping; the seed never touches the data. Gets an estimated 70–80% of NAT pairs. (3) **Relay** for the rest. Punching fails on symmetric NAT, blocked outbound UDP (common on corporate/guest networks), and carrier-grade NAT; **the failure is asymmetric** — if the far side's firewall blocks outbound UDP its packet never leaves, so no mapping forms and your correctly-sent packet has nothing to slip through, *your side having done everything right*. You cannot know which case you are in until you try: **attempt the punch, allow 2–3 seconds, fall back quietly** — the user never learns which path was used, they just see the transcript arrive. The relay is a server on a public 443 that both nodes dial **outbound** (universally allowed), which pairs their sockets and pumps bytes both ways **for the entire session** — it is a permanent middleman, explicitly *not* an introducer that steps aside (that is the seed, which is genuinely DNS-shaped). All connections landing on 443 is unremarkable: a port is a doorway, not a channel, and each connection has its own socket. No keepalives needed (real established TCP, no NAT mapping to hold). The relay is dumb by construction — no protocol parsing, no storage — and payloads are encrypted end-to-end between node keys, so it sees ciphertext only. Transports: direct/QUIC when punched, websocket-over-TLS when relayed (HTTP only for the upgrade), same application protocol on top. **Rejected: SignalR for the relay leg** — the hub/RPC model is dead weight for an opaque byte pump, it couples the wire format to a framework, and this is the *exception* path; raw websockets in ASP.NET Core is ~30 lines. (SignalR was stripped during the local-first pivot on the assumption no relay would be needed; a relay proving necessary does not bring it back.) **Rejected: Tor as the node transport** — it would solve reachability outright (onion service per node, dial-out only, stable address behind any NAT, no punching, no churn) but costs 1–2s per round trip, poor payload throughput, and is blocked on many corporate networks by policy; building censorship circumvention into a developer tool is the wrong place to end up. Viable later as a personal-machine fallback. **Hosting**: seed and relay share a codebase (ASP.NET Core minimal APIs, likely Azure Container Apps) but get **separate deployments or at minimum separate scaling rules** — the seed is tiny, bursty, request-response, scales to zero happily, and must pin to one replica (divergent tables otherwise); the relay holds long-lived bandwidth-hungry connections, scales on concurrent connections, and **must not scale to zero** (it would kill live tunnels). If the relay is hammered, discovery must not go down with it.

---

## B. §15 (Open Decisions & Flagged Revisits)

**Amend row 11:**

| 11 | Multi-node coordination: shared Postgres vs. true P2P replication | Evaluate at roadmap #5; leases keep both open. **The P2P branch now has a design** (`P2P-DESIGN.md`, Decisions Log #25–#28) — that does not decide the branch, it removes "we don't know what it would look like" as a reason to avoid it |

**Add rows:**

| 12 | Awaiting-materialisation surfacing: blocked vs. pending vs. quiet retry (log #26) | Unresolved; decide when lazy payload sync is built (roadmap #5) |
| 13 | Claim race tiebreak across owners: deterministic logical timestamp (Lamport counter, ~1h of work; events currently carry only local UTC wall-clock) vs. jitter-backoff | **Deferred deliberately.** Jitter-backoff is not deterministic on log replay, which is awkward for event sourcing; a deterministic tiebreak is the likely answer. Cross the bridge on arrival |
| 14 | Peer wire protocol: message set, gossip framing, project-query shape | Not yet opened; this is the substance of the node-side P2P work |
| 15 | Node certificate revocation distribution, and how peers treat events signed before a revocation | Raised by log #25; unresolved |

---

## C. §14 (Roadmap) — amend item 5

5. **Multi-node**: second machine joins. Start with "several daemons, one reachable/shared Postgres" (leases already make this safe); evaluate true P2P (replicated state, node discovery) only if shared-Postgres genuinely doesn't fit the topology. **If it goes P2P, `P2P-DESIGN.md` §14 has the build order** — keys and enrolment, then LAN-only via mDNS, then seed plus direct, then relay, then gossip; each slice independently useful, the early ones needing none of the later machinery.

---

## D. AGENTS.md — reading order

Add under "Read in order of need":

```
- `P2P-DESIGN.md` — the peer-to-peer layer: identity, discovery, NAT traversal (design only, nothing built)
```

---
---

# Addendum — owner key lifecycle session (2026-08-18, later)

Full detail in `OWNER-KEY-LIFECYCLE.md`, which slots into `P2P-DESIGN.md` as §4.1–§4.8.

## E. Append to §16 (v0 Decisions Log)

29. **The owner private key is recoverable by succession, not by backup** (2026-08-18; `OWNER-KEY-LIFECYCLE.md` §4.1–§4.6). Follows from noticing how little the owner key does: **it signs node public keys and nothing else** — not events, not project joins, not handshakes. So **every node holds the owner *public* key and any node can join a project on the owner's behalf**; the primary is special for exactly one operation, enrolling a machine. Therefore **losing the owner private key is inconvenient, not fatal**: every node keeps claiming, syncing and signing indefinitely, and the only broken capability is enrolling a *new* machine. **Decision: at enrolment, each node generates its own owner keypair and the primary signs a succession statement ("owner key A also controls owner key B")**, stored on the enrolling node and carried in project state — enrol five machines, get five vouched-for heirs, at the cost of one extra signature in a ceremony that happens a handful of times per lifetime. Recovery is "promote a node you still have": no password manager, no paper. **Promotion is deliberate, never automatic** — since nothing is broken there is no failover to detect, hence **no health checks, elections, quorum, or split-brain machinery**. It discovers itself at the right moment: `h9k node invite` on a secondary finds no reachable node holding the owner key, says so plainly, and prompts (*this node can be promoted — proceed?*); on yes it presents its succession statement, announces, mints the token, and the remaining nodes re-enrol against it as ordinary onboarding. The prompt is required — promotion changes the identity root network-wide and must not be a side effect of wanting a new laptop. **Ordering is oldest reachable node**: it needs no extra state and enrolment dates are already signed by the primary, so the order is *verifiable rather than declared*; skip down when the oldest is offline, and the promoted node announces so others stand down. **Floor if there is no chain and no surviving node**: a project member re-invites you with an ordinary project token — one Slack message — at the honest cost that prior work is attributed to an owner key nobody vouches for anymore (history intact, authorship orphaned), which is precisely the argument for minting succession statements by default. **Rejected: copying the owner private key to every node** — every copy is another leak surface, a leaked owner key certifies nodes as you forever (a leaked token dies in an hour), and it collapses per-node revocation since a laptop holding the owner key *is* you. **Rejected: threshold signatures (2-of-3)** — the right instinct and it does solve the failure directly, but it is real cryptographic engineering with thin .NET library support, and it requires two machines online simultaneously to enrol, trading a rare inconvenience for a routine one; disproportionate for a key that signs perhaps five things in its life. **Rejected as the sole answer: manual key backup** — remains possible and is proportionate for a solo project with nobody to vouch for you, but as *the* design it pushes ceremony onto every user; Hall9k manages code, not money.

30. **Two token types; certificates travel with stored events only; pre-P2P history needs a one-off migration** (2026-08-18; `OWNER-KEY-LIFECYCLE.md` §4.2, §4.7–§4.8, §10 amendment). **Node enrolment tokens and project invite tokens are distinct types on the wire.** Enrolment requires the owner private key and causes it to sign a node public key, granting scope over every project the owner belongs to; a project invite requires no owner key, can be minted by any member's node, and merely adds an owner public key to one project's member list. Keeping them separate prevents a project token silently becoming "act as me everywhere." Both are single-use, ~1h TTL, proven by HMAC over the joiner's public key keyed by the token secret. **An inviter need not know the invitee's owner public key beforehand, or that it is Brian at all** — it arrives inside the join request wrapped in the token proof; the token bootstraps exactly one fact, that this owner public key belongs to the person meant to be invited. **Certificates travel with anything that will be repeated by someone else**: transient messages (queries, heartbeats, project questions) carry a signature only, since identity was settled in the handshake and the message dies at the receiver; **stored events carry signature *and* certificate in their persisted form**, because a third peer that never handshook with the originating node must verify independently rather than take the relaying peer's word. **Not built now** — dead weight in every aggregate today; what matters is not foreclosing a signed envelope *around* an event rather than assuming an event is only ever what the local node wrote. **Pre-P2P events are unsigned and this does bite**: history propagates, so a back catalogue synced to a peer gets relayed onward to nodes that will reasonably ask who signed it. A one-off migration at switch-on — retroactive signing (attesting now to what you wrote then) or a hash-chain checkpoint signing the tip once (Bitcoin's block chaining in miniature) — is owed then, not now. **Same-owner sync relaxes #26's lazy-payload rule**: eager metadata / lazy payloads was reasoned about with cross-owner peers in mind, but between two machines of the same owner on a fast link, pulling attachments is essentially free and the laptop is worth having as a genuine mirror rather than a thin index — **same owner on a fast link pulls everything; anything else waits for a claim**, and a newly enrolled node **syncs all of that owner's projects by default** (the certificate is owner-scoped, so which projects sync is a preference, not a trust question).

## F. §15 (Open Decisions) — additional rows

| 16 | Competing promotions: two nodes promoted independently while partitioned, each with a valid succession statement | Peers must accept only one. Earliest-promotion-wins vs. last-writer-loses vs. human resolution. Unresolved, and rare enough to leave so |
| 17 | Pre-P2P event history: retroactive signing vs. hash-chain checkpoint (log #30) | Decide at P2P switch-on; both are cheap, neither is owed in v0 |
| 18 | Succession statement distribution before any project exists (an owner's first node has nowhere to put them) | Probably local-only until first sync; confirm when building slice 1 |

**Amend row 14** (peer wire protocol): frame and interaction model now sketched — request-response vs. push kept distinct; every message carries type, length prefix, and correlation ID for requests; type catalogue (handshake, join, project query, event push, event range, peer exchange) with unknown types ignored for forward compatibility; handshake ordered certificate → challenge-response → version → project statement, with QUIC supplying the transport half. Message-by-message exchange still open. See `OWNER-KEY-LIFECYCLE.md` closing section.

## G. §14 (Roadmap) / `P2P-DESIGN.md` §14 — amend build slice 1

Slice 1 (keys and enrolment) additionally covers **succession statements minted during enrolment** and **`h9k node invite` / `h9k owner promote` with the promotion prompt**. Cheapest to build here and expensive to retrofit: a succession statement wants to be signed at enrolment time by a key that is still alive.

## H. Append to §16 (v0 Decisions Log) — partition handling

31. **Competing promotions reconcile after the fact; duplicated work arbitrates through GitHub** (2026-08-18; `OWNER-KEY-LIFECYCLE.md` §4.9). Closes §15 row 16. **The common case is not a race**: a sleeping primary wakes, hears a promotion announcement it can verify *because it signed the succession statement itself*, and steps down to ordinary node. **Promotion events form a chain**, each naming the key it supersedes, so a node offline across several promotions replays them in order — step-down logic handles a chain, not a single hop. **The hard case is a partition, and a quorum rule does not prevent it**: when a location loses its uplink its *local* network stays healthy, a quorum of the owner's nodes is available, is consulted, and agrees, because from inside a partition both halves look correct. **Rejected: detecting the condition** ("don't promote while offline") — that requires knowing your view is incomplete, which is a liveness oracle and does not exist; an uplink is only one of many reasons two locations stop seeing each other, none reliably distinguishable from a dead primary. **Decision: reconcile by lowest hash of the promotion event.** Both sides compute both hashes from immutable data, agree with no negotiation and **no trusted clock** — wall-clock time being precisely what a partition makes unreliable. This is CAN-bus arbitration (the same trick offered for the claim-race tiebreak, §15 row 13): arbitrary but deterministic priority that losers detect for themselves. Hash rather than raw node ID because it is unpredictable and so ungameable; costs nothing. **Aftermath**: the loser's certified nodes re-enrol under the winner (new certificates, nothing lost), and **a certificate is valid for what it signed while it was valid** — events already signed under the losing key stay verifiable forever, since rejecting them discards legitimate work and voiding the key retroactively would misstate what happened; therefore **the losing promotion event is retained as evidence, not deleted**. **This is not a chain split**: blockchain reorgs erase one history because the two *compete* (same coins, contradictory spends), whereas two machines certifying two laptops doing unrelated work are not in conflict at all — both histories merge, nothing is orphaned. **The one genuine conflict is the same task claimed twice**, which is a claim-race problem at a different layer and needs solving regardless. **GitHub arbitrates**: a branch or PR is a fact both sides can check and creating one is atomic, so whoever's push landed wins — the external-source-of-truth decision paying off rather than new machinery. Two residual shapes: both pushed (two PRs on one task; a human picks — wasteful, not incorrect) and **a task that never reaches a PR** (spike, investigation, discovery), where nothing arbitrates. For that, **the system flags and does not resolve** — on heal, surface the duplicate claim as needing attention; do not pick, merge, or discard, because two conclusions to the same question are two opinions and only a human can say whether they agree, complement, or one is better (plausibly via a Claude Code session reading both and reporting divergence, the same shape as the pre-PR review loop). **Net: the claim race is a cost problem, not a correctness one.** The inverse case — zero promotable nodes — is already covered by the re-invite floor (#29).

32. **Sync shape confirmed, and repositories materialise lazily** (2026-08-18; `OWNER-KEY-LIFECYCLE.md` §10 amendment). **Event log eagerly, everywhere** — projects, tasks, claims, runs, discovery metadata; Postgres rows, megabytes not gigabytes, so syncing every project of an owner by default is cheap. **File payloads lazily** (#26 unchanged). **Git repositories clone lazily**, triggered by the same claim. **Gap this exposes**: `h9k` assumes the repository is already present because you are standing in it, but cross-node claiming means a machine can hold the complete event log for a project it has never cloned. So a **materialisation step sits ahead of work-tree creation** — do I have this repo? no → clone from the project's GitHub connection → create the work tree as normal. Same lazy-materialisation shape as attachments, with git doing the fetching. `init` is never lazy and never needs to be: when a project is created the repository already exists locally, so **only cloning is ever lazy**.

## I. §15 (Open Decisions) — amend and add

**Amend row 16** (competing promotions): **Resolved** — see Decisions Log #31. Reconcile after the fact by lowest promotion-event hash; losing certificates stay valid for what they signed; duplicate task claims arbitrate through GitHub; payload-only duplicates surface for a human.

**Add:**

| 19 | Duplicate-claim surfacing on partition heal: what the notification looks like and where it lives in `h9k` output (log #31) | Likely shares a mechanism with row 12 (awaiting materialisation) |
| 20 | Repo materialisation ahead of work-tree creation on a node that has never cloned the project (log #32) | Build alongside lazy payload sync (roadmap #5) |

## J. Append to §16 (v0 Decisions Log) — losing work

33. **Losing runs are attached, not merged, and an agent triages them** (2026-08-18; `OWNER-KEY-LIFECYCLE.md` §4.10). Closes §15 row 19. **First, a framing correction**: there is no "primary" in the user's world — nobody walks up to a machine caring what kind of node it is, they walk up and start working. "Primary" names exactly one capability (holding the owner private key) exercised the handful of times a machine is enrolled. So **the key reconciliation of #31 is silent plumbing**: a losing node finds out at the one moment it matters, when `h9k node invite` fails and offers the other machine or promotion. **The reconciliation that ever speaks is the one about work**, and it must surface per-node, where the human is standing. **Losing runs are attached, not merged**: a task has one identity and two runs against it, both stored as they arrived, each keeping its own identity — no replaying, no interleaving, nothing rewritten or discarded. This works because **ordering across nodes was never a single true sequence**; each run is internally ordered, the runs are concurrent, physical log order is arrival order and an implementation detail, and distinct stream IDs mean there is no collision to resolve. The partial order is admitted rather than flattened. **Consequence: the storage layer needs no partition-specific machinery at all** — the only place the ambiguity was ever real is the *lease*, which is exactly what the #31 hash tiebreak settles. **The tiebreak decides who had authority; the events record what happened either way.** Nothing combines at the data level; what combines is the *conclusion*, and that is a **third artifact** — an appended interpretation, never an edit to history. **The winner proceeds normally** (its branch is the branch, its PR is the PR) and **the losing run is flagged as unreviewed, non-blocking** — optional curiosity if the winner already merged, potentially better work if the task is still open, since shipping the winner purely because its push landed first is arbitrary. **Nothing is deleted**: the work, transcript and diff stay; a losing run is a second attempt someone might want to read, not a duplicate to clean up. **An agent triages**, and this is Hall9k reconciling itself: a losing claim **materialises a review task** carrying both attempts as payload, an agent picks it up through the ordinary queue and reports where they diverge. Most of the time they converged — two agents on the same task and codebase reach near-identical results, differing in nuance not outcome — so the agent says so, the winner stands, and no human reads two transcripts to learn nothing; **absorbing that common case is most of the value**. **Escalate only genuine divergence**, which is rare enough to be cheap. **The agent recommends; it does not decide** — discarding your own work wants a human nod and asking costs nothing, which keeps this at the edge of supervised autonomy rather than past it. Same shape as the pre-PR independent review loop (#20–#24): an agent reading work it did not do and reporting rather than acting.

## K. §15 (Open Decisions) — amend

**Amend row 19** (duplicate-claim surfacing): **Resolved** — see Decisions Log #33. A losing claim materialises a review task carrying both runs; an agent triages; only genuine divergence reaches a human; both runs retained; the winner is never blocked. Residual: the exact wording `h9k node invite` uses when it discovers this node's owner key lost a reconciliation.

## L. Append to §16 (v0 Decisions Log) — transport and hosting

34. **QUIC everywhere, no HTTP anywhere in the P2P layer** (2026-08-18; `TRANSPORT-AND-WIRE-PROTOCOL.md` §1). **All peer traffic is QUIC over UDP via `System.Net.Quic`** — in-box .NET on top of MsQuic, no third-party package. We *use* QUIC and do not implement it: packet layout, encryption, handshake, retransmission, congestion control and stream multiplexing belong to the library; the only thing Hall9k writes is the framing inside the streams (#36). Two layers that had been conflated and now are not: **QUIC/TLS provides a private authenticated pipe; the Hall9k protocol decides what the two nodes say over it.** **Why not TCP+TLS**: TCP's single global order means one lost packet stalls everything behind it, whereas QUIC's independent streams let a large back-catalogue sync run alongside a live push; TCP+TLS 1.2 costs three round trips before a useful byte moves, QUIC merges the transport and crypto handshakes into **one**, which also means **certificate exchange happens as part of connecting rather than as a protocol message we design**; a QUIC connection is identified by connection ID rather than the IP/port four-tuple, so **connection migration** carries a sync across a network change with **no resume logic to write**; and QUIC's logic ships in userspace with the next deployment rather than waiting a decade for kernels (UDP itself is still kernel — "userspace" here means privilege level, not a user account). **And UDP is what makes hole punching possible at all**: a router notes outbound traffic and thereafter admits replies, and *that note is the hole* — UDP's statelessness, normally a weakness, means the router does not care whether the packet ever arrived. TCP insists on a SYN handshake in strict order, so simultaneous dials drop both SYNs. TCP's only alternatives are relay (both parties dial *out*; the relay never dials either), manual port forwarding, or UPnP — and a UPnP-opened port is open to *anyone*, which is why routers ship it disabled, with carrier-grade NAT defeating it regardless: **worth trying as an optimisation, never worth depending on**. **Therefore no ASP.NET Core**: the seed listens on a UDP socket, there is no URL, route table, middleware or controller, and the only reason to want ASP.NET Core would be a browser, of which there is none. **The handshake is four steps and one round trip**, and Hall9k writes code for exactly one of them: the Initial packet carries an invented connection ID, the version, an **ephemeral** public value and cipher suite list, padded to ~1200 bytes to prevent amplification, with the payload encrypted under keys derived from the connection ID (format consistency, not secrecy) and **no certificate or identity in it at all**; the reply adds her connection ID, her ephemeral value, the chosen suite, her node certificate and a signature over the handshake so far; **step three is our hook** — the library verifies cryptographically and then asks *do you accept this?*, and our answer is *does this chain to an owner key I recognise as a member of a project I care about?*, with rejection closing the connection silently; step four confirms. **This is a TLS channel and mutual identity verification in a single round trip.** Standard TLS lets the client stay anonymous (that is web browsing); Hall9k configures QUIC to **require a client certificate**, a setting rather than a message. 0-RTT reconnection exists and is attractive later. **Two keypairs, two jobs, and nothing is encrypted with either private key**: the **ephemeral** keypair (X25519, microseconds, generated per connection and discarded) does **key agreement**, and the **node** keypair does **signing**. Key agreement means each side sends only a public value, then combines *its own private half with the other's public half*, and the mathematics yields **the same number on both sides without anything secret crossing the wire**; discarding the ephemeral keypair gives **forward secrecy**, so recorded traffic stays unreadable even if a node key leaks years later. Signing is the opposite in intent — hash, transform with the private key, and the output is **public evidence anyone can verify** — and **verification does not recompute the signature** (it cannot, that would need the private key): signature, hash and public key go into a verify function that answers yes or no. Underneath: the keys are two halves of one relationship, built on an operation easy forwards and effectively impossible backwards, so the public key is *derived from* the private one and anything the private key does leaves a checkable fingerprint. **A certificate is a signed statement, not an actor** — node public key, plus the owner key's signature over it, plus metadata; the certificate signs nothing, the node *private* key makes the handshake signature. And the receiver needs **the owner public key it already holds from out-of-band join**, not one sent over the wire, which would be circular. That prior possession is the whole root of trust.

35. **The seed and relay are ordinary nodes, hosted on Azure Container Instances** (2026-08-18; `TRANSPORT-AND-WIRE-PROTOCOL.md` §2). **Every node can be both seed and relay**, so deploying "a seed" is deploying the ordinary daemon with a public address and capabilities enabled — one binary, one code path, and the infrastructure runs the same code every user runs. The hosted node is a **flavour with no owner and no project data**: it participates in discovery without participating in work. **Seeding is nearly free; relaying costs your bandwidth indefinitely**, so they are not the same commitment — and most home nodes cannot relay anyway, sitting behind exactly the routers that created the problem, which is precisely why a hosted node exists. **Relay capability is auto-detected, not a checkbox**: a node asks the seed *can you reach me at this address?* and advertises only on yes, so NAT'd home nodes self-select out without the user knowing there was a question. **Throttled** to a configurable share of uplink so someone else's partition healing cannot saturate your connection. **Distributed passively — rejected: actively rerouting relay load**, because dynamic load-shifting needs coordination and a view of global state while **peer exchange already solves it**: a slow relay simply loses out as peers pick other paths. Home relaying stays possible via manual port forwarding plus dynamic DNS, at more friction than most will accept. **Hosting is Azure Container Instances.** **Rejected: Container Apps and Functions — inbound UDP is a hard no** (ACA ingress passes HTTP, HTTPS and TCP only; outbound UDP works, inbound does not), which is disqualifying for a QUIC listener. ACI has no ingress layer: point it at an image, declare ports including UDP, get a public IP, pay per second. Given up: autoscaling, managed certificates, custom domains, revisions — none of which a UDP broker needs. **DNS**: static IP, an A record, DNS managed yourself; if the instance moves, update one record. **Security**: ACI gives an NSG and little else, but **the attack surface is already tiny** — one UDP port, a custom binary protocol, and a handshake rejecting unknown peers before real work; a WAF protects HTTP and would do nothing here, so **the real security is in the protocol**. **DDoS is the one legitimate concern** since UDP suits amplification; mitigated by NSG rate limiting, QUIC's mandatory round trip and padded Initial, and Azure's free platform-level volumetric protection — not naked, not as wrapped as ACA, an accepted tradeoff. **No Kubernetes** (ACI is explicitly the no-Kubernetes option). Billing has more line items than ACA but is tiny in absolute terms and likely cheaper in practice. **Scale-to-zero is irrelevant and would be wrong**: a seed that is not reachable is useless, so always-on is correct. **To verify before committing**: no ACI deprecation notice was found (results surfaced only unrelated Reserved VM Instance retirements and ACA preview-feature retirements), but confirm independently.

36. **The wire protocol: six messages, three shapes** (2026-08-18; `TRANSPORT-AND-WIRE-PROTOCOL.md` §4–§5). Closes §15 row 14 and `P2P-DESIGN.md` §13 Q3. **The handshake is not among the six** — QUIC already did it. **Framing**: every message carries a type byte and a length prefix, requests additionally a correlation ID, and **unknown types are ignored rather than erroring** so a newer node cannot break an older peer. **Join request** carries project identifier, joiner's owner public key and node certificate, wrapped in proof of the invite token, and is **synchronous** because the invite already constitutes approval. *(An earlier draft had it pending a human decision — wrong: without an invite you would not know the project identifier or which node to ask.)* **An invite token carries** the project identifier, **Sarah's owner public key** (the critical field — without it you cannot verify her node certificate during the handshake and would be trusting whoever answered), addresses, an expiry, and her owner key's signature over the whole thing. Note it works at **two layers**: the owner public key is loaded **before connecting** so the QUIC verification hook can say yes, and the token is then presented **inside** the join message — **handshake mechanics are QUIC's, the trust decision is ours, and the invite is what supplies it**. **Project query** returns project identifiers plus a **version marker** per project (highest sequence per stream, or a hash), enough to answer *am I current?* without transferring anything. **Event range request** names the point as **the last sequence number held per stream, never a timestamp, because clocks lie**; an empty position set degenerates gracefully to *from the beginning*, so bootstrap and catch-up are the same message in two modes. Two properties fall out: **position is derived entirely from what you hold**, so you need not remember who you synced with or when — correct after a crash and when syncing from a different peer — and it is **naturally idempotent**. **Event push** is fire-and-forget with no acknowledgement, which is safe because **the range request already covers gaps**: *push is the fast path, sync is the correct path*, and reliability is unnecessary in the push because correctness lives elsewhere. **Peer exchange** trades node identifier, last known address and last-seen time as **hints, not facts** — stale addresses just fail and are discarded — making the mesh self-healing and propagating relay capability with no coordinator. **Gossip is project-shaped**: a push travels to the sender's project peers, who can read and verify it, and onward to theirs, so **you need no connection to Sarah, only to someone who eventually has one** — and it explicitly does *not* travel through nodes outside the project, who hold no project keys and would be forwarding opaque unverifiable blobs, a spam vector for no benefit. **Cross-project relaying is for discovery only, never payloads.** **The whole event travels** (a claim is a few hundred bytes; making recipients dial the origin would defeat gossip). **Owner-to-owner enrolment syncs wholesale; a cross-owner join concerns one project** — same messages, different scope. **Connection maintenance**: one project connection is the floor and is genuinely sufficient for correctness, but fragile, so **target two or three warm connections** and dial the rest on demand, letting peer exchange fill gaps; a QUIC connection is cheap but not free (keepalives, state, sleeping laptops), though migration means a warm one survives a network change. **The two-owner case is special**: no redundancy, so both sides reconnect on startup with backoff, and **the seed matters far more** since it may be the only possible introduction — and if she is simply offline you just work, events queuing until she appears, which is the entire point of local-first. **The six are a floor, not a ceiling**: revocation announcements, succession distribution and awaiting-materialisation surfacing all need messages and are deliberately left to be discovered while building.

## M. §15 (Open Decisions) — amend and add

**Amend row 14** (peer wire protocol): **Resolved** — see Decisions Log #36 and `TRANSPORT-AND-WIRE-PROTOCOL.md` §5. Six message types with type/length framing, correlation IDs on requests, unknown types ignored. Remaining message types will be discovered while building, not specified now.

**Amend row 13** (claim race tiebreak): still deferred, but note this is now the **third** flag that the promotion reconciliation (#31) and the claim race want **one shared deterministic-ordering mechanism**. Worth deciding together rather than twice.

**Add:**

| 21 | **Networks that block outbound UDP entirely.** `P2P-DESIGN.md` §8.4 put the relay on TCP/443 precisely for these; a QUIC relay cannot help, since reaching it needs the blocked thing | **Open, and a real gap rather than a detail.** Options: a TCP/443 fallback transport (a second wire format for the exception path), accepting those networks as unsupported, or the Tor fallback already noted in §12 |
| 22 | 0-RTT reconnection for known peers (log #34) | Attractive for returning nodes; minor replay caveats. Not needed for v1 |
| 23 | Verify Azure Container Instances is not deprecated before building on it (log #35) | Brian to confirm independently |

## N. `P2P-DESIGN.md` — superseded sections

Decisions #34–#36 supersede parts of `P2P-DESIGN.md`. The superseded text is worth keeping in place with a pointer rather than deleting, because the *reasoning* in it mostly still holds and only the conclusion moved. Full table in `TRANSPORT-AND-WIRE-PROTOCOL.md` §7; in brief: **§5.2** (seed addresses move to config with an overridable default list, and the seed is not an HTTP endpoint), **§8.4** (relay is an ordinary QUIC node, not a websocket service — the SignalR rejection now doubly true since there is no HTTP leg at all), **§8.5** (all three paths are QUIC), **§9** (ACI, not ASP.NET Core on Container Apps; the seed/relay contrast table still describes the two *workloads* well even though they share a process), **§12** (add three rejections: active relay load rerouting, ASP.NET Core/HTTP anywhere in the P2P layer, and ACA/Functions for lack of inbound UDP), **§13 Q3** (closed), **§14** (slice 3 becomes "deploy the daemon to ACI with public-address flags"; slice 4 shrinks to auto-detection, throttling and the pump).

## O. `OWNER-KEY-LIFECYCLE.md` §4.10.5 — residual dissolved

The residual left open in §4.10.5 — the wording `h9k node invite` shows when this node's owner key lost a reconciliation — **is dissolved, and no copy is needed.** A non-primary node should not *fail* at minting; it should **reach the node holding the owner key and act on its behalf**. Minting is a request, and where the key physically lives is invisible. The only failure that can surface is *"cannot reach any machine holding your owner key"*, which is the promotion prompt already designed in §4.5. So **losing a reconciliation surfaces nowhere** — exactly consistent with §4.10.1's finding that key reconciliation is silent plumbing. Also amends §15 row 19's residual note.

---

# Addendum — UDP-blocked fallback (2026-08-19)

## P. Append to §16 (v0 Decisions Log) — TCP fallback transport

**#37 — A TCP/443 fallback transport for UDP-blocked networks.** Some corporate and guest networks
drop outbound UDP entirely, killing direct, punched, and relayed QUIC alike. Rather than treating
those networks as unsupported, peers on such a network dial outbound to a relay over TLS on 443 and
the relay pumps bytes between them. *Rejected: accepting those networks as unsupported (corporate
networks matter); Tor (blocked by policy on many of the same networks).* Inbound UDP blocking is
already handled by punching; **outbound** blocking is the wall this addresses. See
`TRANSPORT-AND-WIRE-PROTOCOL.md` §9.1–§9.2.

**#38 — The fallback path is single-stream and sequential.** Multiplexing existed so a large sync
could not head-of-line block a live push. On the degraded path that is acceptable: correctness
matters, latency does not much, and the push/sync split already recovers anything delayed. Framing
and message set are unchanged — this is a second transport, not a second wire format. See §9.3,
§9.9.

**#39 — Ordinary nodes may volunteer as full relays; TCP fallback capability requires a DNS
hostname.** TLS on 443 needs an SNI-valid certificate, and IP-address certificates are effectively
unavailable; plain TCP would sidestep this but stops looking like HTTPS and may be killed by
inspecting firewalls. So a volunteered relay advertises either a hostname (eligible for fallback
relaying) or an IP only (QUIC relaying only). The QUIC relay path needs no certificate at all. See
§9.5.

**#40 — The seed relay is the fallback directory and filters peer exchange by TCP capability.**
Volunteered relays are learned via gossip, which runs over QUIC — so a UDP-blocked node cannot
learn the relays that would help it. Candidates therefore come from the seed (always TCP-capable)
plus relays cached from earlier permissive networks. The never-connected-successfully gap is closed
by the seed preferentially handing out TCP-capable peers **when the requesting node arrived over
TCP**. A filter on the existing peer exchange message, not new machinery. See §9.6.

**#41 — The relayed TCP pipe carries its own authentication and encryption.** TLS on 443
authenticates the relay's hostname only, and the relay terminates both legs, so it holds plaintext.
A two-step inner exchange fixes this: (1) each side sends ephemeral public key, node certificate,
and a fresh nonce; (2) each side signs a hash of its own ephemeral key, the peer's nonce, and the
peer's ephemeral key with its node private key. This defeats relay key substitution (it cannot sign
over the substituted key) and replay (the nonce is fresh). Verification is the same check as the
QUIC hook: does the certificate chain to a recognised owner key. Rejection is silent. See §9.8.

## Q. §15 (Open Decisions & Flagged Revisits) — amend

- **Row 21 (UDP blocked outright) — RESOLVED** by #37–#41. A TCP/443 fallback transport, single
  stream, with its own inner handshake.
- **New row 24:** Fallback relay pool size. The DNS-hostname requirement makes volunteered fallback
  relays rare, leaving the seed as the practical single point for UDP-blocked nodes. Acceptable for
  v1; revisit if fallback usage is material.
- **New row 25:** Cached relay staleness. Volunteered relay addresses cached from permissive
  networks may be dead by the time they are needed. No expiry or refresh policy designed.

## R. `TRANSPORT-AND-WIRE-PROTOCOL.md` §8 — amend

Open item 1 (UDP blocked outright) is **closed** by §9. Items 2–5 stand, including the tiebreak
question (now flagged a fourth time) and independent verification of ACI's deprecation status.

## S. `P2P-DESIGN.md` — partial un-supersession

§8.4's original TCP/443 relay placement is **vindicated for the exception path**. The mechanism
there (websocket-over-443 in ASP.NET Core, SignalR rejected) remains superseded — the fallback is
raw TLS with the §5.1 framing inside, no HTTP — but the *reasoning* for choosing 443 was correct and
is now reinstated as §9.2.

---

## T. PENDING TASK — consolidate into one document

**Decided 2026-08-19, not yet done.** Rewrite `P2P-DESIGN.md`, `OWNER-KEY-LIFECYCLE.md`,
`TRANSPORT-AND-WIRE-PROTOCOL.md` and this file's design content into **a single coherent design
document** reflecting only the end state.

Rationale: the artefact is being handed to Claude Code to scope, refine, and break into tasks and
stories. Making it reconcile amendment tables against superseded prose is a waste of its attention
and a source of error.

Requirements:
- **End state only.** No amendment tables, no "superseded in mechanism, retained in reasoning."
- Rejected alternatives **kept**, but as a clearly-marked section — they carry real reasoning and
  prevent re-litigating settled ground.
- Supersedes: `P2P-DESIGN.md` §5.2, §8.4, §8.5, §9, §12, §14 amendments; sections N and S of this
  file.
- **Blocked on:** the shared tiebreak decision (§15 row 13 / promotion reconciliation #31), since it
  touches the design directly. Settle that first, then write once.

---

# Addendum — the shared tiebreak (2026-08-19)

## U. Append to §16 (v0 Decisions Log) — one tiebreak mechanism

**#42 — Hash arbitration is the single tiebreak mechanism, used everywhere.** Both contested
orderings — the claim race (§15 row 13) and promotion reconciliation (#31) — resolve by taking the
hash of the competing events and picking the lowest. One mechanism, one code path, no clocks to
reason about.

*Rejected: Lamport logical clocks and hybrid logical clocks* — both capture causality more
faithfully, but add per-node counters, exchange rules, and a second concept to reason about, for a
benefit the recovery path already absorbs.

*Rejected: "most recent event stream wins"* — this presupposes exactly what does not exist, a shared
notion of time. A causal variant (**whichever stream already contains the other's event is
provably later**) is genuinely stronger evidence than a hash, and is noted here as a possible future
refinement, but it only helps when one side actually observed the other and so cannot stand alone.
Deliberately set aside in favour of simplicity.

**Accepted cost, stated plainly:** hash arbitration is *arbitrary*. A stale claim can beat a fresh
one, and the better piece of work can lose. This was already accepted for partition handling; the
recovery path is identical either way — the loser's work is **triaged, never destroyed**.

**#43 — The winner reviews the loser's work.** Triage is not merely salvage. When a claim race or
promotion reconciliation is resolved:

- The **losing branch survives triage** — it is preserved, not deleted.
- Something **records that a review is owed**: the winner should examine the loser's diff and
  reasoning and fold in anything worth keeping.
- The review itself is an **agent task**, not platform machinery. Both the losing diff and its
  reasoning exist; hand an agent both and ask it to incorporate the improvements.

The design obligation is only to *make this possible*: preserve the losing branch, and record the
outstanding review. Since the tiebreak is arbitrary, the losing approach is as likely to be the
better one — this is what recovers that value.

## V. §15 (Open Decisions & Flagged Revisits) — amend

- **Row 13 (claim race ordering) — RESOLVED** by #42. Hash arbitration, same as partitions.
- **Promotion reconciliation tiebreak (#31) — RESOLVED** by #42. Same mechanism.
- **`TRANSPORT-AND-WIRE-PROTOCOL.md` §8 item 3 — CLOSED.** Flagged four times; the answer is that
  the two tiebreaks *are* one mechanism.
- **New row 26:** Causal ordering as a refinement. "Whichever stream contains the other's event is
  provably later" could pre-empt hash arbitration where the evidence exists, falling back to the
  hash otherwise. Not in v1.
- **New row 27:** Where the outstanding-review obligation is recorded, and how it surfaces. Related
  to the unresolved awaiting-materialisation surfacing question.

## W. Consolidation task — unblocked

Section T's blocker is cleared by #42. The consolidated design document can now be written.

---

# Addendum — Postgres provisioning and install UX (2026-08-19)

## X. Append to §16 (v0 Decisions Log) — the database is a connection string

**#44 — Hall9k requires a Postgres connection string, and takes no position on where Postgres runs.**
The container in `docker-compose.yml` is a *convenience*, not a requirement. Docker, Podman, Apple's
container framework, a native `apt install postgresql`, Homebrew, or an existing server the user
already runs are all equally valid — Hall9k needs a reachable Postgres and credentials, nothing more.

Rationale: the install cliff was assumed to be Docker, and on inspection it is not. Anyone who has
Claude Code installed and authenticated, plus `git` and the `gh` CLI, is comfortably over the Docker
bar. And on Linux a container is arguably the *odd* choice, since Postgres is a package; macOS has
Homebrew for the same reason.

**Remote Postgres is supported but is not the default, and the docs must say why.** A database in
someone else's cloud quietly forfeits the local-first promise — offline capability goes away. Allowed
because it costs nothing to allow and some people genuinely have this set up already; never suggested.

*Note for the P2P future:* two people pointing at the **same** remote Postgres are sharing a store,
not peering. That is the shared-Postgres topology already contemplated in §15 row 11 / roadmap #5, not
a defect.

**Rejected: embedding Postgres in the product.** Investigated 2026-08-19. The .NET options
(`MysticMind.PostgresEmbed`, `EmbeddedPostgres`) are small community projects aimed primarily at
integration testing, thin on maintenance, and some require a Visual C++ redistributable on Windows.
There is an open request asking Npgsql to provide an official embedded package, which is itself
evidence that none exists. Not something to stake a product's install path on.

**#45 — Install stays boring; a doctor check does the teaching.** Installation puts binaries on the
PATH and nothing else — no database prompt, no provisioning, no guessing. This extends the principle
already settled in `backlog/12-daemon-install.md` (install registers no service, starts nothing).

*Rejected: prompting for a connection string during install* — it asks a question most users cannot
answer yet, at the least informed moment. *Rejected: guessing a local default silently* — it converts
a clear setup step into a confusing failure later, at a moment the user cannot connect back to the
install.

Instead, **the first command that needs a database runs a check**, and that check answers four
questions in order:

1. **Is a connection string configured at all?** If not, that is the entire answer; nothing else
   matters yet.
2. **Is it reachable?** Distinguish *nothing listening on that port* from *reached it, credentials
   rejected* — completely different fixes, and conflating them is the usual sin.
3. **Is the schema present and current?** Marten can create its own tables, so this is mostly an offer:
   *shall I set that up?*
4. **Only if (1) found nothing — what is available?** Is a container runtime running; is there a native
   Postgres on the standard port; **is there a stopped `hall9k-postgres` container from a previous
   session.** That last case is the nicest possible outcome: *your database exists, it is just not
   running* is a one-line fix.

**The same check works forever, not only at install.** Database moved, container stopped, credentials
rotated, laptop reimaged — same diagnostic, same teaching message. It is not an onboarding wizard; it
is a permanent piece of the CLI.

**Placement consequence:** the check must work while the daemon is down, so **it lives in the CLI, not
the daemon.** This sits slightly awkwardly against the thin-CLI rule (§16 #8 — no Wolverine host
cold-start on every invocation), but a raw Npgsql connection attempt is cheap enough that the rule
survives intact.

## Y. §15 (Open Decisions) — add

| 28 | Two provisioning paths exist today — the Aspire AppHost manages its own Postgres for the dev loop, while `docker-compose.yml` serves installed mode. `backlog/12-daemon-install.md` does not mention Postgres at all | Decide whether the doctor check (#45) unifies these or whether they stay deliberately separate. Flagged on reading the repo 2026-08-19 |
| 29 | Where the connection string is configured, and its precedence order (environment variable, `~/.hall9k/config`, per-project override) | Needed before #45's check can report anything useful |
