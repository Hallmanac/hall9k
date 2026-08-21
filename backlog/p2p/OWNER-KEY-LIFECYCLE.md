# Owner key lifecycle — succession, recovery, and what the owner key actually does

**Slots into `P2P-DESIGN.md` as §4.1–§4.8**, under a retitled §4 ("Enrolment and the owner key
lifecycle"). Subsections rather than a new top-level section, so nothing after it renumbers.
Amendments to §10, §13, and §14 are at the bottom.

Session of 2026-08-18 (second half). Everything here follows from one observation: the owner
private key does far less work than it first appears to, and that changes what losing it costs.

---

## 4.1 What the owner private key does, exhaustively

**It signs node public keys. That is the entire job.**

It does not sign events. It does not join projects. It is not consulted on sync, on claims, on
gossip, or on any handshake. A node emits events signed by its *own* node key, with its
certificate riding along; peers verify two links (§2) and the owner key is nowhere in the
transaction.

Two consequences worth stating plainly, because both are load-bearing:

- **Every node holds the owner *public* key**, not just the primary. Public keys are meant to be
  everywhere. So **any** node can join a project on the owner's behalf — it presents the owner
  public key, proves it holds the invite token, and proves it is a certified node under that
  owner. The primary is special for exactly one operation: enrolling a machine.
- **Losing the owner private key is inconvenient, not fatal.** Every existing node keeps working
  indefinitely — claiming, syncing, signing, joining new projects. The single thing that breaks
  is enrolling a *new* machine.

That second point is what makes the rest of this section possible. There is no outage to detect
and no emergency to automate.

---

## 4.2 Two token types, deliberately distinct

Enrolment (§4) and project invitation look similar and are not the same operation.

| | **Node enrolment token** | **Project invite token** |
|---|---|---|
| Minted by | a node holding the owner private key | any node of any project member |
| Carried to | a new machine of the *same* owner | another human |
| Causes | the owner key to **sign a node public key** (a certificate) | an owner public key to be **added to a project's member list** |
| Requires the owner private key? | **Yes** | No |
| Scope granted | every project this owner is a member of | exactly one project |

Both are single-use, expire in ~1h, and are proven by an HMAC over the joiner's public key keyed
by the token secret (§4, and the joining message is node-signed as usual).

**Keep them as separate types on the wire.** A project token must never be able to enrol a
machine — that would silently convert "you may see this project" into "you may act as me
everywhere."

---

## 4.3 Joining a project, and what the inviter learns

Sarah does not need to know Brian's owner public key beforehand, and does not need to know it is
Brian at all. She trusts whoever holds the token.

1. Sarah mints a project invite token and sends it however (§4 — the channel needs integrity,
   not secrecy).
2. Brian runs `h9k project join` on **any** of his nodes.
3. That node finds Sarah's node (token addresses first, discovery as the safety net), handshakes,
   and sends: his **owner public key**, the HMAC proof over it, and his node certificate — all
   node-signed.
4. Sarah's node verifies, adds the owner public key to the project's member list, and returns the
   list plus project state.

The token's whole job is bootstrapping exactly one fact: *this owner public key belongs to the
person I meant to invite.*

---

## 4.4 Succession: the standing chain

**Decision. At enrolment, every node generates its own owner keypair in addition to its node
keypair, and the primary signs a succession statement: "owner key A also controls owner key B."**

The statement is stored on the enrolling node and travels as part of project state, so peers
already hold it before it is ever needed. Enrol five machines and you have five heirs, each
vouched for by the key that was healthy at the time.

This costs nothing extra to produce — the machines are already talking, the owner key is already
being used, and the second signature is one more call in a ceremony that happens a handful of
times per lifetime.

Recovery therefore becomes: **promote a node you still have.** No password manager, no paper
backup, no instructions to hand to a colleague.

### Rejected: copy the owner private key to every node

Defensible, and plenty of systems do it. Costs two real things. Every copy is another place the
root of trust can leak, and a leaked owner key can certify nodes as you *forever* (a leaked
enrolment token is dead in an hour). And it collapses per-node revocation: a laptop holding the
owner key **is** you, so losing it means rotating your whole identity rather than revoking one
certificate.

### Rejected: threshold signatures (2-of-3 multisig)

The right instinct — it does solve the single-point-of-failure directly, and splitting the key so
no machine holds it whole is genuinely appealing. Two snags. It is real cryptographic engineering
(threshold Ed25519, or a Shamir split with a signing ceremony) and the .NET library support is
thin. And it requires two machines online *simultaneously* to enrol, trading a rare inconvenience
for a routine one. Disproportionate for a key that signs perhaps five things in its life. Worth
revisiting only if the trust root ever needs to survive an adversary rather than a dead SSD.

### Rejected as the *only* answer: manual backup

Exporting the key to a password manager is sensible and should remain possible. It is not
adequate as the design, because it pushes ceremony onto every user of the tool. Hall9k manages
code, not money; nobody is going to stamp a seed phrase into steel for this.

---

## 4.5 Promotion is deliberate, and discovers itself at the right moment

Because nothing is broken (§4.1), there is **no failover to automate**: no health checks, no
elections, no quorum, no split-brain machinery. Promotion is a human act, invoked the day it is
actually needed — which is the day you try to enrol a machine.

The flow falls out naturally:

1. On a surviving node, `h9k node invite`.
2. The node looks for a reachable node holding the owner private key. Finds none.
3. It says so plainly: *no owner key available; this node can be promoted — proceed?*
4. On yes: it presents its succession statement (already signed by the old key), announces the
   promotion to peers, and mints the token.
5. From then on it is the primary, and the remaining nodes re-enrol against it in the ordinary
   way — new tokens, new certificates. Nothing special; it is onboarding again.

**The prompt matters.** Promotion changes the identity root across the network and must not
happen silently as a side effect of wanting a new laptop.

### Ordering: oldest reachable node

**Oldest surviving node is the default successor.** It needs no extra state — every node knows
its own enrolment date, and those dates are signed by the primary, so the ordering is *verifiable
rather than declared*.

It is a suggested default, not an automatic rule, precisely because the oldest node may be the
one that is offline. Skip down to the oldest **reachable** node; the promoted node announces
itself so the others stand down.

---

## 4.6 If there is no chain and no surviving node

The floor, and it is not bad: **a project member re-invites you.** Sarah mints an ordinary project
token, you join with a brand-new owner key, and you are working again in one Slack message.

The cost is honest and worth recording: your prior work is attributed to an owner key nobody
vouches for anymore. The history is intact; the authorship is orphaned. A pre-signed succession
statement (§4.4) is exactly what prevents that, which is the argument for minting them by default.

For a solo project with nobody to vouch for you, an exported key file in your normal backups is
proportionate. That is the one case where manual backup earns its keep.

---

## 4.7 Certificates on the wire vs. certificates in the store

The rule: **a certificate travels with anything that will be repeated by someone else.**

- **Transient messages** — queries, heartbeats, "do you carry project X" — carry a signature only.
  Identity was settled once in the handshake, and the message dies at the receiving node.
- **Stored events** carry signature *and* certificate as part of their persisted form, because
  they outlive the connection. Sarah's node relays your event to a third peer that has never
  handshaken with your laptop, and that peer must verify independently rather than take Sarah's
  word for it.

**Do not build this yet.** There is nothing to relay to, and it would be dead weight in every
aggregate today. What matters now is not foreclosing it: leave room for a signed envelope
*around* an event, rather than assuming an event is only ever the thing your own node wrote.

---

## 4.8 Events written before P2P existed

Pre-P2P events are unsigned. The tempting answer — "they never leave this machine, so nobody
needs to verify them" — is **wrong**, and this was caught in session: history propagates. Sarah
syncs your back catalogue, then relays it onward to a node that will ask, reasonably, who signed
this.

So a one-off migration is needed at the moment P2P is switched on. Two candidates:

- **Retroactive signing** — walk the log and sign each event. Honest enough: you are attesting
  *now* to what you wrote *then*.
- **A checkpoint** — hash-chain the log (each event commits to the previous event's hash) and
  sign the tip once. One signature vouches for the entire history, because altering event forty
  breaks every hash after it. Bitcoin's block chaining, in miniature.

Either is cheap and neither is owed today. It is a switch-on task, not a tax carried in v0.

---

# Amendments to existing sections

## §10 (Sync semantics) — add

**Same-owner sync relaxes the lazy-payload rule.** Eager metadata / lazy payloads was reasoned
about with *cross-owner* peers in mind. Between two machines of the same owner on a fast link,
pulling attachments is essentially free, and there is real value in the laptop being a genuine
mirror rather than a thin index. Rule: **same owner on a fast link pulls everything; anything
else waits for a claim.** And a newly enrolled node **syncs all of that owner's projects by
default** — the certificate is owner-scoped, so which projects sync is a preference, not a trust
question.

## §13 (Open questions) — add

5. **Competing promotions.** Two nodes promoted independently while partitioned from each other,
   each holding a valid succession statement. Peers must accept only one. Earliest promotion
   wins? Last writer loses? Human resolution? Unresolved — and rare enough to leave unresolved.
6. **Retroactive signing vs. checkpoint** for pre-P2P events (§4.8). Decide at switch-on.
7. **Succession statement distribution.** They travel with project state, but an owner's *first*
   node before any project exists has nowhere to put them. Probably local-only until the first
   sync; confirm.

## §14 (Build order) — amend slice 1

1. **Keys and enrolment** — owner/node keypairs on the existing aggregates, certificate chain,
   enrolment token, signed events. **Plus: succession statements minted during enrolment, and
   `h9k node invite` / `h9k owner promote` with the promotion prompt** (§4.4–§4.5). Pure local
   work; no network. The succession chain is cheapest to build here and expensive to retrofit,
   since it wants to be signed at enrolment time by a key that is still alive.

---

# Wire protocol — opened, not designed

Recorded so the next session starts from something. This remains §13 open question 3.

**Two interaction styles, kept distinct rather than unified:** request-response ("do you carry
project X", "send me events after sequence N") and push ("here is a new event, pass it on").
Protocols get messy by pretending these are one thing and then bolting on workarounds.

**Frame:** every message carries a **type**, a **length prefix**, and — for requests — a
**correlation ID**, so responses arriving out of order can be matched to their questions.

**Type list** (the catalogue is the actual specification work): handshake, join request, project
query, event push, event range request, peer exchange. Unknown types are ignored, so adding a
capability later does not break old nodes.

**Handshake, ordered so an unauthorised peer is rejected before any expensive work:** certificate
first, then proof of the matching private key (sign a random challenge from the other side), then
protocol version, then which projects each carries or asks about. QUIC supplies the transport
half of this — mutual verification, then a derived symmetric session key neither side transmitted
— so the work is plugging node certificates into its verification hook, not implementing it.

Next session: walk the handshake exchange message by message.

---
---

# 4.9 Partitions, competing promotions, and reconciliation

Added after working the failure modes through properly. **This closes §13 open question 5**, which
the first draft left open on the grounds that the race was too rare to bother with. It is rarer
than a coin toss and more likely than it first sounds, and the resolution turned out to be cheap.

## 4.9.1 The easy case: a latecomer

The common shape is not a race at all. The primary is a laptop, the lid closes, and while it
sleeps a secondary is asked to enrol a machine, fails to reach it, and promotes.

When the laptop wakes it hears the promotion announcement, and **it can verify that statement
because it signed it itself**. So it steps down: archives its owner private key and carries on as
an ordinary node. No ambiguity — the promotion happened, the old primary simply hadn't heard.

**Promotion events form a chain**, each one naming the key it supersedes. A node that was offline
across *several* promotions replays them in order and lands on the current primary. Step-down
logic must therefore handle a chain, not a single hop.

## 4.9.2 The hard case, and its actual generator

Two nodes promote independently, neither aware of the other, both cryptographically valid.

The first instinct is a quorum rule — refuse to promote unless you can reach some number of your
other nodes, on the grounds that failing to reach the *primary* is weaker evidence than failing
to reach *everyone*. **That does not work**, and finding out why located the real generator:

> Location two loses its internet uplink. Its local network is perfectly healthy — three of the
> owner's nodes talking happily to each other. From inside that bubble it looks exactly like an
> ordinary dead primary. A quorum is available, is consulted, and agrees.

That is the standard nastiness of partitions: **both halves look correct from the inside.** A
quorum rule protects against a *lonely* node, not a split, and the split is the case that matters.

**Rejected: detecting the condition.** "Don't promote while the uplink is down" requires knowing
your view is incomplete, which is a liveness oracle and does not exist. The uplink is only one of
many reasons two locations stop seeing each other, and none of them are reliably distinguishable
from a genuinely dead primary. Reconcile after the fact instead.

## 4.9.3 The reconciliation rule

**Both promotions are hashed; the lower hash wins.** Both sides compute both hashes independently
from data that never changes, so they reach the same answer with no negotiation and — importantly
— **no trusted clock**, wall-clock time being exactly what a partition makes unreliable.

This is CAN-bus arbitration, the same trick raised earlier for the claim race (§13 open question
2): an arbitrary but deterministic priority that losers detect for themselves rather than being
told. The difference is timing — CAN resolves in microseconds *during* transmission, whereas this
resolves days later, after both sides have already acted as winners. Which is why the aftermath
below is the substantial part.

*Node ID alone would be equally deterministic and simpler, since IDs are already globally unique.
Hashing is preferred because it is unpredictable: nobody can choose an ID to guarantee winning.
That matters little when every owner is you, and costs nothing.*

## 4.9.4 The aftermath

Both keys were live for days. Each certified nodes; those nodes signed real events.

- **Nodes are easy.** The loser's certified nodes re-enrol under the winner. New certificates,
  nothing lost.
- **Events are the question**, and the rule is: **a certificate is valid for what it signed while
  it was valid.** The losing key stops certifying anything new, but events already signed under it
  remain verifiable forever. Rejecting them would discard legitimate work; treating the key as
  never-having-been-valid would be a lie about what happened.
- Consequently **the losing promotion event is retained as evidence, not deleted.** A peer
  verifying an old event needs to be able to establish that the certifying key was legitimate at
  the time.

**This is not a chain split**, despite the resemblance. In a blockchain reorg the two histories
*compete* — the same coins, contradictorily spent — so one must be erased. Here they mostly do
not: two machines certifying two laptops doing two unrelated pieces of work are not in conflict,
they merely happen to have been vouched for by keys that later disagreed about seniority. Both
histories are kept and merged. No reorg, nothing orphaned.

## 4.9.5 The one genuine conflict: the same task claimed twice

Which is a **claim-race problem, not a key problem** — a different layer, and one that needs
solving regardless of any of this.

**GitHub arbitrates.** A remote branch or PR is a fact both sides can check, and creating one is
atomic. Whoever's push landed wins. This is the external-source-of-truth decision paying off
rather than new machinery.

Two residual shapes:

- **Both pushed successfully.** Two branches, two PRs, one task. Not broken — a human picks.
  Wasteful, not incorrect.
- **A task that never reaches a PR** — a spike, an investigation, discovery work that produces a
  transcript and a conclusion. Nothing lands on GitHub, so nothing arbitrates.

For the second, **the system's job is to flag, not to resolve.** On heal, notice a duplicate claim
on a payload-only task and surface it as needing attention. Do not pick, do not merge, do not
discard. Two conclusions to the same question are two opinions, and only a human can say whether
they agree, complement each other, or one is simply better — plausibly with a Claude Code session
reading both and reporting where they diverge, the same shape as the pre-PR review loop.

**Net: the claim race is a cost problem, not a correctness one.** Anything producing code resolves
itself through GitHub; anything that does not produce code does little harm duplicated.

## 4.9.6 The inverse: zero winners

Every node holding a succession statement gone or unreachable, and nothing to promote. Already
covered — that is the re-invite floor (§4.6).

---

# Further amendments

## §10 (Sync semantics) — repo materialisation

**Confirmed shape**: the **event log syncs eagerly, everywhere** (projects, tasks, claims, runs,
discovery metadata — Postgres rows, megabytes not gigabytes). **File payloads sync lazily.**
**Git repositories clone lazily**, triggered by the same claim.

**A gap this exposes**: `h9k` currently assumes the repository is already present, because you are
standing in it. Cross-node claiming breaks that — a machine can hold the complete event log for a
project it has never cloned. So there is a **materialisation step ahead of work-tree creation**:
do I have this repo? No → clone it from the project's GitHub connection → create the work tree as
normal. Pleasingly, this is the same lazy-materialisation shape as attachments, with git doing the
fetching.

Note `init` is never lazy and never needs to be: when a project is created the repository already
exists on that machine. **Only cloning is ever lazy.**

## §13 (Open questions) — amend

**Question 5 (competing promotions) is closed** by §4.9. Replace with:

5. ~~Competing promotions~~ — **resolved**: reconcile after the fact by lowest promotion-event
   hash; losing certificates remain valid for what they signed; duplicate task claims arbitrate
   through GitHub, and payload-only duplicates surface for a human (§4.9).

Add:

8. **Duplicate-claim surfacing on heal** — what the notification looks like, and where it lives in
   `h9k` output. Related to open question 1 (awaiting materialisation) and probably shares a
   mechanism.

---
---

# 4.10 What a node owes you about work that lost

§4.9 settled *who has authority* after a partition. This section settles the part anyone actually
notices: **what happens to the work**. It also **answers §13 open question 8** (duplicate-claim
surfacing) rather than leaving it to be designed later.

## 4.10.1 There is no "primary" in the user's world

A correction to the framing used above. The first draft assumed the losing side needs telling that
it is no longer primary — a status message on an ordinary day.

**It doesn't, because nobody walks up to a machine caring what kind of node it is.** They walk up
and start working. "Primary" is an implementation word that describes exactly one capability:
holding the owner private key, used the handful of times a new machine is enrolled. On every other
day the machines are interchangeable.

So the key reconciliation of §4.9 is **silent plumbing**. If a node lost, it finds out at the one
moment it matters — `h9k node invite` fails, and the message is *use the other machine, or promote
this one*. No status to track, no notification, nothing to surface.

**The reconciliation that ever speaks is the one about work**, and it is per-node: it must surface
where the human is standing, not where the winning node happens to be.

## 4.10.2 Losing runs are attached, not merged

The instinct is that a losing run's events need somehow folding into the winner's — replayed after
them, or interleaved into one true sequence. **Neither.**

A task has one identity and now has **two runs against it**. Both are stored as they arrived, each
keeping its own identity: this run, on this node, at this time. Nothing is rewritten, nothing is
re-ordered, nothing is discarded.

This works because **ordering across nodes was never a single true sequence.** Each run is
internally ordered; the two runs are concurrent; the physical order in the transaction log is just
arrival order and is an implementation detail. Distinct stream IDs mean there is no collision to
resolve — the partial order is admitted rather than flattened, and the read model reports "two
attempts" instead of pretending to one narrative.

**Consequence, and it is a large one: the storage layer needs no partition-specific machinery at
all.** The only place the ambiguity was ever real is the **lease** — two nodes each believing they
held the right to work — and that is precisely what the §4.9 hash tiebreak settles. So:

> **The tiebreak decides who had authority. The events record what happened either way.**

Nothing gets combined at the data level. What gets combined is the **conclusion**, and that is a
**third artifact** written by whoever reviews both — an appended interpretation of history, never
an edit to it. Which is the event-sourcing discipline applied to its own operational mess.

## 4.10.3 The winner proceeds; the loser is flagged, not blocked

The winning run stands as the outcome — its branch is the branch, its PR is the PR, nothing to
undo. The task closes through the normal path.

The losing run is **flagged on the task as unreviewed**. Non-blocking. Whether it is worth reading
depends on where the task got to: if the winner is already merged, the other attempt is optional
curiosity; if the task is still open, the other attempt may well be better, and shipping the
winner *purely because its push landed first* is arbitrary.

Explicitly: **nothing is deleted.** The work stays on disk, the transcript stays, the diff stays.
A losing run is a second attempt someone might want to read, not a duplicate to clean up.

## 4.10.4 The agent triages [and this is Hall9k reconciling itself]

The human does not have to be at that terminal, because the whole point of the product is that
agents absorb this class of work.

**A losing claim materialises a review task**, carrying both attempts as its payload. An agent
picks it up through the ordinary queue, reads both runs, and reports where they diverge.

Most of the time the honest answer is *they converged* — two agents given the same task and the
same codebase reach near-identical results more often than not, differing in nuance rather than
outcome. In that case the agent says so, the winner stands, and the matter closes without a human
reading two transcripts to learn nothing. **That is the common case, and absorbing it is most of
the value.**

**Escalate only genuine divergence.** When the two attempts actually differ in approach or
conclusion, that is a real judgement call and it goes to the human — and it is rare enough to be
cheap.

**The agent recommends; it does not decide.** Discarding your own work is exactly the class of
action that wants a human nod, and asking costs nothing. This sits at the edge of the
supervised-autonomy model rather than past it.

*Note this is the same shape as the pre-PR independent review loop already in use (decisions
#20–#24) — an agent reading work it did not do and reporting rather than acting. Not new
machinery; an existing pattern pointed at a new input.*

## 4.10.5 §13 (Open questions) — amend

**Question 8 (duplicate-claim surfacing) is closed** by §4.10: a losing claim materialises a review
task carrying both runs, an agent triages it, and only genuine divergence reaches a human. Both
runs are retained; the winner is never blocked.

Remaining shape, small: the wording `h9k node invite` uses when it discovers this node's owner key
lost a reconciliation.
