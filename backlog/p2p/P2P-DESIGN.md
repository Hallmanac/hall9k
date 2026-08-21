# Hall9k — Peer-to-Peer Layer Design

Drafted 2026-08-18 (Brian + Claude, design session). **Nothing here is built.** This is the
shape of roadmap #5's "true P2P" branch, recorded while the reasoning is fresh so the v0
foundations (globally unique IDs, owner-as-entity, leases, content-addressed storage — PLAN.md
§6.2, "the P2P down-payment") are known to be sufficient. Binding summaries live in the
Decisions Log (PLAN.md §16 #25–#28); this document carries the rationale, the rejected
alternatives, and the mechanics.

Read alongside: PLAN.md §6.2 (accountability), §8.1 (content addressing), §10 (identity &
connections), §14 roadmap #5, §16 #7/#12 (leases and locality-aware expiry).

---

## 1. Two problems, deliberately separated

Underneath everything, peering is: **find an address and port that will accept your
connection**, and **be certain whoever answers is who you expect**.

- **Trust** — who is this, and are they allowed here. Solved entirely by keys (§2). Already
  half-built: `TaskClaimed` carries `OwnerId` alongside `NodeId`, and Owner/Node are
  first-class aggregates.
- **Reachability** — can bytes physically get from here to there. Solved by discovery (§5–§7)
  and NAT traversal (§8). Entirely greenfield.

Conflating them is the classic mistake. The enrolment token (§4) is trust and does nothing for
reachability; the seed (§5) is reachability and proves nothing about trust. Keep them apart.

---

## 2. Identity: a two-tier key hierarchy

**One owner keypair per human. One node keypair per machine. The owner key signs each node's
public key, producing a certificate.**

- The **owner key** is the root of trust. It stays on the primary machine and stays cold — it
  signs node certificates and nothing else.
- The **node key** is generated locally, never leaves the machine, and does all day-to-day
  event signing.
- A peer verifies two links: this event was signed by node key N, and node key N was certified
  by owner key O. Therefore this is Brian's work, from Brian's laptop.

Consequences worth having:

- **Per-node revocation is free.** Lose a laptop, revoke that certificate; the owner identity
  and every other node survive untouched.
- **Node identity is global**, not per-project — which is exactly what makes the shared
  discovery network in §5 work.

**Self-sovereign.** Keys are generated locally at `h9k owner init`. No OAuth, no account, works
offline, and the trust root stays owned. A signed "this owner is GitHub user X" attestation is
purely additive and can come later.

**Rejected: a keypair per project.** It buys unlinkability — the ability for observers not to
correlate a person's activity across projects. That is the *opposite* of what an accountability
model wants (§6.2: every node belongs to a human). It also multiplies key management by the
number of projects for no benefit anyone asked for.

---

## 3. The peering unit is the project, not the node

The topology, stated plainly:

- a node hosts many projects;
- a node authenticates as exactly one owner;
- an owner may run several nodes;
- a project may span many owners (a team) or exactly one (Hall9k itself);
- **some projects are personal and peers must never learn they exist.**

That last point settles it. If peering were node-to-node wholesale, connecting to a colleague
would leak the existence of every private project on the machine. So membership lives in the
**project**, sharing is opt-in per project, and the node is just a host.

This is why the shared discovery network (§5) is safe: the address book is global, but what a
node will *talk about* is scoped per project and gated by membership.

---

## 4. Enrolment: how a new machine gets certified

The problem: the owner key must sign a new node's public key, but the owner key must not travel.

**A short-lived enrolment token.** Export it from an existing node (~1h TTL), carry it to the
new machine, feed it to `h9k init`. It carries node A's addresses, node A's public key, and a
shared secret — enough to authorise the new node's key over a channel node A can verify. A
one-time ceremony, not a key copy.

On a LAN this is genuinely pleasant: the new machine multicasts (§8.1), the two find each
other with no address typed by hand, and the token is the only thing the human carries.

**Its limit, and why discovery is still mandatory.** Human-mediated enrolment scales to your own
machines and no further. A node joining a project whose owners you have never met has nobody to
hand it a token. Separately, any addresses baked into a token go stale the moment a network
changes. Trust and reachability, again, are different problems.

---

## 5. Discovery: one shared network and an announce/expire seed

### 5.1 One Hall9k-wide address book

**All Hall9k nodes join one shared network, regardless of project.** Finding the peers for a
given project is then a *query over connections you already have* (§7), not a lookup criterion
for finding peers in the first place.

This inverts the obvious design (a rendezvous point per project) and is better on every axis:
no per-project setup, strangers work fine, and — the good part — **the seed never learns which
projects exist.** It only ever sees addresses. Membership is proven peer-to-peer with keys.

### 5.2 The seed: announce and query in one call

Bootstrap always needs something out-of-band; there is no way around it. Bitcoin's compromise is
a handful of DNS names in the source. Hall9k's is a **hardcoded URL** shipped in the binary —
a URL, not an IP, so the service can move hosts or repoint DNS without a new release.

The endpoint is one call that both announces and queries: a node POSTs who it is and where it
is, and the response is the current list of other announcers. Every node that queries also
populates the table. No curation, no submission process. The first node ever gets an empty list
back, which is correct — it is alone.

**The recorded address is the one the service observes the connection arriving from**, never
what the node claims. That is the post-NAT address and port that actually works.

### 5.3 Announce-and-expire, never probe

The seed **never initiates anything.** Nodes re-announce on a heartbeat (~15 min); entries
expire after ~1h of silence. Liveness is the node's job to assert, not the service's to verify.

Rejected: having the seed ping nodes to check they are up. It would hit every NAT problem the
peers themselves have, and it would turn a dumb table into a monitoring service holding state
and opinions. The table is deliberately churning and never complete — it is *currently warm*,
which is all a bootstrap needs.

### 5.4 A seed, not a dependency

Nodes cache the address book on disk, use peer exchange thereafter (§6), and fall back to the
seed only when everything they know has gone stale. **If the hosting bill lapses, existing
networks keep running.** Only brand-new nodes with no address book feel it.

Storage is **in-memory, deliberately**. A few hundred entries expiring hourly is a cache, not a
record. Losing it on restart is a non-event: every node re-announces within 15 minutes and it
refills itself. (If it ever matters, a Table Storage write on announce costs nearly nothing —
but let usage ask for it.)

### 5.5 Far-future scale (recorded for the shape, not to build)

At a million nodes, ~200 bytes an entry is ~200 MB — it fits; the traffic (~1,100 req/s at a
15-minute heartbeat) is the real cost. The answer is not a longer heartbeat but **announcing
when lonely rather than on a timer**: a dense network bootstraps almost entirely from cached
address books and gossip, and only a reachable, opted-in minority needs to keep announcing so
that new nodes always find someone. The seed can tune its own load with a "check back in N"
field in the announce response — no protocol change, no client update.

---

## 6. Peer exchange and the address book

After first contact the seed is irrelevant. Ask a peer who else it knows, add those, repeat. Ten
nodes fully discover each other from one connection, and no registry exists anywhere.

- **Cap active connections** at roughly 8–10. The on-disk **address book grows well beyond
  that** — next startup dials from the book and never touches the seed.
- **Diversify where addresses came from.** Don't let one chatty peer supply your whole view.
  Bitcoin buckets its address table by network group (roughly upper IP range) and spreads
  connections across groups; this is an anti-eclipse measure — if every peer sat behind one
  network, whoever controls it controls what you believe. Borrow the instinct.
- **No locality preference.** Bitcoin has none, and actively resists it. Any geographic
  clustering there is an accident of latency and who happens to run nodes.

---

## 7. Finding a project's peers

You do not ask for *Sarah*. You maintain a set of connections and ask each one: do you carry
project X?

If none of your peers do, ask them to pass the question along — a **bounded gossip query**,
capped by hop count and deduplicated by query ID so it cannot storm the network. Two hops from
ten peers reaches most of a network of any realistic size. Any node carrying the project answers
with its address so you can dial it directly.

If nobody answers, that is a real answer: nobody reachable has it right now. Retry on the
heartbeat.

---

## 8. Reachability: LAN, then punch, then relay

Try in order of cost. Cache per peer what actually worked and skip straight to it next time.

### 8.1 Same LAN — mDNS, nothing external

Two machines on the same network have no NAT between them. The joining node multicasts "I am a
Hall9k node, here is my node key, my owner key, the projects I care about"; the other hears it,
recognises the owner signature, and dials the local address directly.

**Works with no internet uplink at all.** Full LAN speed, so materialising a large attachment set
is instant. Trust checking is trivial when both certificates chain to the same owner key. Also
what makes the enrolment ceremony (§4) pleasant.

### 8.2 Hole punching — the common case

Routers drop unsolicited inbound packets, but *outbound* traffic creates a mapping ("this
machine is talking to that address") that briefly allows replies back. That mapping is the hole.

Walked through end to end:

> Both nodes have been announcing to the seed, so it knows the external address and port it
> **observed** each arriving from. Brian's node asks the seed for Sarah's; the seed
> simultaneously nudges Sarah's node with Brian's. Both fire UDP at each other at roughly the
> same moment. Brian's packet leaves first, punches the mapping in his router, reaches Sarah's
> router, and is dropped — she hasn't sent yet. Expected. Milliseconds later hers leaves, opens
> her mapping, and arrives at Brian's router, which recognises the address he just sent to and
> lets it through. The path is now open both directions.

Then QUIC over that path (TLS, streams, and reliability for free), keys verified against the
project's member list, and data flows. **The seed never touches the data.** A keepalive every
20–30s holds the mapping open indefinitely; on drop or address change, go back to the seed for a
fresh introduction.

Gets an estimated 70–80% of NAT pairs.

### 8.3 When punching fails

**Symmetric NAT** assigns a different external port per destination, so the port the seed
observed is useless for anyone else. **Blocked outbound UDP** (common on corporate and guest
networks; sometimes UDP is allowed only to port 53) stops the packet inside the building.
**Carrier-grade NAT** (mobile tethering) has the same effect.

The failure is **asymmetric and worth internalising**: if Sarah's firewall blocks outbound UDP,
her packet never leaves, so no mapping forms on her side — and Brian's packet, which left
correctly and reached their border firewall, has nothing to slip through. *Brian's side did
everything right and it still failed.*

You cannot know which case you are in until you try. So: attempt the punch, give it 2–3 seconds,
**fall back quietly.** The user never learns which path was used; they just see the transcript
arrive.

### 8.4 The relay — a permanent middleman on 443

A server on a public address, port 443, whose only job is to be reachable by everyone.

Both nodes make **outbound** connections to it, which works because outbound HTTPS is allowed
essentially everywhere. Each names the peer it wants; the relay matches the two sockets and
becomes a byte pump — everything from one socket is written to the other, both directions, until
someone disconnects. **Two possible connections replace one impossible one.**

Two things to be precise about, because they are easy to get backwards:

- **The relay stays in the middle for the whole session.** It is *not* an introducer that steps
  aside. That is the seed's job — the seed is genuinely DNS-shaped (introduce, then the two talk
  directly); the relay is a phone exchange that stays on the line.
- **All connections land on 443 and that is fine.** A port is a doorway, not a channel. Each
  connection has its own socket; the relay holds thousands of distinct sockets accepted on one
  listening port, exactly like any web server.

No keepalives are needed here — these are real established TCP connections with no NAT mapping
to hold open.

The relay is **dumb by construction**: it doesn't parse the protocol, store anything, or know
what a task is. Payloads are encrypted end-to-end between node keys, so it sees ciphertext only.
It can pass bytes along or drop them; it cannot read or tamper.

### 8.5 Transport summary

| Path | Transport | Notes |
|---|---|---|
| Same LAN | direct TCP/QUIC after mDNS | no external service involved |
| Punched | QUIC over UDP | keepalive 20–30s; no HTTP anywhere |
| Relayed | websocket over TLS/443 | HTTP only for the upgrade handshake |

Same application protocol on top of all three.

**Rejected: SignalR for the relay leg.** It is a good abstraction over websockets for
many-clients-calling-typed-methods-on-a-server; this is two peers piping opaque bytes. The hub
model, protocol negotiation, and fallback transports are dead weight, and they couple the wire
format to a framework. Raw websockets in ASP.NET Core is roughly thirty lines. It is also the
*exception* path — optimising developer convenience for the minority case at the cost of the
protocol is backwards. (Note: SignalR was stripped during the local-first pivot on the
assumption no relay would be needed; a relay being necessary does not bring it back.)

---

## 9. Infrastructure: what actually gets hosted

Brian runs the seed and relay. Likely Azure Container Apps, ASP.NET Core minimal APIs.

**Same codebase, separate deployments** — or at minimum separate scaling rules, with the
endpoints cleanly separated so splitting later is config, not a rewrite. They have genuinely
different shapes:

| | Seed | Relay |
|---|---|---|
| Traffic | tiny, bursty, request-response | long-lived, bandwidth-hungry |
| State | in-memory table, expires hourly | socket pairs, evaporate on disconnect |
| Scale to zero | fine | **no** — would kill live tunnels |
| Scaling metric | requests/sec | concurrent connections |
| Replicas | pin to one (divergent tables otherwise) | any |

Practically: if the relay gets hammered, discovery must not go down with it.

**"Centralised" oversells both.** Neither can read anything. The seed holds addresses that go
stale in an hour; the relay shuffles ciphertext for the minority of pairs where direct failed.
Nodes always try direct first. Keys do all the security work.

---

## 10. Sync semantics

**Metadata gossips eagerly; payloads materialise lazily on claim.** All nodes stay synchronised
on which projects and tasks exist. Discovery artifacts, transcripts, and attachments are *not*
replicated by default — claiming a task pulls its content from a peer that has it.

This fits the content-addressed attachment store (§8.1) exactly: the claimer already knows which
hashes it needs, and "do you have hash abc123?" is the right primitive either way.

**Claiming while the source node is offline holds the claim and syncs later.** The claim lives in
the event log regardless; the payload is only a cache to warm. This implies an *awaiting
materialisation* state so an agent is never dispatched into a half-empty working directory —
how that surfaces is open (§12).

---

## 11. Claims across nodes

**Claims are recorded against the OWNER, not the node.** Sarah needs to know *Brian* holds task
ABC, not which of his machines. The node key signs the event (who typed this); the payload
records the claiming owner. Sarah's node resolves node → owner and rejects a duplicate claim
from Brian's second machine as a same-owner duplicate.

`TaskClaimed(Id, NodeId, OwnerId, LeaseGeneration, RunId, ClaimedAt)` already carries exactly
this shape. No change needed.

Remote lease expiry is already settled and unchanged by anything here: never auto-stolen,
owner-aware escalation, takeover as a deliberate human act, fencing token making unenforceable
remote revocation safe (PLAN.md §16 #12).

---

## 12. Rejected alternatives

| Approach | Why not |
|---|---|
| **BitTorrent mainline DHT** as rendezvous | A DHT is only worth its complexity with a huge table, so the appeal was piggybacking someone else's. But mainline is BitTorrent, and corporate networks and ISPs throttle or block it on sight — breaking peering exactly where it is most needed. (Detection is not port-based in practice; DPI matches the handshake signature — byte `19` then the literal protocol string — a rule every appliance ships. Useful lesson in reverse: Hall9k's own protocol is unrecognisable by default, and TLS on 443 looks like ordinary HTTPS.) |
| **Git-remote rendezvous** (signed peer records in the project repo) | Burdens every project owner with repo overhead Hall9k shouldn't impose. The real exposure isn't read access — addresses are public in Bitcoin too, and unauthorised peers just get their handshake rejected — but publishing home IPs (privacy leak, DoS target) and the question of who can write. A private side repo solves it and makes the burden worse. Not fully closed off, but not the route. |
| **Bitcoin's crawler model** (seeds roam and publish reachable nodes) | Only works because Bitcoin nodes are *expected* to be publicly reachable. Developers on home and office networks mostly are not. Announce-and-expire is also strictly simpler: one endpoint, a dictionary, and expiry. |
| **Tor as the node transport** | Tempting: every node an onion service, dial-out only, stable address behind any NAT, no punching, no hole to keep open, no address churn. But 1–2s per round trip, poor throughput for payloads, and blocked on many corporate networks — the directory authorities are a fixed, trivially-blocked list, and DLP teams treat Tor as an exfiltration channel by policy. Bridges exist, but building censorship circumvention into a developer tool is the wrong place to end up. **Viable as a fallback for personal machines; useless where corporate policy bites.** |
| **Per-project keypairs** | See §2 — buys unlinkability, which is the opposite of the accountability model. |
| **SignalR for the relay** | See §8.4. |

---

## 13. Open questions

1. **Awaiting materialisation** — does a claim against un-synced payload surface as blocked,
   pending, or a quiet retry? (§10)
2. **Claim race tiebreak.** Two owners claim simultaneously. Jitter-backoff (CAN-bus style) is
   not deterministic on log replay, which is awkward for event sourcing; a deterministic
   tiebreak — earliest logical timestamp, node ID to break ties, applied identically everywhere
   — wants a Lamport counter, since events currently carry only local UTC wall-clock. Roughly an
   hour of work whenever it's wanted. **Deferred deliberately**: cross that bridge on arrival.
3. **The wire protocol between peers** — what messages exist, how gossip is framed, how a
   project query looks on the wire. Not yet opened; this is the substance of the node-side work.
4. **Revocation distribution** — how a revoked node certificate propagates, and what a peer does
   with events signed before the revocation.

---

## 14. Build order, if and when

Each slice is independently useful, and the early ones need none of the later machinery.

1. **Keys and enrolment** — owner/node keypairs on the existing aggregates, certificate chain,
   enrolment token, signed events. Pure local work; no network.
2. **LAN-only via mDNS** — two of your own machines, same network. No seed, no punching, no
   relay. Proves sync, materialisation, and cross-node claims end to end.
3. **Seed + direct** — announce/expire service, address book, hole punching, QUIC.
4. **Relay** — the fallback, and the detection/timeout logic that chooses it.
5. **Gossip** — peer exchange, bounded project queries, address book diversity.

Effort, honestly: medium-large. The hard parts are few — punching with reliable timeouts and
fallback, the QUIC handshake with key verification, gossip with sane dedup and backoff — each
days rather than weeks. The infrastructure is genuinely easy (the relay is a dictionary and a
byte loop). What will eat time is the unglamorous middle: address book maintenance, connection
churn, peers that lie or stall mid-transfer, and testing all of it without ten real machines.
Call it ~90% node-side, ~10% infrastructure.
