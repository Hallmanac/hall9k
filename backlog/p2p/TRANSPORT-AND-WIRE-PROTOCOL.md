# Hall9k — Transport, Hosting, and the Wire Protocol

Companion to `P2P-DESIGN.md` and `OWNER-KEY-LIFECYCLE.md`. Produced 2026-08-18.

This document covers the layer between "two nodes have found each other" and "two nodes agree
about the state of a project": the transport they speak, where the introducer runs, and the six
messages that actually flow.

**It supersedes parts of `P2P-DESIGN.md`.** §5.2, §8.4, §8.5, §9 and §12 all carry assumptions
that did not survive this session. The amendments are collected in §7 below rather than left
implicit; the superseded text is worth keeping in place with a pointer, because the *reasoning*
in it is still sound and only the conclusion moved.

---

## 1. Transport: QUIC, and only QUIC

### 1.1 The decision

**All peer traffic is QUIC over UDP, via `System.Net.Quic`.** No third-party package: QUIC ships
in .NET on top of MsQuic (present with the runtime on Windows, a small native library on Linux).

You **use** QUIC, you do not implement it. Packet layout, encryption, handshake, retransmission,
congestion control and stream multiplexing are the library's job. What Hall9k writes is the
framing *inside* the streams — which is §5 of this document, and nothing else.

Two layers that were conflated earlier in the design and should not be:

| Layer | Provides | Whose job |
|---|---|---|
| QUIC + TLS | a private, authenticated, multiplexed pipe | the library |
| Hall9k wire protocol | what the two nodes *say* over that pipe | ours |

### 1.2 Why QUIC rather than TCP + TLS

- **No head-of-line blocking.** TCP guarantees one global order, so a single lost packet stalls
  everything behind it. QUIC carries independent streams inside one connection: a large
  back-catalogue sync on one stream does not block a live event push on another.
- **One handshake, not two stacked.** TCP+TLS 1.2 costs three round trips before a useful byte
  moves. QUIC merges the transport and crypto handshakes into **one round trip** — and encryption
  is mandatory rather than optional, which means **certificate exchange happens as part of
  connecting, not as a protocol message we design.**
- **Connection migration.** A connection is identified by its connection ID, not the
  IP/port four-tuple, so changing networks does not kill it. A laptop moving from home to a coffee
  shop mid-sync keeps the sync. **No resume logic to write.**
- **It is what makes hole punching possible at all.** See §1.3.
- **It ships in userspace.** TCP's mechanics live in the kernel, which is why TCP improvements take
  a decade to roll out; QUIC's logic sits above the kernel's UDP socket and ships with the next
  deployment. (UDP itself is still kernel; only the QUIC layer on top is userspace. "Userspace"
  here means privilege level, not a user account.)

### 1.3 Why UDP specifically enables punching

A router notes outbound traffic and thereafter admits replies from the address it was sent to.
**That note is the hole.** UDP's statelessness — normally a weakness — is exactly what makes the
trick work: the router does not care whether your packet ever arrived, so a packet fired at a peer
who has not yet fired back still opens the mapping.

TCP cannot do this. It insists on a SYN handshake in strict order, so when both sides dial
simultaneously both SYNs are dropped and both sides time out.

TCP's alternatives are therefore: **relay** (both parties dial *out* to a middleman that copies
bytes between two pipes — note the relay never dials either party), **manual port forwarding**, or
**UPnP** (a separate prior conversation with the router, not part of the SYN — and a port opened
that way is open to *anyone*, which is why routers ship with it disabled, and carrier-grade NAT
defeats it regardless). **UPnP works often enough to try as an optimisation and never often enough
to depend on.**

### 1.4 No ASP.NET Core anywhere in the P2P layer

**None of this is HTTP.** The seed listens on a UDP socket for QUIC connections. There is no URL,
no route table, no middleware, no controllers. The only reason to want ASP.NET Core would be a
browser reaching the service, and there is no browser.

This retires the assumption in `P2P-DESIGN.md` §9 that seed and relay are ASP.NET Core minimal
APIs, and the assumption in §8.4 that the relay leg is a websocket upgrade on 443.

### 1.5 The handshake, and the one hook we write

Four steps, one round trip:

