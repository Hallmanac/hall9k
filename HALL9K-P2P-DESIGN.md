# Hall9k — Peer-to-Peer Layer Design

**Status: designed, not built.** This is the complete design for roadmap item #5's "true P2P"
branch, consolidated from four design sessions (2026-08-18 and 2026-08-19). Nothing described here
exists in code today.

This document is the **single reference**. It supersedes `P2P-DESIGN.md`,
`OWNER-KEY-LIFECYCLE.md`, and `TRANSPORT-AND-WIRE-PROTOCOL.md`, which should be deleted once this
is in the repo. It records **end state only** — where the design moved during the sessions, only
the destination is written down. Rejected alternatives are kept deliberately (§16), because they
carry real reasoning and prevent re-litigating settled ground.

Binding one-line summaries live in `PLAN.md` §16 (Decisions Log #38–#56). This document carries the
rationale and the mechanics.

Read alongside: `PLAN.md` §6.2 (accountability, "the P2P down-payment"), §8.1 (content addressing),
§10 (identity & connections), §14 roadmap #5, §16 #7/#12 (leases and locality-aware expiry).

---

## 1. Two problems, deliberately separated

Underneath everything, peering is: **find an address and port that will accept your connection**,
and **be certain whoever answers is who you expect**.

- **Trust** — who is this, and are they allowed here. Solved entirely by keys (§2–§5). Already
  half-built: `TaskClaimed` carries `OwnerId` alongside `NodeId`, and Owner/Node are first-class
  aggregates.
- **Reachability** — can bytes physically get from here to there. Solved by discovery (§7) and NAT
  traversal (§9–§10). Entirely greenfield.

Conflating them is the classic mistake. The invite token (§5.2) is trust and does nothing for
reachability; the seed (§7.2) is reachability and proves nothing about trust. Keep them apart.

---

## 2. Identity: a two-tier key hierarchy

**One owner keypair per human. One node keypair per machine. The owner key signs each node's public
key, producing a certificate.**

- The **owner key** is the root of trust. It stays on the primary machine and stays cold — it signs
  node certificates and nothing else.
- The **node key** is generated locally, never leaves the machine, and does all day-to-day event
  signing.
- A peer verifies two links: this event was signed by node key N, and node key N was certified by
  owner key O. Therefore this is Brian's work, from Brian's laptop.

Consequences worth having:

- **Per-node revocation is free.** Lose a laptop, revoke that certificate; the owner identity and
  every other node survive untouched.
- **Node identity is global**, not per-project — which is exactly what makes the shared discovery
  network in §7 work.

**Self-sovereign.** Keys are generated locally at `h9k owner init`. No OAuth, no account, works
offline, and the trust root stays owned. A signed "this owner is GitHub user X" attestation is
purely additive and can come later.

### 2.1 What a certificate is, structurally

`node public key + signature over it by the owner private key + metadata`. A **signed statement,
not an actor**: the certificate does not sign anything. The node *private* key makes the handshake
signature; the certificate proves that node key belongs to a particular owner.

Note what the receiver needs: **not the sender's owner public key over the wire** — that would be
circular — but the owner public key it *already holds*, obtained out of band when it joined the
project (§5.2). That prior possession is the entire root of trust.

---

## 3. The peering unit is the project, not the node

The topology, stated plainly:

- a node hosts many projects;
- a node authenticates as exactly one owner;
- an owner may run several nodes;
- a project may span many owners (a team) or exactly one (Hall9k itself);
- **some projects are personal and peers must never learn they exist.**

That last point settles it. If peering were node-to-node wholesale, connecting to a colleague would
leak the existence of every private project on the machine. So membership lives in the **project**,
sharing is opt-in per project, and the node is just a host.

This is why the shared discovery network (§7) is safe: the address book is global, but what a node
will *talk about* is scoped per project and gated by membership.

---

## 4. The owner key: what it does, and why losing it is survivable

**It signs node public keys. That is the entire job.**

It does not sign events. It does not join projects. It is not consulted on sync, on claims, on
gossip, or on any handshake. A node emits events signed by its *own* node key, with its certificate
riding along; peers verify two links (§2) and the owner key is nowhere in the transaction.

Two consequences, both load-bearing:

- **Every node holds the owner *public* key**, not just the primary. So **any** node can join a
  project on the owner's behalf — it presents the owner public key, proves it holds the invite
  token, and proves it is a certified node under that owner. The primary is special for exactly one
  operation: enrolling a machine.
- **Losing the owner private key is inconvenient, not fatal.** Every existing node keeps working
  indefinitely — claiming, syncing, signing, joining new projects. The single thing that breaks is
  enrolling a *new* machine.

That second point is what makes §6 possible. There is no outage to detect and no emergency to
automate.

---

## 5. Enrolment and joining

### 5.1 Two token types, deliberately distinct

| | **Node enrolment token** | **Project invite token** |
|---|---|---|
| Minted by | a node holding the owner private key | any node of any project member |
| Carried to | a new machine of the *same* owner | another human |
| Causes | the owner key to **sign a node public key** (a certificate) | an owner public key to be **added to a project's member list** |
| Requires the owner private key? | **Yes** | No |
| Scope granted | every project this owner is a member of | exactly one project |

Both are single-use, expire in ~1h, and are proven by an HMAC over the joiner's public key keyed by
the token secret. The joining message is node-signed as usual.

**Keep them as separate types on the wire.** A project token must never be able to enrol a machine —
that would silently convert "you may see this project" into "you may act as me everywhere."

### 5.2 Enrolling a new machine of your own

The problem: the owner key must sign a new node's public key, but the owner key must not travel.

**A short-lived enrolment token.** Export it from an existing node (~1h TTL), carry it to the new
machine, feed it to `h9k init`. It carries node A's addresses, node A's public key, and a shared
secret — enough to authorise the new node's key over a channel node A can verify. A one-time
ceremony, not a key copy.

On a LAN this is genuinely pleasant: the new machine multicasts (§9.1), the two find each other with
no address typed by hand, and the token is the only thing the human carries.

**Its limit, and why discovery is still mandatory.** Human-mediated enrolment scales to your own
machines and no further. A node joining a project whose owners you have never met has nobody to hand
it a token. Separately, any addresses baked into a token go stale the moment a network changes.
Trust and reachability, again, are different problems.

### 5.3 Joining someone else's project

Sarah does not need to know Brian's owner public key beforehand, and does not need to know it is
Brian at all. She trusts whoever holds the token.

1. Sarah mints a project invite token and sends it however she likes — the channel needs
   **integrity, not secrecy**.
2. Brian runs `h9k project join` on **any** of his nodes.
3. That node finds Sarah's node (token addresses first, discovery as the safety net), handshakes,
   and sends: his **owner public key**, the HMAC proof over it, and his node certificate — all
   node-signed.
4. Sarah's node verifies, adds the owner public key to the project's member list, and returns the
   list plus project state.

The token's whole job is bootstrapping exactly one fact: *this owner public key belongs to the
person I meant to invite.*

### 5.4 What an invite token carries

| Field | Why |
|---|---|
| Project identifier | which project is being joined |
| **Sarah's owner public key** | **the critical one** — without it you cannot verify her node certificate during the handshake and would be trusting whoever answered |
| Seed/node addresses | where to aim the first packet |
| Expiry | so an intercepted token is not usable a year later |
| Signature by Sarah's owner key over the above | so the token cannot be forged |

The token does work at **two layers**, which is easy to miss: the owner public key is loaded
**before connecting** so the QUIC verification hook (§8.4 step 3) can say yes, and the token itself
is then presented **inside** the join message. Handshake mechanics belong to QUIC; the trust
decision is ours, and the invite is what supplies it.

---

## 6. Succession, promotion, and partitions

### 6.1 The standing succession chain

**At enrolment, every node generates its own owner keypair in addition to its node keypair, and the
primary signs a succession statement: "owner key A also controls owner key B."**

The statement is stored on the enrolling node and travels as part of project state, so peers already
hold it before it is ever needed. Enrol five machines and you have five heirs, each vouched for by
the key that was healthy at the time.

This costs nothing extra to produce — the machines are already talking, the owner key is already
being used, and the second signature is one more call in a ceremony that happens a handful of times
per lifetime.

Recovery therefore becomes: **promote a node you still have.** No password manager, no paper backup,
no instructions to hand to a colleague.

### 6.2 Promotion is deliberate, and discovers itself at the right moment

Because nothing is broken (§4), there is **no failover to automate**: no health checks, no
elections, no quorum, no split-brain machinery. Promotion is a human act, invoked the day it is
actually needed — which is the day you try to enrol a machine.

1. On a surviving node, `h9k node invite`.
2. The node looks for a reachable node holding the owner private key. Finds none.
3. It says so plainly: *no owner key available; this node can be promoted — proceed?*
4. On yes: it presents its succession statement (already signed by the old key), announces the
   promotion to peers, and mints the token.
5. From then on it is the primary, and the remaining nodes re-enrol against it in the ordinary way —
   new tokens, new certificates. Nothing special; it is onboarding again.

**The prompt matters.** Promotion changes the identity root across the network and must not happen
silently as a side effect of wanting a new laptop.

**Ordering: oldest reachable node.** It needs no extra state — every node knows its own enrolment
date, and those dates are signed by the primary, so the ordering is *verifiable rather than
declared*. It is a suggested default, not an automatic rule, precisely because the oldest node may
be the one that is offline. Skip down to the oldest **reachable** node; the promoted node announces
itself so the others stand down.

**Minting is a request, not a local capability.** A non-primary node asked to mint should **reach
the node holding the owner key and act on its behalf** — where the key physically lives is invisible
to the user. The only failure that can surface is *"cannot reach any machine holding your owner
key"*, which is the promotion prompt above.

### 6.3 If there is no chain and no surviving node

The floor, and it is not bad: **a project member re-invites you.** Sarah mints an ordinary project
token, you join with a brand-new owner key, and you are working again in one Slack message.

The cost is honest and worth recording: your prior work is attributed to an owner key nobody vouches
for anymore. The history is intact; the authorship is orphaned. A pre-signed succession statement
(§6.1) is exactly what prevents that, which is the argument for minting them by default.

For a solo project with nobody to vouch for you, an exported key file in your normal backups is
proportionate. That is the one case where manual backup earns its keep.

### 6.4 The easy failure: a latecomer

The common shape is not a race at all. The primary is a laptop, the lid closes, and while it sleeps
a secondary is asked to enrol a machine, fails to reach it, and promotes.

When the laptop wakes it hears the promotion announcement, and **it can verify that statement
because it signed it itself**. So it steps down: archives its owner private key and carries on as an
ordinary node.

**Promotion events form a chain**, each naming the key it supersedes. A node offline across
*several* promotions replays them in order and lands on the current primary. Step-down logic must
handle a chain, not a single hop.

### 6.5 The hard failure: two valid promotions

Two nodes promote independently, neither aware of the other, both cryptographically valid.

The first instinct is a quorum rule — refuse to promote unless you can reach some number of your
other nodes. **That does not work**, and finding out why locates the real generator:

> Location two loses its internet uplink. Its local network is perfectly healthy — three of the
> owner's nodes talking happily to each other. From inside that bubble it looks exactly like an
> ordinary dead primary. A quorum is available, is consulted, and agrees.

That is the standard nastiness of partitions: **both halves look correct from the inside.** A quorum
rule protects against a *lonely* node, not a split, and the split is the case that matters.

### 6.6 Reconciliation, and the universal tiebreak

**Both promotions are hashed; the lower hash wins.** Both sides compute both hashes independently
from data that never changes, so they reach the same answer with no negotiation and — importantly —
**no trusted clock**, wall-clock time being exactly what a partition makes unreliable.

This is CAN-bus arbitration: an arbitrary but deterministic priority that losers detect for
themselves rather than being told.

> **Hash arbitration is the single tiebreak mechanism, used everywhere.** Both contested orderings —
> promotion reconciliation here, and the cross-owner claim race (§14) — resolve the same way. One
> mechanism, one code path, no clocks to reason about.

*Hash rather than raw node ID: node IDs are already globally unique and would be equally
deterministic, but a hash is unpredictable, so nobody can choose an ID to guarantee winning. That
matters little when every owner is you, and costs nothing.*

**Accepted cost, stated plainly:** hash arbitration is *arbitrary*. A stale claim can beat a fresh
one, and the better piece of work can lose. The recovery path is what absorbs this — the loser's
work is **triaged, never destroyed** (§6.8).

### 6.7 The aftermath of a competing promotion

Both keys were live for days. Each certified nodes; those nodes signed real events.

- **Nodes are easy.** The loser's certified nodes re-enrol under the winner. New certificates,
  nothing lost.
- **Events are the question**, and the rule is: **a certificate is valid for what it signed while it
  was valid.** The losing key stops certifying anything new, but events already signed under it
  remain verifiable forever. Rejecting them would discard legitimate work; treating the key as
  never-having-been-valid would be a lie about what happened.
- Consequently **the losing promotion event is retained as evidence, not deleted.** A peer verifying
  an old event needs to establish that the certifying key was legitimate at the time.

**This is not a chain split**, despite the resemblance. In a blockchain reorg the two histories
*compete* — the same coins, contradictorily spent — so one must be erased. Here they mostly do not:
two machines certifying two laptops doing two unrelated pieces of work are not in conflict, they
merely happen to have been vouched for by keys that later disagreed about seniority. Both histories
are kept and merged. No reorg, nothing orphaned.

### 6.8 What a node owes you about work that lost

**There is no "primary" in the user's world.** Nobody walks up to a machine caring what kind of node
it is; they walk up and start working. "Primary" names exactly one capability — holding the owner
private key — exercised the handful of times a machine is enrolled. So **key reconciliation is
silent plumbing**: a losing node finds out at the one moment it matters (§6.2), and there is no
status to track and nothing to surface.

**The reconciliation that ever speaks is the one about work**, and it must surface per-node, where
the human is standing.

**Losing runs are attached, not merged.** A task has one identity and now has **two runs against
it**. Both are stored as they arrived, each keeping its own identity: this run, on this node, at
this time. Nothing is rewritten, re-ordered, or discarded.

This works because **ordering across nodes was never a single true sequence.** Each run is
internally ordered; the two runs are concurrent; physical order in the transaction log is arrival
order and an implementation detail. Distinct stream IDs mean there is no collision to resolve — the
partial order is admitted rather than flattened, and the read model reports "two attempts" instead
of pretending to one narrative.

**Consequence, and it is a large one: the storage layer needs no partition-specific machinery at
all.** The only place the ambiguity was ever real is the **lease** — two nodes each believing they
held the right to work — and that is precisely what the §6.6 hash tiebreak settles.

> **The tiebreak decides who had authority. The events record what happened either way.**

Nothing gets combined at the data level. What gets combined is the **conclusion**, and that is a
**third artifact** written by whoever reviews both — an appended interpretation of history, never an
edit to it.

**The winner proceeds; the loser is flagged, not blocked.** The winning run stands as the outcome —
its branch is the branch, its PR is the PR. The losing run is flagged on the task as **unreviewed**,
non-blocking. Explicitly: **nothing is deleted.** The work stays on disk, the transcript stays, the
diff stays.

**An agent triages, and the winner reviews the loser.** A losing claim **materialises a review
task**, carrying both attempts as its payload. An agent picks it up through the ordinary queue,
reads both runs, and reports where they diverge — *and folds in anything from the losing approach
worth keeping*. Since the tiebreak is arbitrary (§6.6), the losing approach is as likely to be the
better one; this is what recovers that value.

Most of the time the honest answer is *they converged* — two agents given the same task and codebase
reach near-identical results more often than not. In that case the agent says so, the winner stands,
and the matter closes without a human reading two transcripts to learn nothing. **That is the common
case, and absorbing it is most of the value.** Escalate only genuine divergence.

**The agent recommends; it does not decide.** Discarding your own work is exactly the class of
action that wants a human nod. This sits at the edge of the supervised-autonomy model rather than
past it.

*Note this is the same shape as the pre-PR independent review loop already in use (`PLAN.md` §16
#20–#24) — an agent reading work it did not do and reporting rather than acting. Not new machinery;
an existing pattern pointed at a new input.*

The **design obligation** is only to make this possible: preserve the losing branch, and record that
a review is owed.

---

## 7. Discovery

### 7.1 One Hall9k-wide address book

**All Hall9k nodes join one shared network, regardless of project.** Finding the peers for a given
project is then a *query over connections you already have* (§7.5), not a lookup criterion for
finding peers in the first place.

This inverts the obvious design (a rendezvous point per project) and is better on every axis: no
per-project setup, strangers work fine, and — the good part — **the seed never learns which projects
exist.** It only ever sees addresses. Membership is proven peer-to-peer with keys.

### 7.2 The seed

Bootstrap always needs something out-of-band; there is no way around it. Bitcoin's compromise is a
handful of DNS names in the source. Hall9k's is **seed addresses in configuration, with an
overridable default list** — hostnames rather than IPs, so the service can move hosts or repoint DNS
without a new release, and configurable rather than hardcoded so a private mesh is possible without
a fork.

The seed is **an ordinary node** (§11), not an HTTP endpoint. Announce and query are a message pair
in the ordinary framing (§12).

A node announces who and where it is; the response is the current list of other announcers. Every
node that queries also populates the table. No curation, no submission process. The first node ever
gets an empty list back, which is correct — it is alone.

**The recorded address is the one the seed observes the connection arriving from**, never what the
node claims. That is the post-NAT address and port that actually works.

### 7.3 Announce-and-expire, never probe

The seed **never initiates anything.** Nodes re-announce on a heartbeat (~15 min); entries expire
after ~1h of silence. Liveness is the node's job to assert, not the service's to verify.

The table is deliberately churning and never complete — it is *currently warm*, which is all a
bootstrap needs.

**A seed, not a dependency.** Nodes cache the address book on disk, use peer exchange thereafter
(§7.4), and fall back to the seed only when everything they know has gone stale. **If the hosting
bill lapses, existing networks keep running.** Only brand-new nodes with no address book feel it.

Storage is **in-memory, deliberately**. A few hundred entries expiring hourly is a cache, not a
record. Losing it on restart is a non-event: every node re-announces within 15 minutes and it refills
itself.

**Far-future scale**, recorded for the shape rather than to build: at a million nodes, ~200 bytes an
entry is ~200 MB — it fits; the traffic (~1,100 req/s at a 15-minute heartbeat) is the real cost. The
answer is not a longer heartbeat but **announcing when lonely rather than on a timer**: a dense
network bootstraps almost entirely from cached address books and gossip, and only a reachable,
opted-in minority needs to keep announcing so new nodes always find someone. The seed can tune its
own load with a "check back in N" field in the announce response — no protocol change, no client
update.

### 7.4 Peer exchange and the address book

After first contact the seed is irrelevant. Ask a peer who else it knows, add those, repeat. Ten
nodes fully discover each other from one connection, and no registry exists anywhere.

- **Cap active connections** at roughly 8–10. The on-disk **address book grows well beyond that** —
  next startup dials from the book and never touches the seed.
- **Diversify where addresses came from.** Don't let one chatty peer supply your whole view. Bitcoin
  buckets its address table by network group (roughly upper IP range) and spreads connections across
  groups; this is an anti-eclipse measure — if every peer sat behind one network, whoever controls it
  controls what you believe. Borrow the instinct.
- **No locality preference.** Bitcoin has none, and actively resists it. Any geographic clustering
  there is an accident of latency and who happens to run nodes.

### 7.5 Finding a project's peers

You do not ask for *Sarah*. You maintain a set of connections and ask each one: do you carry project
X?

If none of your peers do, ask them to pass the question along — a **bounded gossip query**, capped by
hop count (three or four) and deduplicated by query ID so it cannot storm the network. Two hops from
ten peers reaches most of a network of any realistic size.

**Multi-hop answers come back directly**, not back along the hop path, because the query carries the
asker's address. **Gossip is a search fabric, not a routing fabric.**

If nobody answers, that is a real answer: nobody reachable has it right now. Retry on the heartbeat.

### 7.6 Shoulder-tapping

**Sarah does not know you want her.** She holds a warm connection to the seed with heartbeats keeping
her mapping open, so the seed can **tap her on the shoulder**. The seed's real service is being
reachable at a known address.

**When the seed has no live connection to Sarah**, it asks the peers it does have. A peer still
holding a path to Sarah becomes the shoulder-tapper — it nudges Sarah itself rather than handing her
address back to the seed, and tells the asker where to aim. **The seed is a directory of first
resort, not a switchboard.**

**Addresses are hints, not facts.** If nobody holds a path, Sarah is genuinely offline — queue
locally and sync later.

**The introducer's actual service is synchronising the attempt** — telling both sides at roughly the
same instant, since a delayed nudge means the hole has already closed.

---

## 8. Transport: QUIC

### 8.1 The decision

**All peer traffic is QUIC over UDP, via `System.Net.Quic`.** No third-party package: QUIC ships in
.NET on top of MsQuic (present with the runtime on Windows, a small native library on Linux).

You **use** QUIC, you do not implement it. Packet layout, encryption, handshake, retransmission,
congestion control and stream multiplexing are the library's job. What Hall9k writes is the framing
*inside* the streams — which is §12, and nothing else.

Two layers that must not be conflated:

| Layer | Provides | Whose job |
|---|---|---|
| QUIC + TLS | a private, authenticated, multiplexed pipe | the library |
| Hall9k wire protocol | what the two nodes *say* over that pipe | ours |

### 8.2 Why QUIC rather than TCP + TLS

- **No head-of-line blocking.** TCP guarantees one global order, so a single lost packet stalls
  everything behind it. QUIC carries independent streams inside one connection: a large
  back-catalogue sync on one stream does not block a live event push on another.
- **One handshake, not two stacked.** TCP+TLS 1.2 costs three round trips before a useful byte moves.
  QUIC merges the transport and crypto handshakes into **one round trip** — and encryption is
  mandatory rather than optional, which means **certificate exchange happens as part of connecting,
  not as a protocol message we design.**
- **Connection migration.** A connection is identified by its connection ID, not the IP/port
  four-tuple, so changing networks does not kill it. A laptop moving from home to a coffee shop
  mid-sync keeps the sync. **No resume logic to write.**
- **It is what makes hole punching possible at all.** See §8.3.
- **It ships in userspace.** TCP's mechanics live in the kernel, which is why TCP improvements take a
  decade to roll out; QUIC's logic sits above the kernel's UDP socket and ships with the next
  deployment. (UDP itself is still kernel; only the QUIC layer on top is userspace. "Userspace" here
  means privilege level, not a user account.)

### 8.3 Why UDP specifically enables punching

A router notes outbound traffic and thereafter admits replies from the address it was sent to.
**That note is the hole.** UDP's statelessness — normally a weakness — is exactly what makes the
trick work: the router does not care whether your packet ever arrived, so a packet fired at a peer
who has not yet fired back still opens the mapping.

TCP cannot do this. It insists on a SYN handshake in strict order, so when both sides dial
simultaneously both SYNs are dropped and both sides time out.

TCP's alternatives are therefore: **relay** (both parties dial *out* to a middleman that copies bytes
between two pipes — note the relay never dials either party), **manual port forwarding**, or **UPnP**
(a separate prior conversation with the router, not part of the SYN — and a port opened that way is
open to *anyone*, which is why routers ship with it disabled, and carrier-grade NAT defeats it
regardless). **UPnP works often enough to try as an optimisation and never often enough to depend
on.**

### 8.4 The handshake, and the one hook we write

Four steps, one round trip:

1. **Initial packet.** An invented connection ID, the QUIC version, and a TLS ClientHello carrying an
   **ephemeral** public value plus the supported cipher suite list, padded to ~1200 bytes
   (deliberately, to prevent amplification). The header is in the clear; the payload is encrypted
   with keys derived from the connection ID, so anyone *can* decrypt it — this is format consistency,
   not secrecy. **No certificate, no identity, no project information.** An observer learns only that
   someone is opening a QUIC connection.
2. **The reply.** Her connection ID, her own ephemeral public value, the cipher suite she chose (by
   preference order, strongest first), her **node certificate**, and a **signature over the handshake
   so far** made with her node private key. The certificate is sent here rather than in step 1
   precisely because initial keys now exist to protect it.
3. **Our turn, symmetrically** — node certificate plus signature. **This is the only step Hall9k
   writes any code for**: the library does the cryptographic verification and then hands over the
   certificate and asks *do you accept this?* Our answer is: **does this chain to an owner key I
   recognise as a member of a project I care about?** If not, the connection closes with no error and
   no explanation — we simply stop existing to the far side.
4. **Confirmation.** Connection live; symmetric encryption thereafter.

Note what this collapses: **a TLS channel and mutual identity verification in a single round trip.**
Standard TLS lets the client stay anonymous (that is web browsing); Hall9k configures QUIC to
**require a client certificate**, which is a setting rather than a message.

**Verified (2026-08-19).** The hook exists and is reachable through the managed API:
`RemoteCertificateValidationCallback` is available on both `SslClientAuthenticationOptions` and
`SslServerAuthenticationOptions` as used by `QuicClientConnectionOptions` /
`QuicServerConnectionOptions`, and `ClientCertificateRequired` is the setting referred to above. The
callback receives the certificate and chain and returns a boolean — which is exactly what step 3
needs. Two caveats carried forward:

- **Rejection may not be silent.** The library may close the connection with a TLS alert rather than
  vanishing. §8.4's "no error, no explanation" is the *intent*; confirm what the far side actually
  observes.
- **A known runtime issue** allows `QuicListener` to hand back a connection that already failed client
  certificate validation. So the accept path must re-check rather than assuming a rejected connection
  never arrives.

There is also a **0-RTT** mode for reconnecting to a peer already known, which sends application data
in the very first packet. Minor replay caveats; attractive for reconnecting nodes. Not required for
v1.

### 8.5 Two keypairs, two jobs

| | Ephemeral keypair | Node keypair |
|---|---|---|
| Lifetime | generated fresh per connection, discarded after | long-lived, in the certificate |
| Purpose | **key agreement** | **signing** (identity) |
| Curve/cost | X25519, microseconds | signing curve, once per handshake |

**Nothing is encrypted with either private key.** The widespread mental model — "encrypt with the
private key, decrypt with the public" — describes *signing*, and even then only loosely.

**Key agreement** (Diffie–Hellman): each side sends only a public value, then combines **its own
private half with the other's public half**, and the mathematics yields **the same number on both
sides**. That number never crosses the wire; an eavesdropper holding both public values cannot
compute it. It is then run through a derivation function to produce the symmetric keys used for the
rest of the connection. Because the keypair is thrown away, this gives **forward secrecy**: traffic
recorded today stays unreadable even if a node's long-term key leaks years later.

**Signing** is a different operation with opposite intent: hash the thing being signed, apply a
transformation using the private key, and the output is public and verifiable by anyone. Key
agreement produces a shared secret; signing produces public evidence.

**Verification does not recompute the signature.** It cannot — that would need the private key. The
signature, the hash, and the signer's public key go into a verify function that answers yes or no.
Sign with private, verify with public, never the reverse.

The intuition underneath: **the keys are two halves of one relationship.** There is an operation easy
forwards and effectively impossible backwards — for elliptic curves, multiplying a fixed generator
point by a number. The private key is the number; the public key is the resulting point. The public
key is *derived from* the private one, so anything the private key does leaves a fingerprint the
public key can check.

---

## 9. Reachability: LAN, then punch, then relay

Try in order of cost. Cache per peer what actually worked and skip straight to it next time.

### 9.1 Same LAN — mDNS, nothing external

Two machines on the same network have no NAT between them. The joining node multicasts "I am a Hall9k
node, here is my node key, my owner key, the projects I care about"; the other hears it, recognises
the owner signature, and dials the local address directly.

**Works with no internet uplink at all.** Full LAN speed, so materialising a large attachment set is
instant. Trust checking is trivial when both certificates chain to the same owner key. Also what
makes the enrolment ceremony (§5.2) pleasant.

### 9.2 Hole punching — the common case

Both nodes have been announcing to the seed, so it knows the external address and port it
**observed** each arriving from.

> Brian's node asks the seed for Sarah's; the seed simultaneously nudges Sarah's node with Brian's.
> Both fire UDP at each other at roughly the same moment. Brian's packet leaves first, punches the
> mapping in his router, reaches Sarah's router, and is dropped — she hasn't sent yet. Expected.
> Milliseconds later hers leaves, opens her mapping, and arrives at Brian's router, which recognises
> the address he just sent to and lets it through. The path is now open both directions.

Then QUIC over that path, keys verified per §8.4, and data flows. **The seed never touches the
data.** A keepalive every 20–30s holds the mapping open indefinitely; on drop or address change, go
back to the seed for a fresh introduction.

Gets an estimated 70–80% of NAT pairs.

### 9.3 When punching fails

**Symmetric NAT** assigns a different external port per destination, so the port the seed observed is
useless for anyone else. Common on mobile networks and some corporate setups. **Carrier-grade NAT**
(mobile tethering) has the same effect. **Blocked outbound UDP** is a different and harsher problem —
see §10.

The failure is **asymmetric and worth internalising**: if Sarah's firewall blocks outbound UDP, her
packet never leaves, so no mapping forms on her side — and Brian's packet, which left correctly and
reached their border firewall, has nothing to slip through. *Brian's side did everything right and it
still failed.*

You cannot know which case you are in until you try. So: attempt the punch, give it 2–3 seconds,
**fall back quietly.** The user never learns which path was used; they just see the transcript
arrive.

### 9.4 The QUIC relay

A node with a public address whose only job is to be reachable by everyone. Both nodes make
**outbound** QUIC connections to it; each names the peer it wants; the relay matches the two and
becomes a byte pump. **Two possible connections replace one impossible one.**

Two things to be precise about:

- **The relay stays in the middle for the whole session.** It is *not* an introducer that steps
  aside. That is the seed's job — the seed is genuinely DNS-shaped (introduce, then the two talk
  directly); the relay is a phone exchange that stays on the line.
