# Hall9k v0 Kickoff Prompt


Read PLAN.md in full before responding.

We are building **v0: Local Edition** only (PLAN.md section 13). The rest of the document is the long-term vision - it constrains our choices ("does this block the platform later?") but we are NOT building it now. Do not scaffold a web UI/portal, Jira, multi-node/P2P networking, away-from-machine capture, or anything cloud-hosted. There is no coordinator - this platform is local-first (CLI + daemon + Postgres), permanently.

Context about me: I'm a senior C# engineer using latest .NET for this project. Strong in C# and TypeScript. New-ish to Marten/Wolverine event sourcing - explain modeling decisions as we make them.

The v0 topology (section 13) is settled at the concept level:
- `h9` CLI (Spectre.Console.Cli, on PATH, execute-and-exit) - used by me, by scripts, and by headless agents
- `h9d` daemon (native host process via .NET IHost worker, NOT containerized - it spawns `claude -p` on the host and needs host credentials/paths; self-registers via `h9d install` per OS)
- Postgres in Docker Compose, Marten + Wolverine from day one; the database is the CLI↔daemon bus (LISTEN/NOTIFY via Wolverine)
- Detached headless agents (`claude -p --bare`, stream-json) that outlive any terminal, reporting and asking questions via `h9`
- An interactive Claude Code session as the conversational window (stateless; a CLAUDE.md/skill teaches it the orchestrator role)
- Node identity + lease-based task claiming from day one (multi-node future-proofing, nothing more)

Your first tasks, in order:

1. **Challenge the plan.** Before any code: tell me what in the v0 scope looks wrong, risky, or underspecified from a build perspective. Especially: detached process spawning/monitoring across OSes (process lifetime, orphan handling, capturing stream-json), worktree lifecycle (creation, cleanup, collision), the h9 ask/answer resume mechanics (claude -p session resume with injected answers), and the DB-as-bus pattern (LISTEN/NOTIFY via Wolverine vs. simpler polling). Push back where I've been hand-wavy.

2. **Model the Task aggregate and its event stream** (PLAN.md section 4.1) for v0 - including node identity and lease semantics. Resolve open decision #9 only as far as v0 needs (v0 has no funnel; `--from-issue` adoption seeds a task directly).

3. **Propose the v0 solution structure.** Starting point is section 11.1. Likely shape: Hall9k.Cli, Hall9k.Daemon, Hall9k.Domain, Hall9k.Contracts, Hall9k.Connectors - but propose what v0 actually needs, with Docker Compose for Postgres, and Aspire only if it earns its keep locally (open decision #4).

4. **Produce a Slice 1 task breakdown** (section 13 build slices) - small, ordered, verifiable tasks with acceptance criteria, dogfooding the task-readiness contract from section 4. Include `h9 task add --from-issue <url>` as a Slice 1 nicety. We will feed these tasks to Hall9k itself once Slice 1 runs.

5. **Scaffold the repository** with this specific worktree-first layout (we are in the workspace root):
   - `git init --bare hall9k.git` - the repo structure lives in `hall9k.git/`
   - Create the first worktree: a `dev/` folder hosting the `main` branch (`git --git-dir=hall9k.git worktree add dev main` after an initial commit, or equivalent - propose the cleanest sequence)
   - Move PLAN.md into the worktree so it's versioned and can evolve with the project
   - Create a **private GitHub repo** in my account (`gh repo create hall9k --private`) and set it as `origin` of the bare repo; push main
   - All solution scaffolding then happens inside `dev/`
   - This layout exists because Hall9k itself will manage agent worktrees as siblings of `dev/` later - confirm the layout supports that before committing to it

6. Then we build.

Working agreements:
- Slice 1 before anything shiny. If I drift toward dashboard/platform features, call it out.
- **Dogfood ASAP**: the moment Slice 1 runs, subsequent Hall9k tasks go through Hall9k itself. Structure the Slice 1 breakdown with that flip in mind.
- Pace: I want a walking skeleton fast - ideally the scaffold + first vertical slice moving in our first working session. Bias toward the simplest thing that runs end-to-end; refactor after it moves.
- Every dependency or pattern choice gets a one-line "why" and a one-line "does this block the later vision?"
- Agents commit as me - no bot identities, and configure off any Co-Authored-By trailers (PLAN.md section 6.6).
- Write decisions into PLAN.md as we make them (append a v0 Decisions Log section).
- Keep CLAUDE.md updated with build/test/run commands as they solidify - and draft the orchestrator-role CLAUDE.md content (how an interactive Claude session should use h9) as part of Slice 1, since agents and the orchestrator window both depend on it.

Start with task 1: your critique of the v0 plan.