1. **Initial packet.** An invented connection ID, the QUIC version, and a TLS ClientHello carrying
   an **ephemeral** public value plus the supported cipher suite list, padded to ~1200 bytes
   (deliberately, to prevent amplification). The header is in the clear; the payload is encrypted
   with keys derived from the connection ID, so anyone *can* decrypt it — this is format
   consistency, not secrecy. **No certificate, no identity, no project information.** An observer
   learns only that someone is opening a QUIC connection.
2. **The reply.** Her connection ID, her own ephemeral public value, the cipher suite she chose
   (by preference order, strongest first), her **node certificate**, and a **signature over the
   handshake so far** made with her node private key. The certificate is sent here rather than in
   step 1 precisely because initial keys now exist to protect it.
3. **Our turn, symmetrically** — node certificate plus signature. **This is the only step Hall9k
   writes any code for**: the library does the cryptographic verification and then hands over the
   certificate and asks *do you accept this?* Our answer is: **does this chain to an owner key I
   recognise as a member of a project I care about?** If not, the connection closes with no error
   and no explanation — we simply stop existing to the far side.
4. **Confirmation.** Connection live; symmetric encryption thereafter.

Note what this collapses: **a TLS channel and mutual identity verification in a single round trip.**
Standard TLS lets the client stay anonymous (that is web browsing); Hall9k configures QUIC to
**require a client certificate**, which is a setting rather than a message.

There is also a **0-RTT** mode for reconnecting to a peer already known, which sends application
data in the very first packet. Minor replay caveats; attractive for reconnecting nodes. Not
required for v1.

### 1.6 Two keypairs, two jobs — and what the handshake keys actually do

This was a persistent source of confusion and is worth stating precisely.

| | Ephemeral keypair | Node keypair |
|---|---|---|
| Lifetime | generated fresh per connection, discarded after | long-lived, in the certificate |
| Purpose | **key agreement** | **signing** (identity) |
| Curve/cost | X25519, microseconds | signing curve, once per handshake |

**Nothing is encrypted with either private key.** The widespread mental model — "encrypt with the
private key, decrypt with the public" — describes *signing*, and even then only loosely.

Key agreement (Diffie–Hellman) works like this: each side sends only a public value. Each side then
combines **its own private half with the other's public half**, and the mathematics yields **the
same number on both sides**. That number never crosses the wire; an eavesdropper holding both
public values cannot compute it. It is then run through a derivation function to produce the
symmetric keys used for the rest of the connection.

Because the keypair is thrown away, this gives **forward secrecy**: traffic recorded today stays
unreadable even if a node's long-term key leaks years later.

Signing is a **different operation with opposite intent**: hash the thing being signed, apply a
transformation using the private key, and the output is public and verifiable by anyone. Key
agreement produces a shared secret; signing produces public evidence.

**Verification does not recompute the signature.** It cannot — that would need the private key.
The signature, the hash, and the signer's public key go into a verify function that answers yes or
no. Sign with private, verify with public, never the reverse.

The intuition underneath, without the mathematics: **the keys are two halves of one relationship.**
There is an operation easy forwards and effectively impossible backwards — for elliptic curves,
multiplying a point by a number. The private key is the number; the public key is the result. They
are not two independent things that happen to work together; the public key is *derived from* the
private one, so anything the private key does leaves a fingerprint the public key can check.

### 1.7 What the certificate is, structurally

`node public key + signature over it by the owner private key + metadata`. A **signed statement**,
not an actor: the certificate does not sign anything. The node *private* key makes the handshake
signature; the certificate proves that node key belongs to a particular owner.

And note what the receiver needs: **not the sender's owner public key over the wire** — that would
be circular — but the owner public key it *already holds*, obtained out of band when it joined the
project. That prior possession is the entire root of trust.

---

## 2. The seed and relay are ordinary nodes

### 2.1 One binary, one code path

**Every node can be both seed and relay.** Deploying "a seed" means deploying the ordinary daemon
with a public address and the relevant capabilities enabled. No separate service, no separate
codebase, no separate protocol — and the code the infrastructure runs is the code every user runs,
which is dogfooding in the strongest sense.

The hosted node is a **flavour with no owner and no project data**: it participates in discovery
without participating in work. Same binary, empty workload.

### 2.2 Seeding is nearly free; relaying is not