- The relay is **dumb by construction**: it doesn't parse the protocol, store anything, or know what
  a task is. Payloads are encrypted end-to-end between node keys, so it sees ciphertext only. It can
  pass bytes along or drop them; it cannot read or tamper.

**Note what the QUIC relay is for.** Most relay cases are *not* UDP being blocked — they are
addressing failures where UDP works perfectly (symmetric NAT, CGNAT). The QUIC relay serves
working-UDP-but-unroutable peers, which is the common case. The harsher case has its own path (§10).

### 9.5 Transport summary

| Path | Transport | Notes |
|---|---|---|
| Same LAN | QUIC direct after mDNS | no external service involved |
| Punched | QUIC over UDP | keepalive 20–30s |
| Relayed (QUIC) | QUIC to a relay node | the common relay case: NAT, not blocking |
| Relayed (TCP fallback) | TLS on 443, single stream | the exception path — §10 |

The same application protocol (§12) runs on top of all four.

---

## 10. The UDP-blocked exception path: a TCP/443 fallback

### 10.1 The problem, stated precisely

Some corporate and guest networks drop outbound UDP entirely. On such a network **every path in §8–§9
dies at the same point**: the direct connection, the punch attempt, and the connection to the QUIC
relay all need a UDP packet to leave the building, and none does. A QUIC relay cannot rescue this,
because reaching the relay requires the blocked thing.

