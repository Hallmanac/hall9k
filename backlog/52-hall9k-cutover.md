---
project: hall9k
type: chore
objective: The hall9k project itself moves into its default project home - the dogfood project becomes the first conforming citizen of the shape it built
criteria:
- Every branch and all local work in the old location is verified pushed before anything else happens - a fresh clone carries nothing local, so nothing local may exist that matters
- The hall9k project's home is materialised at the default location (~/.hall9k/projects/hall9k) by the platform recipe (h9k project init), never by filesystem moves: fresh bare clone from the recorded remote, dev/ worktree, seeded skills, generated AGENTS.md, ideas/, tasks/, .claude/ adapter - the landed 47/48/49 shape, which has no top-level runs/
- A build and the full test suite pass from the new home's repo/dev before the daemon is touched
- The project's recorded paths update to the new home, the daemon is bounced onto them during a quiet board (no live worktrees), and startup adoption reports clean (adopted or empty, zero requeued, zero failed)
- One end-to-end task dispatched after the bounce runs entirely in the new home (worktree cut under <home>/repo/, run recorded under its task, PR opened) and closes out clean
- The old ~/Code/Hall9k_Platform location is retired by Brian himself, manually, only after all of the above holds - nothing is deleted by platform or agent
- The repo's in-tree backlog/ directory is marked as the dogfood-era archive (a README line at its top), and the orchestrator-window docs plus any absolute paths naming the old location are corrected - this docs sweep is the cutover's one dispatched companion task
- Existing global idea workspaces at ~/.hall9k/ideas stay exactly where they are: a project-less idea's workspace is global by design (#35, #79), and capture-before-classification is the point of the global space
---
The one-time cutover ruled at discovery (idea 64e4ebd2), amended at the
criteria walk (2026-08-24) after decisions #76-#79 landed: clean start at the
default location via the platform's own recipe, not adopt-in-place and not
filesystem relocation - #76 ruled that init always materialises fresh from the
recorded remote, which dissolved most of the agent-assisted migration this
file originally imagined into platform code. What remains is a supervised
operator sitting (Brian plus the orchestrator window): push-verify, init,
repoint, bounce, prove with one dispatched task, then Brian retires the old
location by hand. The docs sweep is the one piece that is ordinary task work
and dispatches as a companion task. Runs after 45/44 merge and the fresh
binaries install; reset tolerance is the net, but the criteria above mean it
is never needed blindly.

Walk rulings (2026-08-24): old-location removal is Brian's manual act after
proof; global idea workspaces stay ("the whole point of getting an idea is to
get it down"); moving an idea into a project exists as h9k idea assign, and
spawning a project from an idea is captured as its own new idea.