These are deliberately not the same commitment:

| | Seeding | Relaying |
|---|---|---|
| Cost | a table of addresses and a socket | **your bandwidth, indefinitely** |
| Who can | anyone publicly reachable | anyone publicly reachable |
| Default | on when reachable | on when reachable, **capped** |

Most home nodes **cannot** relay anyway, because they sit behind exactly the routers that created
the problem. Which is the whole reason a hosted node exists.

### 2.3 Relay capability is auto-detected, throttled, and passively distributed

- **Auto-detected, not a checkbox.** A node asks the seed *can you reach me at this address?* If
  yes, it advertises relay capability; if no, it stays quiet. Home nodes behind NAT self-select out
  without the user knowing there was a question.
- **Throttled.** Relay traffic is capped at a configurable share of uplink bandwidth, so someone
  else's partition healing cannot saturate your connection.
- **Distributed passively.** *Rejected: actively rerouting relay load.* Dynamic load-shifting needs
  coordination and a view of global state. **Peer exchange already solves it** — a slow relay simply
  loses out as peers pick other paths. No coordinator, no rebalancing logic.
- **Home relaying is possible but not expected.** It needs manual port forwarding plus dynamic DNS
  (home addresses change). Doable; more friction than most people will accept.

### 2.4 Hosting: Azure Container Instances

**Decision: the seed node runs on Azure Container Instances (ACI), doubling as the default relay.**

**Why not Container Apps.** ACA's ingress passes HTTP, HTTPS and TCP only — **inbound UDP is a hard
no**, though outbound works. That is disqualifying for a QUIC listener. Azure Functions carries the
same restriction. ACI has no ingress layer at all: point it at an image, declare ports including
UDP, get a public IP, pay per second.

What is given up by not using ACA: autoscaling, managed certificates, custom domains, revisions.
None of which a UDP broker needs.

| Concern | Position |
|---|---|
| **DNS** | Request a static IP, point an A record at it, manage DNS yourself (Cloudflare or Azure DNS). If the instance moves, update one record. |
| **Security posture** | ACI gives an NSG and little else — no WAF, no managed identity out of the box. But **the attack surface is already tiny**: one UDP port, a custom binary protocol, and a handshake that rejects unknown peers before any real work. A WAF protects HTTP and would do nothing here. **The real security is in the protocol.** |
| **DDoS** | The one legitimate concern, since UDP suits amplification. Mitigations: NSG rate limiting, QUIC's mandatory round trip before real work (and the padded Initial packet), and Azure's free platform-level volumetric protection. Not naked; not as wrapped as ACA. Accepted tradeoff. |
| **Kubernetes** | None. ACI is explicitly the no-Kubernetes option. |
| **Billing** | More line items than ACA (compute/sec, memory/sec, egress) but tiny in absolute terms for a broker workload, and likely *cheaper* in practice. |
| **Scale to zero** | **Irrelevant, and wrong.** A seed that is not reachable is useless. Always-on is the correct model. |

**Verify before committing:** ACI has existed since ~2017 and was somewhat overshadowed when ACA
launched in 2022. A search found no deprecation notice (only unrelated Reserved VM Instance
retirements and ACA preview-feature retirements), but this is worth confirming independently —
e.g. `azurecharts.com/timeboards/deprecations` — before building on it.

---

## 3. Discovery, refined

The mechanics from `P2P-DESIGN.md` §5–§8 were re-derived and hold. Refinements and corrections:

- **Same house**: mDNS, direct connection, no internet involved at all.
- **Across the internet**: both sides reach a node with a public address; it tells each where the
  other appears from *outside*; both fire simultaneously; both routers begin admitting replies;
  the streams meet and the seed drops out.
- **Sarah does not know you want her.** She holds a warm connection to the seed with heartbeats
  keeping her mapping open, so the seed can **tap her on the shoulder**. The seed's real service is
  being reachable at a known address.
- **When the seed has no live connection to Sarah**, it asks the peers it does have. A peer still
  holding a path to Sarah becomes the shoulder-tapper. **Addresses are hints, not facts.** If
  nobody holds a path, Sarah is genuinely offline — queue locally and sync later.
- **The shoulder-tapping peer nudges Sarah itself** rather than handing her address back to the
  seed, and tells the asker where to aim. **The seed is a directory of first resort, not a
  switchboard.**