**Inbound versus outbound.** Inbound UDP blocking is the *common* condition — nearly every home router
refuses unsolicited inbound packets by default, which is the entire reason hole punching exists
(§8.3). That case is already handled. The case that defeats the design is **outbound** blocking: with
no outbound UDP you cannot open a mapping at all, so there is nothing to punch and nowhere to punch
it from.

### 10.2 The decision

**A TCP fallback transport on port 443.** Both peers dial outbound to a relay over ordinary TLS on
443 — universally permitted, because it is indistinguishable from HTTPS at the network level — and the
relay pumps bytes between the two connections.

**Fallback is triggered by QUIC failure, not configured**: try QUIC, and on silence, fall back.

### 10.3 Single stream, sequential

**The fallback path carries one stream, not multiplexed.**

Multiplexing mattered on the QUIC path so that a large back-catalogue sync could not head-of-line
block a live event push. On the fallback that concern is acceptable, because:

- This is the *degraded* path. Correctness matters; latency does not much.
- The push/sync split is already the escape hatch — push is the fast path, sync is the correct path
  (§12.2). A push stuck behind a large transfer is picked up by the next sync regardless.

This collapses most of the implementation cost: one ordered byte stream, the §12.1 framing unchanged,
messages handled sequentially.

### 10.4 What TLS to the relay does and does not give you

