# Hall9k - Agentic Workflow Platform - Planning Document

**Status:** Initial plan, output of exploration/discovery conversation (Aug 2026). Living document - refine in Claude Code as build proceeds.
**Owner:** Brian Hall ("Hallmanac") - solo builder, v0 user count = 1
**Name:** **Hall9k** - Hall (owner's name / "Hallmanac") + HAL 9000. The wink is the thesis: HAL is the AI that escaped supervision; Hall9k exists to keep the human in the loop. Conventions: repo/org/CLI `hall9k`, namespaces `Hall9k.*` (e.g. `Hall9k.Cli`, `Hall9k.Daemon`, `Hall9k.Domain`).
**Purpose:** Define what we're building, why, the architecture, the domain model, and a build plan. This doc seeds the Claude Code project (`PLAN.md` / basis for `CLAUDE.md`).

---

## 1. Vision & Goal

Move from **Level 2 (Parallel - human orchestrates ~10 agents manually)** to **Level 3 (Supervised Autonomy - agents run semi-autonomously, human supervises at checkpoints)** per Anthropic's "Steps of AI Adoption" (Boris Cherny, Jul 2026).

The Step 2 → 3 recipe from that doc is effectively our build spec:

1. Let Claude pull its own context (agents read code/wikis/docs themselves)
2. Break work into loops and routines (repeatable named workflows, not ad-hoc prompts)
3. Let Claude kick off Claude (orchestration loop; one agent dispatches others)
4. Trust in the loop (automated verification/review so the human stops reading everything)

**#4 is the real bottleneck at Level 3** - the dispatcher is the easy part; the verification pipeline that lets the human *not look* is where a homegrown build earns its keep, because trust gets calibrated to our own standards.

### Deployment philosophy: local-first, P2P later, portal maybe

**Hall9k runs locally.** It is a CLI app on the PATH plus a daemon running in the background on the user's machine. There is no cloud host.

- **Now (v0):** one machine, one node: `h9k` CLI + `h9kd` daemon + local Postgres (container).
- **Later:** a **decentralized network of nodes** - each machine runs its own daemon + Postgres; nodes coordinate with each other so work shards across machines (possibly working the same projects).
- **Maybe eventually:** a web portal as a nicer remote window into the nodes - manage tasks away from the machines. Far down the priority list; nothing in the core design depends on it.

### What this platform is

An **orchestration brain over external systems**, running on your own machines:

- **Work items** (tasks, epics) live in **Jira or GitHub** - they are the source of truth once work is published.
- **Code and PRs** live in **GitHub** (always).
- **Agents** (AI coding sessions) run locally, spawned and supervised by the node's daemon.
- The platform itself owns: the **funnel** (ideas → triage → discovery → publish), **orchestration state**, **connector configs**, **run logs/learnings**, and the **attention surface** (what's awaiting the human).

### What this platform is not

- Not a kanban board / work tracker (Jira & GitHub already do that).
- Not an agent runtime (Claude Code / Agent SDK already do that).
- Not a hosted web app / SaaS. Local-first is the identity, not a stopgap.

---

## 2. Core Concepts & Vocabulary

| Term | Meaning |
|---|---|
| **Idea** | Anything entering the funnel: bug report, feature thought, big initiative. Deliberately unstructured at capture. Lives on the platform, never in Jira/GitHub. |
| **Inbox** | Node-level queue of new ideas awaiting triage. |
| **Triage** | Decision gate: pursue now / park / kill. Parked ideas keep history; a `resurfaced_count` gives recurring ideas weight over time. |
| **Discovery** | Interactive process turning a pursued idea into implementable work: an interview-style agent session that drafts scope, breakdown, and acceptance criteria. Includes what other shops call "refinement" - exit criterion is "tasks are dispatch-ready." |
| **Refinement** | The *backward* loop: implementation discovered the work was mis-shaped ("this task is really 3 tasks"), so it goes back for re-discovery. Same muscle as discovery, different trigger. |
| **Publish** | One-way handoff: discovery output is written to Jira/GitHub as work items. From that moment, the external system is the source of truth. No two-way content sync, ever. |
| **Adopt** | The reverse of publish: link a task to an *existing* external work item (§3.1a). |
| **Parking garage** | Where parked/killed ideas live, with full history and resurfacing counts. Never touches Jira/GitHub. |
| **Epic** | Optional grouping created only when discovery yields more than one task. Problem scope + objectives + desired outcomes. Never forced - single-card work stands alone. |
| **Task** | The unit of work. Always maps to exactly one Jira card or GitHub issue. Must satisfy the readiness contract (§4) before dispatch. |
| **Project** | A configured workspace: which connections, which repo(s), which Jira project or GitHub issue tracker, its own context (CLAUDE.md), agents' memory. Projects can be *born from the funnel* (triage outcome: "this idea becomes a new project"). |
| **Node** | One machine running Hall9k: `h9kd` daemon + Postgres + spawned agents. v0 = exactly one node; the P2P future is many nodes coordinating. |
| **Daemon (`h9kd`)** | The resident orchestrator process on a node: dispatch loop, run monitoring, lease management, verification orchestration. |
| **CLI (`h9k`)** | Execute-and-exit control surface on the PATH. Used by humans, scripts, *and headless agents*. Thin - logic lives in Domain/daemon. |
| **Agent** | An AI session (Claude Code headless, later possibly Codex/Kimi) spawned by the daemon to do work. |
| **Executor** | The specific AI CLI/SDK the daemon uses to spawn agents (Claude Code = the only v0 executor). |
| **Orchestrator window** | An interactive Claude Code session acting as the conversational UI, driving `h9k`. Stateless and disposable - all state is in Postgres. |

---

## 3. The Lifecycle

### 3.1 Funnel (lives on the platform)

```
Idea (inbox) → Triage ─┬→ Pursue → Discovery → Publish → [work items in Jira/GitHub]
                       ├→ Park   (→ parking garage; resurfacing tracked)
                       └→ Kill   (→ parking garage, as record)
```

- Ideas are captured with near-zero friction: freeform text + optional attachments/links + optional project assignment. Default state: `triage`.
- Triage can also spawn a **new project** from an idea.
- Discovery is conversational (see §7). Output: an approved breakdown (epic + tasks, or a single task).
- Publish writes to the external system via the connector, records external refs, and links back to the discovery record (**origin link** - implementing agents can pull the full discovery context: conversation, alternatives considered, rejected approaches).
- The **parking garage lives on the platform** - killed/deferred ideas never touch Jira/GitHub.
- **Open question (local-first ingestion):** how ideas get captured when away from the machine is unresolved - see §8.

### 3.1a Adopting existing external work (the reverse funnel exit)

The platform practices **selective adoption, never mirroring**: it tracks only work the user (or their agents) will actually take on. External backlogs are never bulk-synced - critical for the "existing multi-developer project, I own a subset" use case. A 400-card Jira project yields, say, 6 adopted tasks.

Adopted items run through the **same funnel with a different exit**: publish and adopt are the same operation with direction reversed.

```
Point at PROJ-123 / repo#42 → Idea (pre-linked to external ref) → Triage ("am I taking this on?")
  → Discovery → LINK (task created against the EXISTING item; nothing new created externally)
```

Discovery still earns its keep on well-fleshed cards: a card can be *human-ready but not agent-ready*. For adopted items, discovery's output is the platform-side agent context (§4.1) - readiness-contract check, acceptance-criteria extraction, constraints. It's just much faster than greenfield discovery.

The connector interface (§9.2) must support both exits: create (publish) and link (adopt).

**Project onboarding exploration:** when connecting an existing project, an initial discovery-flavored session surveys the repo/board - what is this, current state, greenfield vs. brownfield - and produces the project's context doc (feeds CLAUDE.md / project defaults, and informs which items are worth adopting). In v0 this is simply "human + Claude Code look around the repo together."

### 3.2 Execution (source of truth is external)

```
Task (Jira/GitHub) → Dispatch → Execute → Verify → Checkpoint → Complete
                        ↑                              │
                        └── Refinement ←── needs-human / mis-shaped work
```

1. **Dispatch** - the daemon claims a ready task (lease), prepares an isolated workspace (git worktree), assembles context (task + discovery record + repo + CLAUDE.md), spawns a detached agent session.
2. **Execute** - agent works; the daemon captures stream-json events into the run's event stream.
3. **Verify** - automated gates: build, tests, lint, security scan must pass; then a **separate reviewer agent session** (never the implementer's session) critiques the diff.
4. **Checkpoint** - human approval where required. The human gate for code **is PR review in GitHub** - no custom review UI needed.
5. **Complete** - merge/deliver, transition the external work item, record run artifacts + learnings.

Escape hatch state: **`needs-human`** - agents park a task there with a question instead of guessing. Mis-shaped tasks route to **refinement**.

### 3.3 Task lifecycle states (orchestration metadata, platform-side)

`queued → dispatched → in-progress → verifying → awaiting-review → done | failed | needs-human | needs-refinement`

The external system carries the *human-facing* status (via mapped transitions); the platform carries orchestration state keyed by external ref.

---

## 4. The Task Readiness Contract

A task cannot be dispatched until it satisfies this contract - "everything an agent needs to succeed without the human present":

1. **Objective** - one sentence, outcome-phrased. ("Add rate limiting to auth endpoints," not "look into rate limiting.")
2. **Context pointers** - where to find what's needed: repo/branch, docs, related task IDs, the discovery record. Pointers, not pasted content - the agent pulls context itself. This is also how **sibling/epic awareness** works for adopted work (§3.1a): sibling cards are never imported as tasks; the agent context points at them ("epic: PROJ-100; siblings: PROJ-121, PROJ-124 - read for boundaries, don't touch their scope") and the agent fetches them live at run time. No import, no staleness, no sync.
3. **Acceptance criteria** - explicit, checkable conditions. **The single highest-leverage field**: it's what verification gates and the reviewer agent test against. If you can't write acceptance criteria, the task isn't ready.
4. **Constraints/guardrails** - what not to touch, allowed tools, budget (max turns/tokens/time).
5. **Task type** - feature / bugfix / refactor / chore / research. Drives persona, prompt template, and verification profile.

Discovery's job is producing tasks that meet this contract. The **readiness gate** is enforced at publish time - nothing reaches the backlog unqualified.

**Tasks produce artifacts, not just diffs**: every run emits a summary, decisions made, and open questions. That's the human's review surface and the memory for future tasks.

### 4.1 The Task entity (platform domain model)

**Task is a first-class platform entity.** The GitHub issue / Jira card is one *linked property* of it - the source of truth for story *content* (title, description, human-facing status), never duplicated platform-side. Everything operational hangs off the platform Task:

| Facet | Contents |
|---|---|
| External ref | Link to the GitHub issue / Jira card (content source of truth) |
| Agent context | The verbose, prescriptive agent-facing material: instructions, context pointers, constraints, verification profile. Deliberately *not* in the issue body - too unwieldy for humans; the issue stays user-story-readable for both humans and agents, the Task carries the agent-only depth. |
| Origin | Link to the discovery record (conversation, alternatives, rejected approaches) |
| Execution | Node binding (project-level default, per-task override), run history, session IDs, agent event stream |
| Deliverables | Linked PR(s) as they're created |
| Economics | Token usage, cost, budget (per §6.4) |
| Conversation | The agent ↔ human message thread (questions, answers, check-ins) |
| Orchestration state | `queued → dispatched → in-progress → verifying → awaiting-review → done \| failed \| needs-human \| needs-refinement` |

Rule of thumb: **content → external system; everything operational → Task entity.**

Being event-sourced, most of this is a projection of the Task's event stream (`RunDispatched`, `RunEventReceived`, `QuestionAsked`, `AnswerProvided`, `PROpened`, `TokensRecorded`, ...) - `h9k task show` and the orchestrator window render straight off it (§12).

**Flagged modeling decision (v0):** an idea does not *mutate into* an epic or task. Discovery **produces** task(s) - plus an epic grouping only when there's more than one - and the idea is archived with origin links both ways. Cleaner event-sourced than entity mutation; confirm when modeling the streams.

---

## 5. Autonomy Dials (the migration path)

Every lifecycle stage is a *role* with an independent autonomy level:

- **Level A** - human does it
- **Level B** - agent drafts, human decides (agent pre-triages with a recommendation; agent drafts the epic breakdown; human approves)
- **Level C** - agent does it, human audits by exception

**We don't automate the pipeline; we automate stages one at a time, in order of trust earned.** Expected order: implementation first (most verifiable) → discovery breakdown → triage last (spend/priority judgment stays human longest). This per-stage dial *is* the Level 2 → Level 3 path.

---

## 6. Architecture

### 6.1 Node topology (one machine = one node)

```
┌────────────────────────── ONE NODE (user machine) ──────────────────────────┐
│                                                                              │
│  Orchestrator window                 h9k CLI (PATH, execute-and-exit)         │
│  (interactive Claude Code            [Hall9k.Cli - Spectre.Console.Cli]      │
│   session; conversational UI) ─────► used by human, scripts, AND agents      │
│                                          │                                   │
│                                          ▼ writes commands/queries           │
│  ┌──────────────────────┐      ┌───────────────────────┐                     │
│  │ Postgres (container) │◄────►│ h9kd daemon             │                     │
│  │ Marten + Wolverine   │ bus  │ [Hall9k.Daemon]        │                     │
│  │ event streams        │      │ native host process    │                     │
│  └──────────────────────┘      │ (NOT containerized)    │                     │
│        the DB IS the bus       │ • dispatch loop        │                     │
│        (LISTEN/NOTIFY)         │ • lease mgmt           │                     │
│                                │ • run monitoring       │                     │
│                                │ • verification orch.   │                     │
│                                └───────────┬───────────┘                     │
│                                            │ spawns (detached)               │
│                                            ▼                                 │
│                      headless agents: claude -p --bare (stream-json)         │
│                      outlive any terminal; report + ask via h9k               │
└──────────────────────────────────────────────────────────────────────────────┘

External systems (reached from the node via gh CLI / provider APIs):
  GitHub - repos, PRs, issues (connector)
  Jira   - cards/epics (connector, later)

Future: many nodes, each shaped like this, coordinating P2P (§14).
```

**Why the daemon is NOT in Docker:** it must spawn `claude -p` on the host - needing the host's Claude subscription login (`~/.claude`), git/`gh` credentials, and real repo/worktree paths. Containerizing means mounting home dirs + credential and path translation pain (brutal on Windows) and likely breaks subscription auth. Postgres containerizes cleanly; the daemon runs native: launchd LaunchAgent (Mac, per-user so it sees your creds), Task Scheduler startup entry or service (Windows - decide at build time; a true Windows Service runs as a different identity by default, same credential problem), systemd user unit (Linux). *(Superseded 2026-08-19, Decisions Log #31: service registration is opt-in — `h9k install` registers nothing; the daemon lifecycle is CLI-owned via `h9k daemon start/stop`, and the per-OS agent above is what `h9k daemon autostart enable` registers.)*

**CLI ↔ daemon communication: the database IS the bus.** CLI writes commands/tasks to Postgres; daemon reacts via Postgres LISTEN/NOTIFY (Wolverine supports Postgres-backed messaging natively). No sockets, no local HTTP API.

### 6.2 Node identity, ownership & leases (the P2P down-payment)

**The accountability principle: every node belongs to a human.** Not to an agent, not to a service - a person. Whatever autonomy agents gain, a human is ultimately responsible for every run their nodes perform. The P2P future is multi-*user*: a team project splits work across developers, each developer runs their own node(s) - Brian's nodes, Sarah's nodes - like each brings their own IDE. Cross-user node communication is a later capability the groundwork must not foreclose.

Groundwork rules (paid in v0; all cheap):

- **Owner is a first-class entity**, not an assumption. A node record carries `owner_id`; an owner record exists even when there's exactly one (Brian, created at `h9kd install`). Never "the user" implied by context.
- **Every run records node + owner.** The accountability chain is queryable: this PR ← this run ← this node ← this human.
- **Globally unique IDs everywhere** (owners, nodes, ideas, tasks, runs - UUIDs/similar): records born on different machines by different people must be able to coexist in one merged/synced view without collision. No auto-increment identity anywhere in the domain.
- **No single-owner assumptions baked into streams or queries**: event streams and projections are written as if multiple owners' data could share a store, because someday it might (shared Postgres between teammates is the likely first multi-user topology).
- Task claiming is **lease-based**: claim (by node, therefore by owner) + heartbeat + timeout requeue. Works identically for 1 node or 20, one owner or a team.

That is the *entire* multi-node/multi-user tax paid now. Explicitly deferred: node discovery, gossip/replication, partition handling, and the cross-user trust/auth question (how does Brian's node verify Sarah's node?). When multi-machine arrives, evaluate "several daemons against shared/replicated Postgres" before anything exotic.

### 6.3 Executor abstraction

`IExecutor` seam in the daemon: `Spawn(taskContext) → event stream → result`. v0 implements Claude Code headless only:

- `claude -p --bare` with `--output-format stream-json`, `--allowedTools`, `--permission-mode`, budget flags.
- Spawned **detached** - agents outlive the terminal/session that requested them. Daemon records PID + session ID, monitors via the run record.
- Session IDs captured for resume (the `h9k ask`/`h9k answer` loop depends on this).
- **Do not use Claude Code subagents as workers** - they run inside a parent session, flood its context, and serialize on it. Detached processes with the DB as the coordination bus is the shape.
- **Known limitation**: fire-and-forget per invocation - no mid-run approval callbacks. When that hurts ("I want to intercept *this specific action*"), the upgrade path is a thin **TypeScript sidecar wrapping the Claude Agent SDK** (`canUseTool` callbacks, hooks) that the daemon talks to. Design the seam so this is a swap, not a rewrite.
- Reference: Agent SDK exists in **TypeScript and Python only**; the C# `Anthropic` NuGet package is the raw **Messages API client** (no agent loop) - not a substitute.

### 6.4 Billing posture (design principle: auth mode is config, not architecture)

Current state (verified Aug 2026):

- **Raw Messages API** (incl. the C# `Anthropic` NuGet): always API key, always pay-per-token. Never subscription.
- **Headless CLI (`claude -p`) and Agent SDK**: follow whatever auth Claude Code uses. Logged in on a subscription → draws from the subscription pool (same weekly limits as interactive). Setting `ANTHROPIC_API_KEY` overrides the subscription entirely → pay-as-you-go API billing.
- **The flux**: Anthropic announced (May 2026) a split moving all *programmatic* usage (`claude -p`, Agent SDK, GitHub Actions, third-party agents) to a separate monthly "agent credit" pool billed at API rates; the change was **paused before its June 15 effective date** and is not live, but Anthropic has signaled a revised version may return with advance notice.

Design consequences for Hall9k:

1. **Build headless anyway.** Interactive-terminal spawning to stay on the subscription side of the line is a trap: no `stream-json`, no clean completion signal, screen-scraping fragility - and the "interactive = subscription" line is a proxy Anthropic can (and likely will) redraw around *behavior*. Don't architect around a definition in flux; gaming it also risks violating usage-policy spirit.
2. **Auth/billing mode is per-node (or per-project) config**: `subscription` (default; daemon uses the machine's Claude login) vs. `api-key` (daemon injects `ANTHROPIC_API_KEY` into executor env). A future split becomes a config flip + budget decision, not a rearchitecture.
3. **Track cost per run from the first run**: headless JSON output includes token counts; persist per-run tokens/cost on the run record, roll up per task, per project, per billing mode. This is also what makes the "is the credit pool enough?" decision answerable if the split returns.
4. **Practical concurrency guidance**: 1–3 steady agents on a Max plan → subscription is cheapest; 5+ parallel → subscription rate limits bite fast, API key is for burst. Mixed mode (subscription primary, API key overflow) is the expected steady state.

### 6.5 Verification pipeline

Per task type, a **verification profile**:

1. Deterministic gates: build, tests, lint, typecheck, security scan - configured per project.
2. **Reviewer agent**: a fresh agent session (separate identity/persona from the implementer) critiques the diff against the acceptance criteria. Role separation is what makes the gate trustworthy.
3. Output: pass/fail + review artifact attached to the run.
4. On pass → PR opened/updated → `awaiting-review` (human checkpoint = GitHub PR review).

### 6.6 Agent identity: none. Personas: yes.

**Decision: no bot identities.** Agents act as the node owner's git/`gh` identity, and the work is *authored by the human* - agents did grunt work under the human's planning, discovery, and supervision, and accountability is the owner's (§6.2). No `hall9k-agent[bot]` PRs, no `Co-Authored-By: Claude` trailers (Claude Code adds these by default - configure them off). The audit trail lives in Hall9k, not in commit cosmetics: every PR ← run ← node ← owner.

**Personas are roles, not identities**: a reviewer persona, a discovery persona, an idea-capture persona, an implementer persona - each is a prompt template + tool policy + verification profile applied to an agent session. Role separation matters (the reviewer must not be the implementer's session, §6.5); named separate *accounts* do not.

---

## 7. Discovery Mechanics

Discovery is conversational and runs as **interactive Claude Code sessions** - which fits local-first natively and runs on subscription:

- **Capture**: `/idea` - the 30-second transaction. Claude asks just enough (which project, if any? attachments?), writes via `h9k idea add`, done. Ends with an optional bridge: "want to discover this now?" - so idea → discovery happens in one motion when the human is at the machine and in the mood, without making capture a commitment to think. **Capture and discovery are different speeds; keep them separate commands.** (Wispr Flow note → paste into `/idea` is the standard capture flow.)
- **Triage**: `/triage` (or "let's triage" in the orchestrator window) - reads the inbox via `h9k`, batch-recommends pursue/park/kill (flags resurfaced ideas), human decides conversationally, state updated via `h9k`. Weekly cadence, like a triage meeting.
- **Discovery**: `/discover <idea>` - interactive interview against the idea, drafts epic/task breakdown into the idea's record, iterates until human approves.
- **Publish / Adopt**: `/publish` - pushes approved breakdown to Jira/GitHub via the connector (or links to existing items), writes back external refs.

The shape - capture → triage → interview → draft → approve → publish/adopt - is stable; whether the record lives in markdown or Postgres in early versions is a build-time call (open decision #7). Slash commands are trivially editable prompts; iteration is cheap.

### 7.1 Where the discovery conversation lives

Discovery is a **phase of the idea's life, not its own aggregate**: discovery *events* (`DiscoveryStarted`, `BreakdownDrafted`, `BreakdownApproved`) go on the **Idea's event stream**. The conversation *transcript* is bulky, so it's stored like an attachment: an artifact on disk (content-addressed, §8.1), referenced from the `DiscoveryCompleted` event alongside a distilled summary (key decisions, alternatives rejected). Tasks carry an **origin link** to the idea ID; agents wanting deep context pull the transcript artifact. Streams stay lean, the origin trail stays complete. Confirm during event-stream modeling (alongside decision #9).

---

## 8. Ideas & Capture

What's settled:
- **Idea record**: freeform text + optional attachments/links + optional project. No required structure beyond the text. Structure is triage's job, not capture's.
- Voice: Wispr Flow handles speech-to-text; capture a note anywhere (Wispr Flow, Apple Notes, wherever), then paste it into `/idea` or `h9k idea add` at the machine. No voice feature in Hall9k itself.
- **At-the-machine capture**: `/idea` in a Claude session (§7) or `h9k idea add` directly. Both land in the inbox in `triage` state.

### 8.1 Attachments (files on disk, never in Postgres)

- `h9k idea add --attach ~/Downloads/mockup.png` - the CLI **copies** the file from wherever it lives into the node's data directory: `~/.hall9k/attachments/`, **content-addressed** (stored under the file's hash). Duplicates dedupe; references never break on rename or source deletion.
- Postgres stores metadata only: filename, hash, mime type, size, owning idea/task.
- Content-addressing also ages well for P2P: whether future nodes synchronize or shard attachments, "do you have hash abc123?" is the primitive either way.
- Discovery transcripts and run artifacts use the same store (§7.1).

### 8.2 Away-from-machine capture

Deliberately deferred - not a hard problem, just not now. Interim habit: note it anywhere (Wispr Flow, notes app), paste at the machine. Candidates when it's time: a capture inbox in a private git repo / GitHub issues that a node ingests; phone → SMS/email → a poller on the node; the far-future web portal. (Open decision #10.)

---

## 9. Connectors

Connectors run **on the node** (daemon/CLI reach external systems directly - `gh` CLI and/or provider REST APIs).

### 9.1 Source-of-truth split

- **External system owns**: card/issue *content* - the user-story-style task documentation (title, description, human-facing status, comments). Kept human-readable; useful to both humans and agents at that level.
- **Platform Task entity owns** (§4.1): everything operational - agent-facing context (the verbose prescriptive material that would make an issue unwieldy), orchestration state, runs and event streams, PR links, conversation thread, token/cost, budgets - keyed by external ref (`jira:PROJ-123`, `github:owner/repo#42`). **Never a duplicate copy of the card content that can drift.**

### 9.2 Adapter interface

`IWorkItemProvider`: fetch, create, transition, comment, link - implemented per provider. Must support both funnel exits: **create** (publish - new external item) and **link** (adopt - attach to an existing item, §3.1a).

**Per-project mapping config** (set at project creation):
- Jira: which issue types = epic/task; which workflow transitions map to lifecycle states. (More complex; custom types supported via mapping.)
- GitHub: labels/states mapping - but note GitHub Issues now has native **sub-issues, issue types, and milestones**, so epic→task parent/child is directly representable. Verify current capabilities when speccing the adapter.

### 9.3 v0 scope

GitHub connector only (issues + PRs + repos), piggybacking `gh` CLI auth. Jira connector comes later (§14). The adapter interface exists from day one so Jira is an implementation, not a refactor.

---

## 10. Identity & Connections (local-first)

No platform login exists - you're on your own machine. Identity is simply the machine's tooling identity:

- **v0**: GitHub access piggybacks the existing `gh` CLI auth. Anthropic access piggybacks the machine's Claude login (or `ANTHROPIC_API_KEY` per §6.4). Jira later = an API token in local config.
- **Secrets** live in local config (`~/.hall9k/`), OS keychain where it earns its keep. No cloud vault.
- **Keep the connections principle** even locally: external-system access is modeled as a **list of connections** (`provider, external_account_id, credential ref`), and projects bind to a `connection_id` - never "the machine's GitHub" hardcoded. That preserves multiple-accounts-per-provider later and keeps the far-future portal (which would need real accounts) from forcing a data-model rewrite. It's one level of indirection, nearly free.

---

## 11. Tech Stack

| Concern | Choice |
|---|---|
| Language/runtime | C#, latest .NET |
| Hosting | **None. Local machines only.** |
| CLI | Spectre.Console.Cli (`h9k` on PATH) |
| Daemon | .NET `IHost` worker, native process (launchd / Task Scheduler / systemd via `h9kd install`) |
| Persistence | **Postgres (Docker Compose) + Marten (event sourcing) + Wolverine** (Critter Stack) |
| CLI ↔ daemon | Postgres as the bus (LISTEN/NOTIFY, Wolverine Postgres messaging) |
| Executor v0 | Claude Code headless (`claude -p --bare`, stream-json), detached processes |
| External access | `gh` CLI auth (GitHub); machine's Claude login or API key (Anthropic) |
| UI | The orchestrator window (interactive Claude Code) + `h9k` output. No web UI. |
| Naming | Platform: **Hall9k**. Repo/org/CLI: `hall9k`. Root namespace: `Hall9k` |

### 11.1 Solution structure (starting proposal)

```
hall9k/                          (repo; lives in the dev/ worktree)
├── Hall9k.sln
├── docker-compose.yml           Postgres for installed mode (Decisions Log #10)
├── Directory.Build.props        net10.0, nullable, implicit usings, seal-by-default
├── Directory.Packages.props     central package version pinning
├── .editorconfig
├── PLAN.md · TASK-MODEL.md · CLAUDE.md
├── src/
│   ├── Hall9k.Domain/           aggregates, events, VOs, deciders, projections,
│   │                            Marten/Wolverine config extensions — vertical slices
│   ├── Hall9k.Cli/              h9k (Spectre.Console.Cli; packs as a dotnet tool)
│   ├── Hall9k.Daemon/           h9kd (IHost worker: dispatch, leases, tailing,
│   │                            IProcessManager, worktrees, executor, verification)
│   ├── Hall9k.Connectors/       IWorkItemProvider + GitHubWorkItemProvider (gh wrapper)
│   ├── Hall9k.AppHost/          Aspire dev loop: Postgres + daemon + dashboard
│   └── Hall9k.ServiceDefaults/  Aspire OTel/health wiring (daemon only — CLI stays cold-start lean)
└── tests/
    └── Hall9k.Tests/            one project, two tiers: unit (DB-free) + integration
                                 (Alba + Testcontainers)
```

References: `Cli → Domain + Connectors` · `Daemon → Domain + Connectors + ServiceDefaults` ·
`Connectors → Domain` · Domain references nothing of ours.

(Settled at kickoff — Decisions Log #16. Departures from the original sketch: no
`Hall9k.Contracts` in v0 — CLI and daemon share `Hall9k.Domain` directly, so there is no wire
boundary to put contracts on; it gets created when a portal/remote API creates one. Single
two-tier test project instead of per-assembly test projects.)

Event sourcing is a natural fit - the funnel and runs are event-shaped: `IdeaLogged`, `IdeaTriaged`, `IdeaParked`, `IdeaResurfaced`, `DiscoveryCompleted`, `WorkPublished`, `WorkAdopted`, `RunDispatched`, `RunEventReceived`, `QuestionAsked`, `AnswerProvided`, `VerificationPassed`, `RunCompleted`, `TokensRecorded`… origin trails and audit fall out for free.

---

## 12. The Attention Surface (no web dashboard)

**Everything awaiting the human, surfaced through the CLI and the orchestrator window:**

- `h9k status` - the one-pane view: running / done / flagged (`needs-human`, `needs-refinement`, awaiting PR review), across projects.
- `h9k task show <id>` - the task detail: external ref, PR(s), run history, event timeline, token/cost vs. budget, agent context, and the conversation thread.
- **The orchestrator window** renders and narrates all of this conversationally ("what needs me?", "how's task 12 going?") and relays answers into the `ask`/`answer` loop.
- `h9k watch [--notify]` - blocking watcher; desktop notifications are its job, not Claude's. An interactive Claude session is a *window you look through, not an alarm* - it checks when prompted.

A web portal rendering the same projections (they're all event-stream projections, §4.1) is the far-future option (§14) - nothing here depends on it.

---

## 13. v0: Local Edition (build this first)

**Reality check (Aug 2026):** even local-first, the full vision is a big build. v0 scopes down to the core value proposition so value lands in weeks, not months.

**Core value proposition:** *the human stops being the message bus.* Today: 10 terminals, every question/completion/verification routed through the human's attention - context switching kills focus. v0 replaces that with **one surface**: agents run to completion or park with a question; the human engages on their own schedule.

### v0 topology

Exactly §6.1 with one node: `h9k` CLI + `h9kd` daemon + Postgres container + orchestrator window + detached headless agents. Node identity + lease-based claiming from day one (§6.2) - the only multi-node tax paid in v0.

**The interaction loop:** you talk to the orchestrator window → it runs `h9k` commands → daemon dispatches detached agents → agents work, report, and `h9k ask` questions → flagged in `h9k status` → you answer via the window (or `h9k answer` directly) → daemon resumes the agent session with the answer injected.

### Scope exclusions

No web UI/portal, no full funnel (see `--from-issue` below), no Jira, no multi-node/P2P, no away-from-machine capture.

### Build slices (each independently usable)

1. **Slice 1 - parallel fire-and-forget** (~80% of the value): `h9k task add` → queue → daemon claims (lease) → worktree → detached agent → verify (build/test/lint) → PR via `gh` → visible in `h9k status`. Window-juggling replaced by a queue. **Ship this before anything shiny.**
2. **Slice 2 - the question loop**: agent calls `h9k ask` mid-run → task parks + flags → `h9k answer` → daemon resumes the session with the answer injected. Agents stop stalling silently or guessing.
3. **Slice 3 - reviewer agent**: separate session critiques the diff against acceptance criteria before the PR opens. The trust layer begins.

Slice 1 nicety (small, high value): `h9k task add --from-issue <url>` - adopt an existing GitHub issue as a task (title/body seed the agent context; external ref linked). The funnel formalism arrives later (§3.1a); this is its v0 seed.

### v0 CLI sketch

`h9k idea add [--attach <path>]` · `h9k task add [--from-issue <url>]` · `h9k task list/show` · `h9k dispatch <task>` · `h9k status` · `h9k ask` / `h9k answer` (agents call `ask` too) · `h9k logs <task>` · `h9k watch [--notify]` · `h9kd install/uninstall/run`

### What carries forward

Task entity + event streams (Marten), `IExecutor`, worktree/dispatch/lease logic, verification profiles, the CLI, cost tracking, node identity. Later versions grow *around* this core (funnel, Jira, multi-node) - the core does not get rewritten.

### Distribution ladder (decided; build only tier 1 now)

1. **v0 (audience: me):** clone + build. `docker compose up -d` (Postgres) → `dotnet tool install -g h9k --add-source ./artifacts` (PATH handled) → `h9kd install`. The dev loop is the install.
2. **Interim (audience: developers) - likely permanent:** GitHub Releases via one Actions workflow on version tag: self-contained per-OS binaries (no .NET required on user machines) + optional nupkg (for SDK users) + `install.sh`/`install.ps1` (curl-based - CLI downloads skip Mac Gatekeeper quarantine and Windows SmartScreen entirely; publish checksums). No Apple Developer Program needed at this tier.
3. **Optional someday:** Homebrew/winget wrappers pointing at the same release artifacts; Apple signing/notarization ($99/yr) only if browser-downloaded distribution to a general audience ever matters.

### v0 discipline rules

- The grand vision will whisper "just add a dashboard." Slice 1 first. Always.
- Steal ideas shamelessly from adjacent tools (Baton desktop app, mraza007/baton, OpenClaw). **Where the novelty lives, honestly:** the v0 core (local agent orchestration) is a crowded space and *should* be boring - that's what makes it buildable. What's unique is the combination and trajectory: decentralized, human-owned, per-developer nodes coordinating shared-project agent work through external sources of truth, with a formal idea funnel and an accountability model on top. Protect that combination; don't chase novelty in the plumbing.
- **Dogfood from the first working slice**: the moment Slice 1 runs, every subsequent Hall9k task goes through Hall9k. Slice 2 gets built by agents dispatched via Slice 1. The tool escapes manual orchestration by orchestrating its own development - this is both the test suite and the proof of value, and it should *accelerate* the build.
- Every v0 decision gets checked against "does this block the later vision?" If no: choose the fast option.

### Exit

v0 scoping/build happens in its own Claude Code session, seeded by this document. (See kickoff prompt accompanying this plan.)

---

## 14. Roadmap Beyond v0 (local-first all the way)

Rough order; each stage produces something usable. Don't proceed until the current loop has earned trust.

1. **v0 slices 1–3** (§13): the core loop on one node.
2. **Funnel on the platform**: `h9k idea add`, triage/discovery/publish-adopt flows formalized (per §3.1/§7), parking garage with resurfacing, origin links. Decide markdown-vs-Postgres record storage (decision #7) at kickoff.
3. **Daily-driver hardening**: budgets/limits per task/project, verification profiles per project, run artifacts (summary/decisions/questions) as the standard review surface, orchestrator-window skill matured.
4. **Jira connector** (mapping config, transitions) + multiple connections per provider (the picker).
5. **Multi-node**: second machine joins. Start with "several daemons, one reachable/shared Postgres" (leases already make this safe); evaluate true P2P (replicated state, node discovery) only if shared-Postgres genuinely doesn't fit the topology.
6. **Personas matured** (reviewer/discovery/implementer role templates, §6.6); executor #2 (proves the `IExecutor` seam) and/or Agent SDK sidecar for mid-run approvals.
7. **Optional, far future: web portal** - a remote window over the same event-stream projections for managing tasks away from the machines. If/when it happens, adopt the house API conventions ("Unified API: Architecture & Conventions" - see §16). Nothing before this stage depends on it.

### Explicit non-goals

Hosted SaaS / multi-tenant cloud. Building a kanban UI. Two-way card content sync. Bulk backlog mirroring. Voice capture (Wispr Flow covers it).

---

## 15. Open Decisions & Flagged Revisits

| # | Decision | Status |
|---|---|---|
| 1 | Windows daemon form: Task Scheduler startup entry vs. Windows Service (credential/session implications) | **Decided: Task Scheduler logon task** (runs as the user → credentials work); built as first dogfooded task post-Slice-1 (Decisions Log #3) |
| 2 | Agent identity | **Decided: none** - agents act as the owner, personas are roles not identities (§6.6) |
| 3 | Agent SDK sidecar (TS) for mid-run approval hooks | Deferred until fire-and-forget hurts (§6.3) |
| 4 | Aspire for local dev orchestration | **Decided: yes** — AppHost for the dev loop; Compose remains for installed mode (Decisions Log #10) |
| 5 | GitHub Issues capabilities (sub-issues/types) for epic mapping | Verify current API when speccing connector |
| 6 | Billing split (paused Jun 2026) may return | Resolved as design principle (§6.4); monitor Anthropic announcements |
| 7 | Funnel record storage in early versions: markdown repo vs. Postgres from day one | Pick at roadmap #2 kickoff (v0 has no funnel) |
| 8 | Executor #2 (Codex/Kimi) | Later; seam designed for it |
| 9 | Idea → task/epic modeling: idea archived + produces tasks (current lean) vs. idea mutating into epic | Confirm during event-stream modeling (§4.1) |
| 10 | Away-from-machine idea capture (local-first) | Unresolved by design (§8); revisit at roadmap #2–3 |
| 11 | Multi-node coordination: shared Postgres vs. true P2P replication | Evaluate at roadmap #5; leases keep both open |

---

## 16. v0 Decisions Log

Decisions made during the v0 kickoff session (2026-08-16), Brian + Claude. Each was discussed individually; rationale summarized.

1. **Billing & spawn flags.** Subscription is the default billing mode; agents spawn as plain `claude -p` (no `--bare`). Verified against claude 2.1.233: `--bare` never reads OAuth/keychain — it is API-key-only, so it's used *only* when a node/project is configured for `api-key` mode. In subscription mode, agents inherit the user's full config (settings, plugins, skills, MCP servers) — this is desired (§6.6: agents act as the owner, with the owner's tools). The daemon overrides only what matters, e.g. `--settings '{"includeCoAuthoredBy": false}'` for the no-trailer rule. Caveats: interactively-authenticated MCP servers (claude.ai-connected Jira/Figma) may be silently absent headless — test before the Jira connector; tools not pre-authorized are denied headless (see #9).
2. **Agent output capture is file-based.** Each agent's stdout (stream-json) redirects to `~/.hall9k/runs/<run-id>/stream.jsonl`; the daemon *tails the file* rather than holding a pipe. Daemon restarts are a non-event (agents keep writing; the daemon reattaches). The daemon records PID + process start time (PID reuse guard). Completion signal = the stream's final `result` event, never the exit code (unrecoverable by a restarted non-parent). The file doubles as the raw transcript artifact (`h9k logs` reads it).
3. **OS scope: macOS in Slice 1; Windows immediately after, dogfooded.** Cross-platform seams (`IProcessManager`, daemon-installer interface) from the first commit; launchd implemented and tested in Slice 1. Windows (Task Scheduler *logon task*, not a Windows Service — services run as a different identity and lose the user's Claude/git/gh credentials) is among the first tasks Hall9k dispatches to itself, tested on Parallels, validated on the dedicated Windows tower. CI builds `windows-latest` from day one. Linux later.
4. **Worktree lifecycle.** One worktree per *run* (not per task) — retries/redispatches never collide: `wt-<task>-<run>/`, sibling of `dev/`. Branch per task: `task/<id>-<slug>`, created `git worktree add --no-track -b … origin/main` (never tracking origin/main upstream; `main` itself is never checked out outside `dev/`). Cleanup is state-driven: delete on done/merged; **keep on `needs-human`** (session resume requires the same cwd) **and on `failed`** (crime scene) until the task closes. Daemon serializes git ops per-repo (mutex, not retry loops) and runs `git worktree prune` at startup.
5. **Ask/answer = exit-and-resume.** The agent calls `h9k ask` and *exits* (the command's output instructs it unmissably to stop); on `h9k answer`, the daemon runs `claude -p --resume <session-id> "<answer>"` in the same worktree. Session IDs are daemon-assigned up front via `--session-id <uuid>` (no parsing from output). Rejected: blocking `h9k ask --wait` (dies on Bash tool timeout — answers take hours/days) and held-open stdin via `--input-format stream-json` (re-couples agent lifetime to a live daemon pipe). Back-and-forth discussion works two ways: discuss with the orchestrator window first, answer once (default habit); or answer-with-a-question — each exchange is a suspend/resume cycle, recorded as `QuestionAsked`/`AnswerProvided` on the stream. **Pre-Slice-2 spike:** prove `-p --resume` works headless after the original process fully exited.
6. **Transcripts are artifacts; event streams carry milestones only.** Corrects the §4.1 implication of `RunEventReceived`-per-line: a run emits thousands of stream-json lines; they stay in the run's `stream.jsonl` (content-addressed store per §8.1 later). Marten streams record lifecycle milestones (dispatched, started, asked, answered, verification, PR opened, tokens, completed). Whether Run is its own stream vs. events on the Task stream: decided during Task-aggregate modeling.
7. **Leases: generation counter (fencing token) + adopt-before-reclaim.** Each task carries an integer `lease_generation`; every claim atomically increments it; every run records the generation it was dispatched under; any state change from a stale-generation run is discarded (correctness guarantee — no timing puzzles). The lease tracks *daemon responsibility*, not agent progress (PID + stream file track that). Daemon startup order: (1) **adopt** — reattach to live recorded runs and process any completed-while-down results, same generation, nothing killed; (2) sweep expired leases → requeue; (3) claim queued work, killing any superseded prior-generation process first (kill-before-redispatch). Net effect: on one node a healthy agent is never killed by lease mechanics; kills happen only for hung/superseded runs. Never two live runs per task. Heartbeats are the daemon's job; agents know nothing about leases.
8. **DB-as-bus disciplines.** NOTIFY is a doorbell, never a payload (LISTEN/NOTIFY delivers nothing to disconnected listeners; durable truth is rows/events; the daemon also polls as a sweep — Wolverine's Postgres transport does this natively). **Thin CLI, full daemon**: `h9k` uses a lightweight Marten session / raw Npgsql + raw `NOTIFY` and exits (no Wolverine host cold-start on every invocation — `h9k` is called by agents mid-run); the daemon runs the full Critter Stack. No socket/HTTP API between CLI and daemon, deliberately: `h9k` must work while the daemon is down (durable queue semantics for free), every command lands in Postgres anyway (the DB *is* the API), reads never need the daemon, and shared-Postgres multi-node (roadmap #5) is this same pattern with more listeners. Accepted trade-off: no synchronous request/response — nothing in v0 needs it.
9. **Agent permissions: `--dangerously-skip-permissions`, per-project opt-in.** Headless agents can't answer permission prompts; denied tools cause silent workarounds or stalls. Risk contained by isolated worktrees + own-repos scope. Allowlist tuning rejected for v0 as walking-skeleton friction.
10. **Aspire: yes** (resolves open decision #4). An AppHost orchestrates Postgres + daemon for the dev loop, with the dashboard for logs/traces. Docker Compose remains for installed mode (`h9kd install` under launchd needs Postgres independent of the dev loop).
11. **Hung-agent detection.** Three per-run signals, all falling out of settled designs: process liveness (PID + start time), **stream activity** (hung = process alive + `stream.jsonl` silent past a threshold — the daemon tails these files and persists last-activity to the run record, so `h9k status` shows alive/last-activity/tokens for every live run straight from Postgres), and **budgets** (readiness-contract max time/turns/tokens; exceeding = runaway by policy). **Policy: the daemon never kills on its own judgment.** Local stall threshold ~1h of stream silence (configurable) → flag on the attention surface only; the human kills. Budget overruns auto-kill *only when the task explicitly declared limits* (budgets are opt-in contract terms); no declared budget → nothing is auto-killed. Killed/failed runs keep worktree + transcript per #4.
12. **Lease expiry is locality-aware: local = mechanical, remote = human escalation.** Local expiry is unambiguous — the daemon interrogates reality directly (adopt / requeue / kill-before-redispatch, per #7), fully automatic. Remote expiry (multi-node, roadmap #5) is inherently ambiguous (node down vs. partition vs. overloaded) and is **never auto-stolen**: silence escalates with backoff (late → silent → presumed down; default threshold ~24h), and **escalation is owner-aware**: alerts fire only on nodes belonging to the *same owner* as the silent node. A different owner's silent node is shown passively in status (visible, colored by staleness, non-alerting — Sarah's silence is Sarah's business, per §6.2); takeover remains available as a deliberate act. When alerted (or acting deliberately), the human decides whether to **take over** — an explicit command that re-establishes the lease locally (incrementing the generation) and revokes the remote claim. The fencing token is what makes unenforceable remote revocation safe: an unreachable node's agent that later finishes reports at a stale generation and is discarded — worst case is wasted tokens, never duplicate work or corrupted state. Recorded now so the lease model carries it; v0 builds only the local path.
13. **Naming: CLI is `h9k`, daemon is `h9kd`** (was `h9`/`h9d`). Applied throughout this document; the kickoff prompt retains the old names as a historical record.
14. **All domain IDs are UUIDv7** via the **UUIDNext** package: `Uuid.NewDatabaseFriendly(Database.PostgreSql)` — the same library and call used in the owner's production Marten codebase (NTS platform). Time-ordered IDs give chronological sortability and Postgres index locality; satisfies §6.2's globally-unique-IDs rule.
15. **Domain model settled** (task 2, full detail in `TASK-MODEL.md`): Task and Run as separate streams; Owner/Node/Project/Connection as minimal event-sourced aggregates; run ID minted at claim time and carried on `TaskClaimed`; state split (Task = work lifecycle, Run = execution lifecycle); no multi-stream projections; heartbeats/last-activity as mutable telemetry documents; value objects over primitives and enums (closed vocabularies as sealed records with `Unknown` sentinels; enums only for unpersisted in-process outcomes); acronyms spelled out in type/method/property names; `Guid Id` PascalCase identity; System.Text.Json only.
16. **Solution structure settled** (task 3, recorded in §11.1): six src projects + one two-tier test project; no `Hall9k.Contracts` until a real wire boundary exists; ServiceDefaults referenced by the daemon only, keeping the CLI cold-start lean.
17. **The flip is live** (2026-08-16): Slice-1 tasks S1-01 through S1-10 shipped; the flip gate passed with PR #1 (h9k --version) built by a Hall9k-dispatched agent through real build+test gates, review comments resolved via /git:resolve-copilot-reviews, merged by the owner. Dogfooding is policy: subsequent work is queued via `h9k task add`; manual coding needs a stated reason. Also decided: workflow skills become repo-resident (`.claude/skills/` committed, versioned with the code) and a follow-up-run flow will handle PR review feedback on the existing branch — both queued as the first dogfooded tasks alongside the Windows CI fix (container-dependent tests must not run on windows-latest).
18. **PR closeout is a phase, not an event** (2026-08-17): PullRequestOpened begins closeout, it does not end the platform's involvement. The platform (daemon) — not the human — watches each awaiting-review PR for Copilot review comments, failing checks, and the merge itself, dispatching follow-up runs on the existing branch to resolve feedback and appending RunCompleted on merge (the reserved event finds its meaning). Bounded retries; repeated failure parks NeedsHuman. Built in two dogfooded steps: follow-up-run mechanics (backlog 03) then the automatic monitor (backlog 04).
19. **Slice-1 gaps adopted.** Minimal **Project entity** + `h9k project add` (repo path, base branch, verify commands — the daemon must learn these from somewhere; the v0 CLI sketch lacked it). `h9k task add --file task.md` (frontmatter + body) + stdin — the readiness contract doesn't fit CLI flags. Per-node **concurrency cap**, default 3 (§6.4 guidance). Daemon **retry/backoff on Postgres connect** (launchd starts `h9kd` before Docker Desktop at boot).
20. **Follow-up runs reopen the task** (2026-08-17, backlog 03): the mechanics under decision #18 are `TaskReopened` (Done → Queued, carrying the PR branch from the completing run's record) rather than a Done → Claimed "follow-up claim" (would need a parallel dispatch path beside the loop, bypassing the capacity cap, claim race, and fencing token) or a new task (severs the PR from its task's audit trail). A reopened task flows through the unchanged sweep → claim → launch pipeline; the launcher sees `FollowUpBranch` on the task, checks out the **existing** PR branch (`CheckoutExistingAsync` — local branch preferred, fast-forwarded to origin; recreated from origin if local is gone) instead of cutting a new one, and prompts the agent to run the repo-resident resolve-copilot-reviews skill against the PR URL. Verification gates and the push reuse `RunSupervisor`/`VerificationRunner`/`PullRequestOpener`; the opener detects the task's existing PR URL, pushes in place, and appends `PullRequestUpdated` (never a second PR). Guardrails: only Done reopens, and only with a PR URL — Failed/Abandoned stay dead ends. `h9k pr resolve <task>` is the on-demand trigger; the automatic monitor (backlog 04) drives this same reopen path. Full rationale: TASK-MODEL.md §2.1.
21. **Worktrees live until the pull request completes** (2026-08-17): removing the worktree at PR-open contradicted #18 — the worktree IS the follow-up workspace for review resolution and CI fixes. Retained until the PR is merged/closed or another node takes a lease on the task; removal moves to the closeout monitor (backlog 04), with the follow-up checkout-from-branch path kept for the other-node and purged-artifact cases. Origin incident: worktrees deleted at PR-open had to be recreated by hand to resolve Copilot reviews on PRs 4 and 5.
22. **The closeout monitor drives PRs to true completion** (2026-08-17, backlog 04): a daemon background service (`PullRequestMonitor`/`CloseoutEngine`) polls each awaiting-review PR through gh on a gentle interval (DaemonOptions.PullRequestPollInterval, default 3 minutes; reviews and CI move on human timescales and GitHub has no doorbell here). **Who polls: every node watches the runs it executed** (`RunDetails.NodeId`); the task in closeout is Done and lease-free, so run provenance is the only honest owner, and it is also where the worktree lives. Observations land as run-stream events (`PullRequestMerged`, `PullRequestClosed`, `PullRequestChecksFailed`, `ReviewFeedbackReceived`, `CloseoutParked`; TASK-MODEL.md §2.2). Merge appends `RunCompleted` (the reserved event finds its meaning), removes the retained worktree (#21), and deletes the task branch everywhere it lingers: local `git branch -D` (rebase merges mean the tip is never an ancestor of main; the observed merged-PR signal is the justification; origin incident: five merged task branches accumulated locally because nothing owned this step), remote delete when the merge didn't already, and `git fetch --prune`. Failing checks (only once no check is pending) and unresolved Copilot review threads dispatch follow-up runs through the #20 reopen pipeline, with `TaskReopened.Kind` selecting the fix-the-CI or resolve-copilot-reviews prompt. **Bounded retries**: `TaskReopened.Automatic` marks monitor-driven reopens; the aggregate counts them (reset by any manual reopen) and at DaemonOptions.MaxAutomaticCloseoutRuns (default 2, per the #11 never-loop-on-judgment spirit) the monitor parks the RUN (`CloseoutParked`) rather than the task: the task stays Done so `h9k pr resolve` (which now also takes `--checks` and resets the budget) remains the human's retry lever, and merge detection keeps running for parked PRs. `h9k status` composes the phase: ClosingOut for an in-flight follow-up, ChecksFailing/ReviewPending/AwaitingReview from the current run, NeedsHuman when parked, Done only after the observed merge. A PR closed without merge fails the run honestly, removes the worktree, and keeps the branch (it still holds unmerged work; no merged-PR signal, no deletion).
23. **An errored Copilot review is review-pending, not review-clean** (2026-08-17): during a GitHub partial outage, Copilot posted a review whose only content was "Copilot encountered an error and was unable to review this pull request." An errored review produces zero review threads, indistinguishable from a clean pass by thread count alone, so the #22 monitor would have waved through the exact PR that implements it (origin incident: PR #6 nearly ate its own dogfood). The inspector now reads each reviewer's latest review alongside the threads (GraphQL `latestReviews`, per-reviewer latest, so a successful re-review supersedes an errored one structurally); a Copilot-authored latest review whose body contains "unable to review" (a deliberately conservative match on Copilot's own failure notice, never arbitrary review text) lands as `ReviewErrored` on the run stream and holds the run at ReviewPending, which the monitor now watches alongside AwaitingReview. The monitor re-requests the review through the REST review-request endpoint (requesting copilot-pull-request-reviewer[bot]; the website may be down when this matters, which was the origin incident's exact circumstance), records `ReviewRerequested`, and never re-requests the same errored review twice (the review URL is the dedup key across sweeps). Re-requests draw on the same automatic budget as follow-up dispatches: the task's CloseoutAttempts plus the watched run's ReviewRerequestCount, checked against MaxAutomaticCloseoutRuns by both paths. A reviewer that keeps erroring parks the run (`CloseoutParked`) with the errored review named in the reason, surfacing as NeedsHuman; `h9k pr resolve` remains the reset lever. A successful re-review proceeds through the normal thread-resolution flow.

24. **Every diff gets an independent review before its pull request opens** (2026-08-17): between the verification gates and PullRequestOpener, the daemon's `ReviewEngine` dispatches a review agent — a separate headless session with **fresh context**, never the session that wrote the code (a session that saw the implementation reasoning rubber-stamps; same reason human teams don't self-review) — over the run's diff against the base branch. The prompt demands verified findings only (read the surrounding code, confirm the defect, discard the unconfirmed), each with file:line, a defect statement, and a concrete failure scenario, closed by a parsed `VERDICT: merge-ready | needs-fixes` line. Needs-fixes dispatches a fix session in the same worktree with the findings as its prompt; gates re-run; a *fresh* reviewer looks again (review → fix → gates → review). **Bounded** by DaemonOptions.MaxAutomaticReviewFixRuns (default 2, the #22 budget pattern); exhaustion, a fix run disputing a finding as not-a-defect/human-territory, or a reviewer returning no parseable verdict parks the run (`ReviewParked` → NeedsHuman in `h9k status`) with the positions on disk — never a guessed resolution, never an unbounded loop on judgment (#11 spirit). Milestones land on the run stream (`ReviewDispatched`, `ReviewCompleted` with the verdict, `ReviewFixDispatched`, `ReviewFixCompleted`, `ReviewParked`; TASK-MODEL.md §3.1); the full findings text is an artifact in the run's directory (`review-<cycle>-findings.md`), per #6. Review/fix sessions record `TokensRecorded` like any session. A parked run keeps its task Claimed and its lease alive (adoption refreshes the heartbeat) — the worktree is the human's workspace. Review is pre-PR by design: it works during GitHub outages and on non-GitHub origins (origin incident, 2026-08-17, backlog 06: a GitHub outage errored Copilot's review out entirely, leaving the pipeline's only reviewer absent), and PRs arrive pre-reviewed with Copilot demoted to a second opinion. The human's unpark levers are v0-manual: fix in the worktree by hand, or `h9k task abandon`; a `h9k review resolve` retry lever mirroring `h9k pr resolve` is deliberately deferred until the park pattern proves out.

25. **Failed tasks get an explicit human-driven retry: `h9k task retry`** (2026-08-17): `TaskRetried` moves a Failed task back to Queued through the decider (Failed-only; Abandoned stays a dead end), preserving lease-generation fencing (the next claim increments as usual, per #7). Origin incident (2026-08-17): the first two automatic follow-up runs completed their work and passed the gates, then died at the daemon's plain push against their rebased branches (backlog 08); both tasks fell to Failed, which had no exit, and the completed, gated work sat stranded in the worktrees until it was force-pushed by hand. Failure of the machinery around the work must not permanently condemn the task that contains the work. Design: retry *appends*, never rewrites (the stream reads added → … → failed → retried → claimed, and `h9k task show` keeps the failure reason alongside the retry reason); it is distinct from `TaskReopened` (Done-only, PR closeout, different guards) rather than overloading one event with both meanings; and it is human-only (the closeout monitor never retries a Failed task automatically: a failure that repeats without human eyes is the never-loop-on-judgment rule, #11), so the event carries no Automatic flag and touches no budget (like `h9k pr resolve`, the human asking is the grant). The event records the failed run's branch as observed at retry time; the launcher resumes it through the existing follow-up checkout path when it survives (retained worktree first, then local or origin branch) and starts clean from the base branch when the artifacts are gone (never an error, since the retry's whole point is recovery). A required reason (defaulted honestly by the CLI when omitted) lands on the stream for the audit trail.

26. **Follow-up runs fold fixes into the owning commits, and the commit style is a preference** (2026-08-17, backlog 08): a PR branch reads as a natural progression of the whole change (the AGENTS.md authored-history rule), so the follow-up prompts now instruct the mechanics directly: map each fix to the most recent branch commit touching the same file, `git commit --fixup=<owning-commit>` (one fixup per owning commit when a fix spans owners; genuinely new scope gets a new properly-titled commit, never "review fixes"), `GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash origin/<base>`, then the REQUIRED tree-identity check (`git diff <old-tip> HEAD` empty) so verification-gate results honestly describe the rebased tree. The mechanics also live in the repo-resident **absorb-review-fixes** skill, reusable by hand. The style is a `CommitStyle` value object on the project (`h9k project set --commit-style narrative|append|default`), resolved project-over-platform with `DaemonOptions.DefaultCommitStyle` as the platform default (Narrative; user-level defaults arrive with IDEA-platform-defaults); Append keeps the historic stack-on-top behavior. The daemon absorbs the rewrite it now asks for: `PullRequestOpener` pushes follow-up runs (`RunDispatched.IsFollowUp`) with `--force-with-lease`, never plain `--force`; a failed lease means the branch moved on origin and fails the run honestly (no blind retry; `h9k pr resolve` is the requeue lever). The worktree manager treats a diverged resumed branch as rewritten-on-origin and resets the follow-up worktree to the remote tip (the pull request's truth; the stale tip stays in the reflog), while a local tip strictly ahead of origin is kept, since that is unpushed work. Branch cleanup already tolerated rewrites (`-D` plus remote delete on the observed merge). Origin incidents (both 2026-08-17): PR #6's review round landed as a separate "harden closeout" commit and was rebuilt into the owning commits by hand; the first two automatic follow-up runs then rebased correctly and the daemon's plain push failed both ("failed to push some refs"), stranding completed, gated work in their worktrees.

27. **Failed is a needs-human waypoint, not a terminal state** (2026-08-18): a failed state means there is an unsolved problem, and an unsolved problem is not an ending. Terminal states say how the story ended; "ended in failure" is only true when a human walks away, which is what Abandoned already means. `TaskState.IsTerminal` is now Done and Abandoned only, and Failed has exactly three human-only exits, which `h9k task show` teaches on any failed task: **retry** (`h9k task retry`, re-run, #25), **resolve** (`h9k task resolve --reason <why> [--pr <url>]`, the new `TaskResolved` event, Failed → Done: the human's attestation that the objective was met despite the run failure, with the PR URL recording where the work landed), and **abandon** (`h9k task abandon`, walk away; previously rejected from Failed because Failed was terminal). The resolve reason is required: an attestation without a why is a guess (the AGENTS.md never-guess rule). Resolve appends, never rewrites: the stream reads added → claimed → failed → resolved, and the failure reason stays visible beside the resolution. Double-Fail stays rejected (`TaskDecider.CanFail`, which the daemon's failure recorders also use as their pre-check), and Fail from Done or Abandoned still conflicts. `h9k status` counts Failed toward "need you" and ranks it just under NeedsHuman, since it now waits on a human decision. Origin incident (2026-08-18): tasks a282deb0 and 13241bd8 sat Failed although their work merged as PRs #7 and #8. The failure was in the machinery (the push step), the objective was met, and the final state was simply wrong.

28. **The pre-PR pipeline verifies what it used to trust** (2026-08-18, backlog 14): four fixes from one origin story — the first fully-automated cycle, tasks 08 and 09, where the review loop, closeout monitor, and retry lever all worked as designed and everything around agent output shape and lease lifetime did not. (a) **A parked run structurally holds its task.** #24's claim that "a parked run keeps its task Claimed and its lease alive (adoption refreshes the heartbeat)" was false in practice — it held only while the heartbeat service was actually ticking, and heartbeat decay (a stopped daemon, a laptop asleep past the 60-second timeout, a wake-up sweep racing the first heartbeat tick) requeued a review-parked task; the platform then rebuilt the same feature from scratch across generations 2-4, roughly tripling the token cost, before gen 5 completed. The correction: the expiry sweep now checks the expired task's current run and, when it is ReviewParked (or CloseoutParked — the guarantee belongs to "parked", not one flavor), refreshes the lease instead of requeueing. A parked lease can no longer expire into a requeue, on any node, no matter how long the daemon slept. (b) **A verdict-less reviewer gets ONE same-session re-prompt, then parks.** The first live review ended with "I'll deliver findings and the verdict when it completes" — a promise, not a verdict — and parked a finished, correct implementation. The review prompt now forbids ending without the VERDICT line (wait for running checks, then conclude), and the engine resumes a verdict-less session once (`claude -p --resume`, the #5 pattern — the session already read the diff; a fresh one would re-spend that work) with `ReviewVerdictReprompted` on the stream; a second verdict-less ending parks (never loop on judgment, #11). (c) **`h9k review resolve` is the unpark lever** #24 deliberately deferred, now proven needed (its absence left a parked run no path forward except abandonment): `--merge-ready` sends the run on to PullRequestOpener, `--needs-fixes <reason>` dispatches a fix session with the reason as its findings; `ReviewParkResolved` records the human verdict on the run stream, and like `h9k pr resolve` it restores the automatic fix budget — the human asking is a fresh grant (#22). The daemon's dispatch cycle resumes resolved runs (adoption's mid-loop counterpart). (d) **Zero commits fails fast.** Task 08's agent completed all its work uncommitted; gates passed vacuously on the unmodified tree and the failure surfaced two stages late as "No commits between main and branch" at PR creation. The verification runner now counts the branch's commits past the base before any gate runs and fails the run honestly ("agent produced no commits") — except Research tasks, whose deliverable is the transcript (the one TaskType whose legitimate output is empty); an unobservable count is never treated as zero (the gates then surface whatever is actually broken). Retry and follow-up prompts now also tell the agent the retained worktree may already carry a previous attempt's work — committed or uncommitted — to review before starting over, closing the loop that made requeue-from-scratch so expensive.

29. **System sleep is not node death** (2026-08-18): a laptop lid-close during two active runs became a generation storm. Each sleep stopped heartbeats; each wake ran the expiry sweep before the heartbeat service's first tick; the sweep saw 50-minute-stale leases and requeued both tasks while the previous agents resumed alongside their replacements. Task 13 reached five simultaneous Running generations, four agents sharing one retained worktree, before a human killed the daemon and every agent by hand; roughly three tasks' worth of tokens went to building the same two features. Four defenses, all in the dispatch path. (a) **The OS outranks the timestamp for local leases.** Before expiring a lease this node holds, the sweep asks `IProcessManager` whether the current run's recorded process is alive (pid + start time, the #2 identity discipline the adoption path already uses); a live local process means a live lease and a refreshed heartbeat, whatever the heartbeat timestamp says. Remote semantics are unchanged: pids only mean anything on the node that owns them, so a genuinely silent remote lease still expires on the timeout. (b) **Wake detection is a wall-clock comparison, no platform APIs.** The sweep records when it last ran; a gap beyond the expected cadence (PollInterval + HeartbeatInterval) means the whole daemon, heartbeat service included, was suspended (sleep, debugger pause, VM freeze all look identical), so this node's heartbeats are refreshed before expiry is evaluated. The wake-time race between sweeper and heartbeat service is closed structurally, inside the sweep itself, rather than by timing luck. (c) **Single-flight per task per node.** A claim is refused while a previous generation's agent process is still alive on this node, closing the duplicate-agent hole from the other side (a requeue that slipped through by hand or before this build). The refusal is per cycle: once the OS reports the process gone, the next claim proceeds. (d) **A merged pull request is never rebuilt.** Before spawning an agent for a task carrying a PR URL, the launcher consults the provider: already merged means the task closes out on the spot (completed with its PR, lease released, worktree and branch cleaned like any closeout) instead of redispatching. Inspection failure falls back to a normal dispatch, since the network is often still down right after a wake. Incident for (d) specifically: after PR #11 merged, the storm-killed generation 5's lease expiry requeued the task and generation 6 spawned a fresh agent to rebuild the feature already on main. This narrows but does not replace #28(a): parked runs' leases stay alive by design; this keeps RUNNING runs' leases honest across suspensions.

30. **A run's input side is three counts, and cost is observed, never derived** (2026-08-18): `TokensRecorded` read only `usage.input_tokens` from the result payload, which for a cached session is a rounding error. The event now carries `CacheReadInputTokens` and `CacheCreationInputTokens` alongside `InputTokens`, appended as defaulted parameters so streams written before they existed replay as zero rather than as a reconstruction (the never-guess rule); `RunAggregate` and `RunDetails` accumulate each count on its own line, and `TotalInputTokens` sums them where a total is what is wanted. The three stay separate rather than lumped because they price differently, so any roll-up that adds them first can never be turned back into money. `CostUsd` remains whatever `total_cost_usd` reported, recorded as observed and never recomputed from the token counts: the platform does not hold a price list, and a computed cost that drifts from the bill is worse than no cost at all. An absent field is zero, never inferred from its siblings. Origin incident (2026-08-18): `mt_doc_rundetails` showed 444,443 output tokens against 822 input tokens across 14 runs, off by about three orders of magnitude, because a resumed session reports nearly all of its input under `cache_read_input_tokens` (one real run: 118 fresh input tokens, 8,239,942 cache-read, 196,080 cache-creation). Any cost report built on those numbers would have been confidently wrong, so the `h9k stats` idea waits until the counts are trustworthy.

31. **The daemon has a CLI-owned lifecycle; autostart is a strictly opt-in extra** (2026-08-19, redesigns S1-12): nobody evaluating a local-first tool wants installation to leave a permanently resident background process behind (colleague feedback at the first demo), and the architecture had already paid for the alternative — adopt → sweep → claim at startup plus the closeout sweep mean a stopped daemon costs latency, never correctness, the same trade #29 encoded for sleep. So: **`h9k install` registers nothing** — it publishes h9k + h9kd release binaries to `~/.hall9k/bin` and retargets an existing `h9k` symlink on the PATH (or creates one), idempotently, never clobbering a real file of that name; the `~/.local/bin` fallback is vetted by the same check as every PATH directory, since it is chosen precisely when nothing on the PATH vetted it; re-running after a merge republishes and offers to restart a running daemon, which answers installed-binary staleness (origin incident: the hand-made symlink went stale the moment main advanced). **`h9k daemon start`** launches h9kd detached through a `/bin/sh` intermediary that backgrounds the process and exits at once — h9kd is reparented to launchd (pid 1) with stdin from /dev/null and stdout/stderr appended to `~/.hall9k/h9kd.log` (read only ever from its tail, and kept inside an 8 MB budget by the daemon itself: a five-minute timer copies the log aside to `h9kd.log.1` and truncates it **in place**, never renames it, because the daemon holds that file's descriptor open for its whole lifetime and a rename would send every later line into the rolled-aside generation invisibly, while an O_APPEND writer lands correctly at the start of a truncated one; `h9k daemon start` runs the same rotation before it starts anything. Origin incident (2026-08-19, pre-PR review of this task): rotation ran only on the start path, so an append-only log belonging to a daemon started once and left up for weeks grew for as long as the node lived. The daemon's console logging filters Npgsql and Marten to Warning in code, not appsettings.json — the installed daemon's working directory is never its binary directory, so a published appsettings.json is not read at all — which keeps the tail on what the daemon did rather than on SQL), the double-fork pattern with no parent-shell tie (origin incident: the hand-started daemon died three times in one day with its shell). On start the daemon runs an immediate closeout sweep alongside adoption and the lease sweep and logs one **catch-up report** line ("Catch-up complete — adopted N run(s) … observed K merge(s)"), which `h9k daemon start` tails and prints, so the on-demand cost is visible the moment it is paid. **Single instance is enforced twice**: the CLI refuses politely with the running pid, and the daemon itself holds an advisory lock file for its lifetime (pid file carries pid + start time, the #2 identity discipline) — the guard's refusal exits 0 deliberately, so a KeepAlive LaunchAgent that loses the race is never thrash-restarted by launchd. **`h9k daemon stop`** is SIGTERM-graceful (in-flight event appends finish inside the host's 30s shutdown budget; detached agents keep running for adoption, per #2/#7) and goes through `launchctl bootout` whenever launchd owns the job, so stopped always means stopped. **`h9k daemon autostart enable`** is the only path to start-at-login: it writes a per-user LaunchAgent (RunAtLoad; KeepAlive with SuccessfulExit=false, so crash-restart never resurrects a clean stop; `AbandonProcessGroup` so tearing the job down never sweeps the detached agents, which share h9kd's process group because nothing gives them one of their own and which launchd would otherwise signal along with the job, contradicting the very promise stop prints; origin incident 2026-08-19, cycle-5 pre-PR review of this task) pointing at the installed binary; `disable` fully unregisters. **The registration carries the enabling shell's environment** (`PATH` plus whichever `HALL9K_*` overrides are actually set, recorded as observed and never invented), because launchd starts a job from its own environment, whose default `PATH` is `/usr/bin:/bin:/usr/sbin:/sbin` and holds neither `claude` nor `gh`; enable also names any tool the recorded `PATH` cannot resolve. `disable` reports a daemon as stopped only when it saw the pid launchd owned for the job actually go away, since `bootout` succeeds just as happily on a loaded but idle one. Origin incident (2026-08-19, pre-PR review of this task): an autostarted daemon would have reported running and healthy while every dispatch died on sh's exit 127, and `disable` would have claimed to stop a daemon that was never alive. A crash-restart under autostart also doubles as the Postgres boot-race retry: a daemon that starts before Docker exits nonzero and launchd retries it, while an on-demand `h9k daemon start` detects the early death and says to start Postgres. `h9k status` states plainly when the daemon is not running ("tasks queue but do not dispatch") so a quiet queue is never a mystery. Windows (Task Scheduler logon task, #3) stays deferred to S1-14 behind the `IDaemonAutostart` seam. This supersedes §6.1's original "`h9kd install` self-registers the right service per OS" — registration is now consent, not a side effect.

---

## 17. Reference Materials

- **Steps of AI Adoption** (Boris Cherny, Anthropic, Jul 2026) - the Level 0–4 framing; Step 2→3 recipe drives this design.
- **Collaboard** (github.com/MrBildo/collaboard) - inspiration only, not a dependency. Key takeaway: it's a coordination surface (kanban + MCP server + webhooks), not an orchestrator; the closed-loop pattern (board event → dispatcher → agent → board) is the architecture regardless of whose board. Also: persistent agent identities with roles (implementer/reviewer/coordinator) beat anonymous one-shot sessions.
- **Claude Code headless docs** - `claude -p`, `--bare`, `--output-format stream-json`, session resume: https://code.claude.com/docs/en/headless
- **Claude Agent SDK** (TS/Python) - the upgrade path for mid-run control: https://platform.claude.com/docs/en/agent-sdk/typescript
- **Anthropic C# SDK** (`Anthropic` NuGet) - raw Messages API client only; *not* an agent runtime: https://platform.claude.com/docs/en/api/sdks/csharp
- **Unified API: Architecture & Conventions** (AgelessRx Confluence) - house API standards, relevant **only if/when the far-future web portal happens** (roadmap #7): https://agelessrx.atlassian.net/wiki/spaces/SD/pages/1219035141/Unified+API+Architecture+Conventions