- **Multi-hop query answers come back directly**, not back along the hop path, because the query
  carries the asker's address. **Gossip is a search fabric, not a routing fabric.** Needs a hop cap
  (three or four).
- **The introducer's actual service is synchronising the attempt** — telling both sides at roughly
  the same instant, since a delayed nudge means the hole has already closed.
- **Correction to `P2P-DESIGN.md` §5.2**: seed addresses belong in **configuration with an
  overridable default list**, not baked into the binary. Hardcoding forecloses running a private
  mesh and forces a release to move the seed. (This partially retires the "hardcoded URL, not an
  IP" reasoning: the URL-over-IP argument was right, the hardcoding was not.)

---

## 4. Invites, and what a token carries

An invite is what makes a stranger's certificate mean anything. Sarah's node mints a token; it
travels out of band; `h9k join <token>` presents it.

**Contents:**

| Field | Why |
|---|---|
| Project identifier | which project is being joined |
| **Sarah's owner public key** | **the critical one** — without it you cannot verify her node certificate during the handshake and would be trusting whoever answered |
| Seed/node addresses | where to aim the first packet |
| Expiry | so an intercepted token is not usable a year later |
| Signature by Sarah's owner key over the above | so the token cannot be forged |

The token therefore does work at **two** layers, which is easy to miss: the owner public key is
loaded **before connecting** so the QUIC verification hook (§1.5 step 3) can say yes, and the token
itself is then presented **inside** the join message. Handshake mechanics belong to QUIC; the trust
decision is ours, and the invite is what supplies it.

This is consistent with `OWNER-KEY-LIFECYCLE.md` §4.7's two token types (node enrolment vs project
invite) — this is the project invite, and it requires no owner private key to mint.

---

## 5. The wire protocol

Six message types, three shapes. The handshake is **not** among them: QUIC already did it.

### 5.1 Framing

Every message carries a **type byte** and a **length prefix**. Requests additionally carry a
**correlation ID** so responses can be matched. **Unknown types are ignored rather than treated as
errors**, so a newer node's messages do not break an older peer.

### 5.2 The six

| # | Type | Shape | Purpose |
|---|---|---|---|
| 1 | Join request | request | present an invite, get admitted |
| 2 | Project query | request | what do you have, and how far along are you |
| 3 | Event range request | request | send me everything after this point |
| 4 | Event push | push | something happened here; no response expected |
| 5 | Peer exchange | gossip | trade address hints |
| 6 | *(reserved — see §8)* | | |

**1. Join request.** Carries the project identifier, the joiner's owner public key, and the node
certificate, wrapped in proof of the invite token. **Synchronous**: the invite already constitutes
approval, so the far side verifies it and admits immediately. *(An earlier draft had this pending
a human decision — wrong: without an invite you would not know the project identifier or which node
to ask in the first place.)*

**2. Project query.** *What have you got?* Response: project identifiers plus a **version marker**
per project — highest sequence number per stream, or a hash. Enough to answer *am I current?*
without transferring anything. Only then request events, and only for projects you share and are
behind on.

**3. Event range request.** *For this project, everything after this point.* The point is named as
**the last sequence number held per stream** — never a timestamp, because clocks lie. Responses may
be large and want chunking, and this is where QUIC's independent streams earn their keep.

Two modes, same message:
- **Bootstrap** — an empty position set means *from the beginning*. A brand-new node holds nothing,
  so there is no point to name.
- **Catch-up** — the stream/sequence set, for a node that has been offline.

Two properties fall out, both worth having:
- **Position is derived entirely from what you hold**, so you need not remember who you synced with
  or when. Correct after a crash, and correct when syncing from a completely different peer.
- **Naturally idempotent.** Ask twice, append nothing new.

**4. Event push.** Fire and forget — a claim, a run completing. No acknowledgement, which sounds
reckless until you notice **the range request already covers gaps**. A lost push is caught by the
next sync.

> **Push is the fast path; sync is the correct path.** Reliability is not needed in the push
> because correctness lives elsewhere.

**5. Peer exchange.** Periodically tell peers who else you know: node identifier, last known
address, when last seen. **Hints, not facts** — a stale address just fails and is discarded. This
is what makes the mesh self-healing (lose the seed and everyone still knows several routes) and it
is also how **relay capability propagates**, with no coordinator deciding anything.