TLS on 443 authenticates the *relay's hostname* and encrypts the leg between each peer and the relay.
It does **not** give end-to-end protection, because **the relay terminates both TLS legs and therefore
holds plaintext in the middle**.

That is unacceptable by construction: a relay is untrusted — any node may volunteer (§11.3). So the
fallback path needs **its own encryption and its own peer authentication inside the pipe**. QUIC was
doing both for free; on this path we write them (§10.7).

### 10.5 Volunteering as a fallback relay requires a DNS hostname

Ordinary nodes may volunteer as **full** relays — meaning QUIC relaying *and* TCP/443 fallback
relaying. But the fallback capability carries a requirement the QUIC path does not:

- **A real DNS hostname is required to advertise TCP fallback relay capability.** TLS on 443 needs a
  certificate valid for a hostname, and modern termination is SNI-based; IP-address certificates are
  effectively unavailable. A volunteered relay therefore advertises either a hostname (eligible for
  fallback relaying) or an IP address only (QUIC relaying only).
- Inbound 443 also needs a port forward, which most home routers do not have.

The QUIC relay path needs **no certificate at all** — peers verify each other directly (§8.4). The
hostname requirement exists solely because the fallback path impersonates HTTPS. Plain TCP without
TLS would sidestep the certificate but stops looking like HTTPS, and inspecting firewalls may kill it
— which defeats the entire purpose.

Consequence: volunteered fallback relays will be **rare and technically committed**. That is
acceptable, because of §10.6.

### 10.6 The seed relay is the directory, and filters by capability

Volunteered relays are learned through gossip — **and gossip runs over QUIC**. On a UDP-blocked
network you cannot learn about the very relays that would help you. So relay candidates come from two
places:

1. **The seed relay**, from config with an overridable default list (§7.2), always available, always
   TCP-capable.
2. **Cached volunteered relays**, learned earlier on a permissive network and written to disk.

The residual gap — a fresh install that has *never* had good connectivity, so has nothing cached — is
closed by the seed acting as a directory:

> **The seed tracks which of its known peers advertise TCP fallback capability, and preferentially
> hands those out during peer exchange when the requesting node arrived over TCP.** A node that
> reached the seed over the fallback path evidently needs TCP-capable options.

This is a **filter on the existing peer exchange message** (§12.2), not new machinery.

### 10.7 The identity and key-agreement exchange

Inside the relayed TCP pipe, two things must be established, and they combine into **one two-step
exchange**:

1. **The peer is genuinely who they claim** — a node certificate chaining to a known owner key.
2. **A shared secret the relay never sees** — ephemeral key agreement, as QUIC does internally (§8.5).

**Step 1 — lay the cards down.** Each side generates a throwaway keypair for key agreement, then
sends:

- its ephemeral public key,
- its node certificate,
- a fresh nonce.

Nothing is proven yet. At this moment a malicious relay *could* substitute its own ephemeral key in
each direction, establish a separate shared secret with each peer, and read everything while relaying
it on — the classic machine-in-the-middle.

**Step 2 — bind it.** Each side signs a hash of the transcript — **its own ephemeral public key, the
peer's nonce, and the peer's ephemeral public key** — with its **node private key**, and sends the
signature.

This defeats the substitution: the relay cannot produce a signature over its substituted key, because
it does not hold the peer's node private key. And the **nonce defeats replay** — a captured signature
from an earlier session will not match a fresh nonce.

**Verification is the same check as the QUIC hook (§8.4 step 3):** does the signature verify under the
peer's node public key, and does the peer's certificate chain to an owner key this node recognises?
Rejection is silent.

After step 2 both properties hold: the peer is authenticated, and the shared secret was never visible
to the relay. The §12.1 framing then runs inside that encrypted stream, single-stream and sequential
per §10.3.

### 10.8 Build implications

- A second wire *transport*, not a second wire *format* — §12.1 framing and the §12.2 messages are
  unchanged. Only the carrier and the handshake differ.
- The handshake in §10.7 is new code with no QUIC equivalent to lean on. It is the main cost of this
  decision.
- Relay capability advertisement gains a **TCP-capable flag**, and peer exchange gains the filter in
  §10.6.

---

## 11. The seed and relay are ordinary nodes

### 11.1 One binary, one code path

**Every node can be both seed and relay.** Deploying "a seed" means deploying the ordinary daemon with
a public address and the relevant capabilities enabled. No separate service, no separate codebase, no
separate protocol — and the code the infrastructure runs is the code every user runs, which is
dogfooding in the strongest sense.

The hosted node is a **flavour with no owner and no project data**: it participates in discovery
without participating in work. Same binary, empty workload.

### 11.2 Seeding is nearly free; relaying is not

| | Seeding | Relaying |
|---|---|---|
| Cost | a table of addresses and a socket | **your bandwidth, indefinitely** |
| Who can | anyone publicly reachable | anyone publicly reachable (plus DNS for TCP fallback — §10.5) |
| Default | on when reachable | on when reachable, **capped** |

Most home nodes **cannot** relay anyway, because they sit behind exactly the routers that created the
problem. Which is the whole reason a hosted node exists.

### 11.3 Relay capability is auto-detected, throttled, and passively distributed

- **Auto-detected, not a checkbox.** A node asks the seed *can you reach me at this address?* If yes,
  it advertises relay capability; if no, it stays quiet. Home nodes behind NAT self-select out without
  the user knowing there was a question.
- **Throttled.** Relay traffic is capped at a configurable share of uplink bandwidth, so someone
  else's partition healing cannot saturate your connection.
- **Distributed passively.** Peer exchange already solves distribution — a slow relay simply loses out
  as peers pick other paths. No coordinator, no rebalancing logic.