### 5.3 Two distinctions worth stating plainly

**Owner-to-owner enrolment vs cross-owner join.** Same human syncing a second machine means
*everything is yours* and the sync is wholesale. A cross-owner join concerns one specific project,
agreed in advance. Same messages, different scope — and this is why the project query's list is
sometimes "all of them" and sometimes exactly one.

**Gossip is project-shaped.** Sarah's event push travels to *her* project peers, who can read and
verify it, and onward to theirs — two or three hops and it is everywhere. So you need no connection
to Sarah, only a connection to someone who eventually has one.

Crucially it does **not** travel through nodes outside the project. They hold no project keys, so
they would be forwarding an opaque blob they cannot verify — a spam vector for no benefit.
**Cross-project relaying is for discovery only**: helping peers find each other, never carrying
payloads.

**The whole event travels.** Events are small — a claim is a few hundred bytes — and making every
recipient dial the origin would defeat the point of gossip.

### 5.4 Connection maintenance

- **One connection to a project peer is the floor**, and is genuinely sufficient for correctness.
- **One is fragile**, though: if that peer goes offline you are isolated and may not notice.
- **Target two or three warm connections**, dialling the rest on demand. Redundancy against churn,
  and pushes reach the mesh by multiple routes. Peer exchange fills the gaps as nodes come and go.
- **A QUIC connection is cheap to hold but not free** — keepalives, state on both sides, and a home
  node may sleep or change networks. Connection migration helps: a warm connection survives a
  network change.
- **The two-owner case is special.** With only you and Sarah there is no redundancy, so both sides
  reconnect on startup and retry with backoff. **The seed matters far more here**: in a large mesh
  you can bootstrap from anyone, but with two nodes the seed may be the only possible introduction.
  And if she is simply offline, you just work — events queue and sync when she appears, which is
  the entire point of local-first.

---

## 6. Amendment: the "losing node" message dissolves

`OWNER-KEY-LIFECYCLE.md` §4.10.5 left one residual: the wording `h9k node invite` shows when it
discovers this node's owner key lost a reconciliation.

**That residual is dissolved, and no copy is needed.** A non-primary node should not *fail* at
minting — it should **reach the node holding the owner key and act on its behalf**. Minting is a
request; where the key physically lives is invisible to the user.

The only failure that can surface is *"cannot reach any machine holding your owner key"* — which
is the promotion prompt already designed in §4.5. So **losing a reconciliation surfaces nowhere**,
which is exactly consistent with §4.10.1's finding that key reconciliation is silent plumbing.

---

## 7. `P2P-DESIGN.md` — amendments

| Section | Amendment |
|---|---|
| **§5.2** | Seed addresses live in **config with an overridable default list**, not hardcoded in the binary. The URL-over-IP reasoning stands; the hardcoding does not. |
| **§5.2** | The seed is **not an HTTP announce/query endpoint**. It is an ordinary node reached over QUIC; announce/query becomes a message pair in the same framing as §5 above. |
| **§8.4** | **Superseded in mechanism, retained in reasoning.** The relay is no longer a distinct websocket-over-443 service in ASP.NET Core; it is an ordinary node with relaying enabled, speaking QUIC. What survives: the relay stays in the middle for the whole session, it is dumb by construction, and it sees ciphertext only. **The SignalR rejection stands and is now doubly true** — there is no HTTP leg at all. |
| **§8.5** | Transport table replaced: **all three paths are QUIC**. LAN direct, punched, and relayed differ only in how the packets get there. |
| **§9** | **Superseded.** Not ASP.NET Core minimal APIs on Container Apps; the ordinary daemon on **Azure Container Instances**, seed and relay in one process. The seed/relay contrast table in §9 remains a good description of the two *workloads* even though they now share a process. |
| **§12** | Add: *rejected — active relay load rerouting* (peer exchange already distributes passively); *rejected — ASP.NET Core / HTTP anywhere in the P2P layer* (nothing here is HTTP); *rejected — Azure Container Apps and Azure Functions* (no inbound UDP). |
| **§13 Q3** | **Closed** by §5 above (the wire protocol). |
| **§14** | Build slice 3 ("seed + direct") now means *deploying the daemon to ACI with public-address flags*, not writing a separate service. Slice 4 ("relay") shrinks to auto-detection, throttling, and the pump. |