- **Home relaying is possible but not expected.** It needs manual port forwarding plus dynamic DNS
  (home addresses change). Doable; more friction than most people will accept.

### 11.4 Hosting: Azure Container Instances

**The seed node runs on Azure Container Instances (ACI), doubling as the default relay.**

ACI has no ingress layer at all: point it at an image, declare ports including UDP, get a public IP,
pay per second. What is given up by not using Container Apps: autoscaling, managed certificates,
custom domains, revisions. None of which a UDP broker needs.

| Concern | Position |
|---|---|
| **DNS** | Request a static IP, point an A record at it, manage DNS yourself (Cloudflare or Azure DNS). If the instance moves, update one record. Required anyway for the TCP fallback certificate (§10.5). |
| **Security posture** | ACI gives an NSG and little else — no WAF, no managed identity out of the box. But **the attack surface is already tiny**: one UDP port, a custom binary protocol, and a handshake that rejects unknown peers before any real work. A WAF protects HTTP and would do nothing here. **The real security is in the protocol.** |
| **DDoS** | The one legitimate concern, since UDP suits amplification. Mitigations: NSG rate limiting, QUIC's mandatory round trip before real work (and the padded Initial packet), and Azure's free platform-level volumetric protection. Not naked; not as wrapped as ACA. Accepted tradeoff. |
| **Kubernetes** | None. ACI is explicitly the no-Kubernetes option. |
| **Billing** | More line items than ACA (compute/sec, memory/sec, egress) but tiny in absolute terms for a broker workload, and likely *cheaper* in practice. |
| **Scale to zero** | **Irrelevant, and wrong.** A seed that is not reachable is useless. Always-on is the correct model. |

**Verify before committing:** ACI has existed since ~2017 and was somewhat overshadowed when ACA
launched in 2022. A search found no deprecation notice, but confirm independently before building on
it (§17, open item).

---

## 12. The wire protocol

Six message types, three shapes. The handshake is **not** among them: QUIC already did it (§8.4), and
on the fallback path §10.7 does it.

### 12.1 Framing

Every message carries a **type byte** and a **length prefix**. Requests additionally carry a
**correlation ID** so responses can be matched. **Unknown types are ignored rather than treated as
errors**, so a newer node's messages do not break an older peer.

Identical on both transports.

### 12.2 The messages

| # | Type | Shape | Purpose |
|---|---|---|---|
| 1 | Join request | request | present an invite, get admitted |
| 2 | Project query | request | what do you have, and how far along are you |
| 3 | Event range request | request | send me everything after this point |
| 4 | Event push | push | something happened here; no response expected |
| 5 | Peer exchange | gossip | trade address hints |
| 6 | *(reserved)* | | |

**1. Join request.** Carries the project identifier, the joiner's owner public key, and the node
certificate, wrapped in proof of the invite token. **Synchronous**: the invite already constitutes
approval, so the far side verifies it and admits immediately. *(Not pending a human decision — without
an invite you would not know the project identifier or which node to ask in the first place.)*

**2. Project query.** *What have you got?* Response: project identifiers plus a **version marker** per
project — the highest **stream version** per stream, or a hash over them. Enough to answer *am I
current?* without transferring anything. Only then request events, and only for projects you share and
are behind on. See §12.6 on why this is stream version and never a global sequence.

**3. Event range request.** *For this project, everything after this point.* The point is named as
**stream identity plus the last stream version held for it** — never a timestamp, because clocks lie,
and never a store-global sequence number, because those are local (§12.6). Responses may be
large and want chunking, and this is where QUIC's independent streams earn their keep.

Two modes, same message:
- **Bootstrap** — an empty position set means *from the beginning*. A brand-new node holds nothing, so
  there is no point to name.
- **Catch-up** — the stream/sequence set, for a node that has been offline.

Two properties fall out, both worth having:
- **Position is derived entirely from what you hold**, so you need not remember who you synced with or
  when. Correct after a crash, and correct when syncing from a completely different peer.
- **Naturally idempotent.** Ask twice, append nothing new.

**4. Event push.** Fire and forget — a claim, a run completing. No acknowledgement, which sounds
reckless until you notice **the range request already covers gaps**. A lost push is caught by the next
sync.

> **Push is the fast path; sync is the correct path.** Reliability is not needed in the push because
> correctness lives elsewhere.

**5. Peer exchange.** Periodically tell peers who else you know: node identifier, last known address,
when last seen, and **capability flags** (relay-capable, TCP-fallback-capable). **Hints, not facts** —
a stale address just fails and is discarded. This is what makes the mesh self-healing (lose the seed
and everyone still knows several routes) and it is also how **relay capability propagates**, with no
coordinator deciding anything.

**The six are a floor, not a ceiling.** Revocation announcements, succession statement distribution,
and awaiting-materialisation surfacing all need messages and none are designed. Deliberately left to
be discovered while building rather than specified in the abstract.

### 12.3 Two distinctions worth stating plainly

**Owner-to-owner enrolment vs cross-owner join.** Same human syncing a second machine means
*everything is yours* and the sync is wholesale. A cross-owner join concerns one specific project,
agreed in advance. Same messages, different scope — and this is why the project query's list is
sometimes "all of them" and sometimes exactly one.

**Gossip is project-shaped.** Sarah's event push travels to *her* project peers, who can read and
verify it, and onward to theirs — two or three hops and it is everywhere. So you need no connection to
Sarah, only a connection to someone who eventually has one.

Crucially it does **not** travel through nodes outside the project. They hold no project keys, so they
would be forwarding an opaque blob they cannot verify — a spam vector for no benefit. **Cross-project
relaying is for discovery only**: helping peers find each other, never carrying payloads.

**The whole event travels.** Events are small — a claim is a few hundred bytes — and making every
recipient dial the origin would defeat the point of gossip.

### 12.4 Connection maintenance

- **One connection to a project peer is the floor**, and is genuinely sufficient for correctness.
- **One is fragile**, though: if that peer goes offline you are isolated and may not notice.
- **Target two or three warm connections**, dialling the rest on demand. Redundancy against churn, and
  pushes reach the mesh by multiple routes. Peer exchange fills the gaps as nodes come and go.
- **A QUIC connection is cheap to hold but not free** — keepalives, state on both sides, and a home
  node may sleep or change networks. Connection migration helps: a warm connection survives a network
  change.
- **The two-owner case is special.** With only you and Sarah there is no redundancy, so both sides
  reconnect on startup and retry with backoff. **The seed matters far more here**: in a large mesh you
  can bootstrap from anyone, but with two nodes the seed may be the only possible introduction. And if
  she is simply offline, you just work — events queue and sync when she appears, which is the entire
  point of local-first.

### 12.5 Certificates on the wire vs. in the store

The rule: **a certificate travels with anything that will be repeated by someone else.**

- **Transient messages** — queries, heartbeats, "do you carry project X" — carry a signature only.
  Identity was settled once in the handshake, and the message dies at the receiving node.
- **Stored events** carry signature *and* certificate as part of their persisted form, because they
  outlive the connection. Sarah's node relays your event to a third peer that has never handshaken
  with your laptop, and that peer must verify independently rather than take Sarah's word for it.

**Do not build this yet.** There is nothing to relay to, and it would be dead weight in every
aggregate today. What matters now is not foreclosing it: leave room for a signed envelope *around* an
event, rather than assuming an event is only ever the thing your own node wrote.

---

### 12.6 Stream version is portable; store sequence is not

**This distinction is load-bearing and easy to get wrong**, because Marten's event table carries both
numbers and their names invite confusion.

| Field | Meaning | Portable? |
|---|---|---|
| `IEvent.Version` | the event's position **within its stream** | **Yes.** Part of the event's identity. Identical on every node that holds it. |
| `IEvent.Sequence` | a **store-global** counter, assigned as the event lands in *this* database | **No.** Purely local. Two nodes will assign the same event different values. |

So **every sync position is stream identity plus stream version**, and `Sequence` must never appear in
a protocol message, a version marker, or a comparison between nodes. It is a local read cursor for
walking your own table in order, nothing more.

**Why the divergence is harmless.** Events travel as *events*, not as database rows — nobody ships a
Postgres table. A receiving node appends what arrives and its own store assigns fresh sequence numbers
in local arrival order. Two nodes holding identical event sets will order them differently on disk and
both be correct.

**What the receiver must preserve exactly**: the stream ID and the version as they arrived. Those are
the event's identity. Everything else about how it is stored is local bookkeeping.

This is also why the partition handling in §6.7–§6.8 needs no storage machinery. Two runs against one
task are two streams; nothing is renumbered, interleaved, or deleted, and nobody has to reconcile
physical ordering. In a row-based store you would be diffing state and asking which version of the
truth wins. With events you are appending facts, and facts accumulate rather than conflict.

**Implementation caveat — Marten's append mode.** Marten's `EventAppendMode.Quick` is faster but
**does not populate `Version` and `Sequence` at append time**. Since the entire sync position depends
on `Version`, Hall9k's store must either run in `Rich` mode or establish stream versions by another
means. **Verify the configured append mode before building slice 3.**

## 13. Sync semantics

**The event log syncs eagerly, everywhere.** Projects, tasks, claims, runs, discovery metadata —
Postgres rows, megabytes not gigabytes. All nodes stay synchronised on which projects and tasks exist.

**File payloads sync lazily**, on claim. Discovery artifacts, transcripts, and attachments are *not*
replicated by default — claiming a task pulls its content from a peer that has it. This fits the
content-addressed attachment store (`PLAN.md` §8.1) exactly: the claimer already knows which hashes it
needs, and "do you have hash abc123?" is the right primitive either way.

**Git repositories clone lazily**, triggered by the same claim.

**Same-owner sync relaxes the lazy rule.** Eager metadata / lazy payloads was reasoned about with
*cross-owner* peers in mind. Between two machines of the same owner on a fast link, pulling attachments
is essentially free, and there is real value in the laptop being a genuine mirror rather than a thin
index. Rule: **same owner on a fast link pulls everything; anything else waits for a claim.** A newly
enrolled node **syncs all of that owner's projects by default** — the certificate is owner-scoped, so
which projects sync is a preference, not a trust question.

**Claiming while the source node is offline holds the claim and syncs later.** The claim lives in the
event log regardless; the payload is only a cache to warm. This implies an *awaiting materialisation*
state so an agent is never dispatched into a half-empty working directory — how that surfaces is open
(§17).

**A gap this exposes**: `h9k` currently assumes the repository is already present, because you are
standing in it. Cross-node claiming breaks that — a machine can hold the complete event log for a
project it has never cloned. So there is a **materialisation step ahead of work-tree creation**: do I
have this repo? No → clone it from the project's GitHub connection → create the work tree as normal.
Pleasingly, this is the same lazy-materialisation shape as attachments, with git doing the fetching.

Note `init` is never lazy and never needs to be: when a project is created the repository already
exists on that machine. **Only cloning is ever lazy.**

### 13.1 Events written before P2P existed

Pre-P2P events are unsigned. The tempting answer — "they never leave this machine, so nobody needs to
verify them" — is **wrong**: history propagates. Sarah syncs your back catalogue, then relays it onward
to a node that will ask, reasonably, who signed this.

So a one-off migration is needed at the moment P2P is switched on. Two candidates:

- **Retroactive signing** — walk the log and sign each event. Honest enough: you are attesting *now* to
  what you wrote *then*.