---

## 8. What is still open

1. **UDP blocked outright.** §8.4's original relay lived on 443 precisely because some corporate and
   guest networks block outbound UDP entirely — and a QUIC relay cannot help there, because
   reaching it needs the very thing that is blocked. **This was not resolved this session and
   should not be assumed away.** The options are a TCP/443 fallback transport (reintroducing a
   second wire format for the exception path), accepting those networks as unsupported, or the Tor
   fallback already noted in §12. *Flagged as a real gap, not a detail.*
2. **The message list is a floor, not a ceiling.** Revocation announcements, succession statement
   distribution, and awaiting-materialisation surfacing all need messages and none are designed.
   Deliberately left to be discovered while building rather than specified in the abstract.
3. **Do the two tiebreaks want one mechanism?** The promotion reconciliation (§4.9) and the claim
   race (`PLAN.md` §15 row 13) both need deterministic ordering without a trusted clock. Flagged
   three times now; still unanswered.
4. **0-RTT reconnection** — attractive for returning nodes, has replay caveats. Not needed for v1.
5. **ACI deprecation status** — verify independently before committing (§2.4).

---

## 9. The UDP-blocked exception path: a TCP/443 fallback

This section closes open item 1 from §8. It is the exception path — everything in §1–§5 remains
the normal path, and nothing here changes it.

### 9.1 The problem, stated precisely

Some corporate and guest networks drop outbound UDP entirely. On such a network **every path
designed in §1–§3 dies at the same point**: the direct connection, the punch attempt, and the
connection to the relay all need a UDP packet to leave the building, and none does. A QUIC relay
cannot rescue this, because reaching the relay requires the blocked thing.

**Inbound versus outbound.** Inbound UDP blocking is the *common* condition — nearly every home
router refuses unsolicited inbound packets by default, which is the entire reason hole punching
exists (§1.3). That case is already handled. The case that defeats the design is **outbound**
blocking: with no outbound UDP you cannot open a mapping at all, so there is nothing to punch and
nowhere to punch it from.

### 9.2 The decision

**Build a TCP fallback transport on port 443.** Both peers dial outbound to a relay over ordinary
TLS on 443 — universally permitted, because it is indistinguishable from HTTPS at the network
level — and the relay pumps bytes between the two connections.

Rejected alternatives:

| Option | Why rejected |
|---|---|
| **Accept these networks as unsupported** — users sync when on a permissive network (home, phone hotspot) | Corporate networks matter to the product. Writing them off is a real capability loss, not a rounding error. |
| **Tor as the fallback** (`P2P-DESIGN.md` §12) | Blocked by policy on many of the same networks that block UDP. It does not solve the case we care about. |

### 9.3 Single stream, sequential

**The fallback path carries one stream, not multiplexed.**

Multiplexing mattered on the QUIC path so that a large back-catalogue sync could not head-of-line
block a live event push. On the fallback that concern is acceptable, because:

- This is the *degraded* path. Correctness matters; latency does not much.
- The existing push/sync split is already the escape hatch — push is the fast path, sync is the
  correct path. A push stuck behind a large transfer is picked up by the next sync regardless.

This collapses most of the implementation cost: one ordered byte stream, the §5.1 framing
unchanged, messages handled sequentially.

### 9.4 What TLS to the relay does and does not give you

TLS on 443 authenticates the *relay's hostname* and encrypts the leg between each peer and the
relay. It does **not** give end-to-end protection, because **the relay terminates both TLS legs and
therefore holds plaintext in the middle**.

That is unacceptable by construction: a relay is untrusted (§2.3 — any node may volunteer). So the
fallback path needs **its own encryption and its own peer authentication inside the pipe**. QUIC
was doing both for free; on this path we write them.

### 9.5 Volunteering as a fallback relay requires a DNS hostname

Ordinary nodes may volunteer as **full** relays — meaning QUIC relaying *and* TCP/443 fallback
relaying. But the fallback capability carries a requirement the QUIC path does not:

- **A real DNS hostname is required to advertise TCP fallback relay capability.** TLS on 443 needs
  a certificate valid for a hostname, and modern termination is SNI-based; IP-address certificates
  are effectively unavailable. A volunteered relay therefore advertises either a hostname (eligible
  for fallback relaying) or an IP address only (QUIC relaying only).
- Inbound 443 also needs a port forward, which most home routers do not have.

The QUIC relay path needs **no certificate at all** — peers verify each other directly (§1.5). The
hostname requirement exists solely because the fallback path impersonates HTTPS. Plain TCP without
TLS would sidestep the certificate but stops looking like HTTPS, and inspecting firewalls may kill
it — which defeats the entire purpose.

Consequence: volunteered fallback relays will be **rare and technically committed**. That is
acceptable, because of §9.6.

### 9.6 The seed relay is the directory, and filters by capability

Volunteered relays are learned through gossip — **and gossip runs over QUIC**. On a UDP-blocked
network you cannot learn about the very relays that would help you. So relay candidates come from
two places:

1. **The seed relay**, from config with an overridable default list (§3), always available, always
   TCP-capable.
2. **Cached volunteered relays**, learned earlier on a permissive network and written to disk.

The residual gap — a fresh install that has *never* had good connectivity, so has nothing cached —
is closed by the seed acting as a directory:

> **The seed tracks which of its known peers advertise TCP fallback capability, and preferentially
> hands those out during peer exchange when the requesting node arrived over TCP.** A node that
> reached the seed over the fallback path evidently needs TCP-capable options.

This is a **filter on the existing peer exchange message** (§5.2), not new machinery.

Note also that a *volunteered* relay on the QUIC path needs no DNS at all — it is just an address
learned through gossip.

### 9.7 Why the QUIC relay still exists

Worth stating, because it is easy to conclude the fallback subsumes it: **most relay cases are not
UDP being blocked.** They are addressing failures where UDP works perfectly.

Two nodes behind ordinary home routers both have working outbound UDP but cannot accept unsolicited
inbound. Punching fixes most of those. It fails against **symmetric NAT**, where the router assigns
a different external port per destination — so the port the peer was told about is useless and
there is nothing to punch. Symmetric NAT is common on mobile networks and some corporate setups.

So: **the QUIC relay serves working-UDP-but-unroutable peers, which is the common case. The TCP
fallback serves the rarer, harsher no-outbound-UDP case.** Two different failures, two different
mechanisms.

### 9.8 The identity and key-agreement exchange

Inside the relayed TCP pipe, two things must be established, and they combine into **one two-step
exchange**:

1. **The peer is genuinely who they claim** — a node certificate chaining to a known owner key.
2. **A shared secret the relay never sees** — ephemeral key agreement, as QUIC does internally
   (§1.6).

**Step 1 — lay the cards down.** Each side generates a throwaway keypair for key agreement, then
sends:

- its ephemeral public key,
- its node certificate,
- a fresh nonce.

Nothing is proven yet. At this moment a malicious relay *could* substitute its own ephemeral key in
each direction, establish a separate shared secret with each peer, and read everything while
relaying it on — the classic machine-in-the-middle.

**Step 2 — bind it.** Each side signs a hash of the transcript — **its own ephemeral public key,
the peer's nonce, and the peer's ephemeral public key** — with its **node private key**, and sends
the signature.

This defeats the substitution: the relay cannot produce a signature over its substituted key,
because it does not hold the peer's node private key. And the **nonce defeats replay** — a captured
signature from an earlier session will not match a fresh nonce.

**Verification is the same check as the QUIC hook (§1.5):** does the signature verify under the
peer's node public key, and does the peer's certificate chain to an owner key this node recognises?
Rejection is silent.

After step 2 both properties hold: the peer is authenticated, and the shared secret was never
visible to the relay. The §5.1 framing then runs inside that encrypted stream, single-stream and
sequential per §9.3.

### 9.9 Build implications

- A second wire *transport*, not a second wire *format* — §5.1 framing and the §5.2 messages are
  unchanged. Only the carrier and the handshake differ.
- The handshake in §9.8 is new code with no QUIC equivalent to lean on. It is the main cost of this
  decision.
- Relay capability advertisement gains a **TCP-capable flag**, and peer exchange gains the filter
  in §9.6.
- Fallback is **triggered by QUIC failure**, not configured: try QUIC, and on silence, fall back.