- **A checkpoint** — hash-chain the log (each event commits to the previous event's hash) and sign the
  tip once. One signature vouches for the entire history, because altering event forty breaks every
  hash after it. Bitcoin's block chaining, in miniature.

Either is cheap and neither is owed today. It is a switch-on task, not a tax carried in v0.

---

## 14. Claims across nodes

**Claims are recorded against the OWNER, not the node.** Sarah needs to know *Brian* holds task ABC,
not which of his machines. The node key signs the event (who typed this); the payload records the
claiming owner. Sarah's node resolves node → owner and rejects a duplicate claim from Brian's second
machine as a same-owner duplicate.

`TaskClaimed(Id, NodeId, OwnerId, LeaseGeneration, RunId, ClaimedAt)` already carries exactly this
shape. **No change needed.**

Remote lease expiry is already settled and unchanged: never auto-stolen, owner-aware escalation,
takeover as a deliberate human act, fencing token making unenforceable remote revocation safe
(`PLAN.md` §16 #12).

### 14.1 The cross-owner claim race

Two owners claim the same task at roughly the same time. **Resolved by hash arbitration** — the same
mechanism as promotion reconciliation (§6.6). One mechanism, one code path.

Where the work produced code, **GitHub also arbitrates**: a remote branch or PR is a fact both sides
can check, and creating one is atomic. This is the external-source-of-truth decision paying off rather
than new machinery.

Two residual shapes:

- **Both pushed successfully.** Two branches, two PRs, one task. Not broken — a human picks. Wasteful,
  not incorrect.
- **A task that never reaches a PR** — a spike, an investigation, discovery work that produces a
  transcript and a conclusion. Nothing lands on GitHub, so nothing arbitrates. Here the triage of §6.8
  is the whole answer.

**Net: the claim race is a cost problem, not a correctness one.** Anything producing code resolves
itself through GitHub; anything that does not produce code does little harm duplicated, and the
review task recovers the value.

---

## 15. Build order

Each slice is independently useful, and the early ones need none of the later machinery.

1. **Keys and enrolment.** Owner/node keypairs on the existing aggregates, certificate chain, enrolment
   token, signed events. **Plus succession statements minted during enrolment, and `h9k node invite` /
   `h9k owner promote` with the promotion prompt** (§6.1–§6.2). Pure local work; no network. The
   succession chain is cheapest to build here and expensive to retrofit, since it wants to be signed at
   enrolment time by a key that is still alive.
2. **LAN-only via mDNS.** Two of your own machines, same network. No seed, no punching, no relay. Proves
   sync, materialisation, and cross-node claims end to end.
3. **Seed + direct.** Deploy the ordinary daemon to ACI with public-address flags; address book, hole
   punching, QUIC.
4. **QUIC relay.** Auto-detection, throttling, and the byte pump.
5. **Gossip.** Peer exchange, bounded project queries, address book diversity.
6. **TCP/443 fallback.** The exception path (§10): fallback trigger, the inner handshake, capability
   flags, and the seed's capability-filtered peer exchange. Last because it is the rarest case and
   depends on everything above.

**Effort, honestly: medium-large.** The hard parts are few — punching with reliable timeouts and
fallback, the QUIC handshake with key verification, the fallback inner handshake, gossip with sane
dedup and backoff — each days rather than weeks. The infrastructure is genuinely easy (the relay is a
dictionary and a byte loop). What will eat time is the unglamorous middle: address book maintenance,
connection churn, peers that lie or stall mid-transfer, and testing all of it without ten real
machines. Call it ~90% node-side, ~10% infrastructure.

---

## 16. Rejected alternatives

Kept deliberately. Each of these was considered seriously and the reasoning is worth not repeating.

### Identity and keys

| Approach | Why not |
|---|---|
| **A keypair per project** | Buys unlinkability — the ability for observers not to correlate a person's activity across projects. That is the *opposite* of what an accountability model wants (`PLAN.md` §6.2: every node belongs to a human). It also multiplies key management by the number of projects for no benefit anyone asked for. |
| **Copying the owner private key to every node** | Defensible, and plenty of systems do it. Costs two real things. Every copy is another place the root of trust can leak, and a leaked owner key can certify nodes as you *forever* (a leaked enrolment token is dead in an hour). And it collapses per-node revocation: a laptop holding the owner key **is** you, so losing it means rotating your whole identity rather than revoking one certificate. |
| **Threshold signatures (2-of-3 multisig)** | The right instinct — it does solve the single-point-of-failure directly. Two snags. It is real cryptographic engineering (threshold Ed25519, or a Shamir split with a signing ceremony) and .NET library support is thin. And it requires two machines online *simultaneously* to enrol, trading a rare inconvenience for a routine one. Disproportionate for a key that signs perhaps five things in its life. Worth revisiting only if the trust root ever needs to survive an adversary rather than a dead SSD. |
| **Manual backup as the *only* answer** | Exporting the key to a password manager is sensible and remains possible. It is not adequate as the design, because it pushes ceremony onto every user of the tool. Hall9k manages code, not money; nobody is going to stamp a seed phrase into steel for this. |

### Partition handling

| Approach | Why not |
|---|---|
| **A quorum rule before promoting** | Both halves of a partition look correct from the inside (§6.5). A quorum protects against a *lonely* node, not a split, and the split is the case that matters. |
| **Detecting the partition condition** | "Don't promote while the uplink is down" requires knowing your view is incomplete, which is a liveness oracle and does not exist. The uplink is only one of many reasons two locations stop seeing each other, and none are reliably distinguishable from a genuinely dead primary. Reconcile after the fact instead. |
| **Lamport / hybrid logical clocks for the tiebreak** | Both capture causality more faithfully than a hash, but add per-node counters, exchange rules, and a second concept to reason about — for a benefit the recovery path already absorbs (§6.8). |
| **"Most recent event stream wins"** | Presupposes exactly what does not exist: a shared notion of time. A **causal** variant — *whichever stream already contains the other's event is provably later* — is genuinely stronger evidence than a hash and is noted as a possible future refinement, but it only helps when one side actually observed the other, so it cannot stand alone. |
| **Jitter-backoff (CAN-bus style) for the claim race** | Not deterministic on log replay, which is awkward for event sourcing. |

### Discovery

| Approach | Why not |
|---|---|
| **BitTorrent mainline DHT as rendezvous** | A DHT is only worth its complexity with a huge table, so the appeal was piggybacking someone else's. But mainline is BitTorrent, and corporate networks and ISPs throttle or block it on sight — breaking peering exactly where it is most needed. (Detection is not port-based in practice; DPI matches the handshake signature — byte `19` then the literal protocol string — a rule every appliance ships. Useful lesson in reverse: Hall9k's own protocol is unrecognisable by default, and TLS on 443 looks like ordinary HTTPS.) |
| **Git-remote rendezvous** (signed peer records in the project repo) | Burdens every project owner with repo overhead Hall9k shouldn't impose. The real exposure isn't read access — addresses are public in Bitcoin too, and unauthorised peers just get their handshake rejected — but publishing home IPs (privacy leak, DoS target) and the question of who can write. A private side repo solves it and makes the burden worse. Not fully closed off, but not the route. |
| **Bitcoin's crawler model** (seeds roam and publish reachable nodes) | Only works because Bitcoin nodes are *expected* to be publicly reachable. Developers on home and office networks mostly are not. Announce-and-expire is also strictly simpler: one endpoint, a dictionary, and expiry. |
| **Probing nodes for liveness** | Hits every NAT problem the peers themselves have, and turns a dumb table into a monitoring service holding state and opinions. Liveness is the node's job to assert. |
| **A rendezvous point per project** | Per-project setup, strangers become hard, and the rendezvous learns which projects exist. §7.1 inverts it. |
| **Hardcoding seed addresses in the binary** | Forecloses running a private mesh and forces a release to move the seed. Config with an overridable default list instead. The *URL-over-IP* part of the original reasoning was right and is retained. |

### Transport and hosting

| Approach | Why not |
|---|---|
| **TCP + TLS as the primary transport** | Head-of-line blocking, three round trips to first byte, no connection migration, and — decisively — **it cannot hole punch** (§8.3). |
| **ASP.NET Core / HTTP anywhere in the P2P layer** | None of this is HTTP. The seed listens on a UDP socket; there is no URL, route table, middleware, or controller. The only reason to want ASP.NET Core would be a browser, and there is no browser. |
| **SignalR for the relay leg** | A good abstraction over websockets for many-clients-calling-typed-methods-on-a-server; this is two peers piping opaque bytes. The hub model, protocol negotiation, and fallback transports are dead weight, and they couple the wire format to a framework. Doubly moot now that the QUIC relay has no HTTP leg at all. |
| **Azure Container Apps / Azure Functions** | **Inbound UDP is a hard no** — ACA ingress passes HTTP, HTTPS and TCP only (outbound UDP works, inbound does not). Disqualifying for a QUIC listener. |
| **Actively rerouting relay load** | Dynamic load-shifting needs coordination and a view of global state. Peer exchange already distributes passively — a slow relay simply loses out. |
| **Tor as the node transport** | Tempting: every node an onion service, dial-out only, stable address behind any NAT, no punching, no hole to keep open, no address churn. But 1–2s per round trip, poor throughput for payloads, and blocked on many corporate networks — the directory authorities are a fixed, trivially-blocked list, and DLP teams treat Tor as an exfiltration channel by policy. Building censorship circumvention into a developer tool is the wrong place to end up. **Also rejected as the UDP-blocked fallback specifically**, since it is blocked by policy on many of the very same networks. |
| **Accepting UDP-blocked networks as unsupported** | Corporate networks matter to the product. Writing them off is a real capability loss, not a rounding error. |
| **Multiplexing on the TCP fallback path** | The degraded path values correctness over latency, and push/sync already recovers anything delayed (§10.3). |

---

## 17. Open questions

Genuinely unresolved. Each is deliberately left rather than overlooked.

1. **Awaiting-materialisation surfacing.** Does a claim against un-synced payload surface as blocked,
   pending, or a quiet retry? (§13)
2. **Repo materialisation ahead of work-tree creation** on a node that has never cloned the project.
   Build alongside lazy payload sync. (§13)
3. **Node certificate revocation distribution** — how a revoked certificate propagates, and what a peer
   does with events signed before the revocation. Needs a message (§12.2).
4. **Succession statement distribution before any project exists.** They travel with project state, but
   an owner's *first* node has nowhere to put them. Probably local-only until the first sync; confirm
   when building slice 1. (§6.1)
5. **Retroactive signing vs. hash-chain checkpoint** for pre-P2P events. Decide at switch-on. (§13.1)
6. **Where the outstanding-review obligation is recorded**, and how it surfaces. Related to item 1 and
   probably shares a mechanism. (§6.8)
7. **Causal ordering as a tiebreak refinement.** "Whichever stream contains the other's event is
   provably later" could pre-empt hash arbitration where the evidence exists, falling back to the hash
   otherwise. Not in v1. (§6.6)
8. **Fallback relay pool size.** The DNS-hostname requirement makes volunteered fallback relays rare,
   leaving the seed as the practical single point for UDP-blocked nodes. Acceptable for v1; revisit if
   fallback usage is material. (§10.5)
9. **Cached relay staleness.** Volunteered relay addresses cached from permissive networks may be dead
   by the time they are needed. No expiry or refresh policy designed. (§10.6)
10. **0-RTT reconnection** — attractive for returning nodes, has replay caveats. Not needed for v1.
    (§8.4)
11. **ACI deprecation status** — verify independently before committing. (§11.4)
12. **Marten's configured append mode.** `Quick` mode does not populate `Version` at append time, and
    the entire sync position depends on it. Confirm the store runs in `Rich` mode, or establish stream
    versions another way. (§12.6)
13. **Whether QUIC rejection is observably silent.** The verification hook is confirmed present, but
    what the rejected peer actually sees is not. (§8.4)
