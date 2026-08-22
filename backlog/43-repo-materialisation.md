---
project: hall9k
type: feature
objective: h9k materialises a project's repo on demand - bare clone, corrected refspec, worktree-ready - so a fresh node goes from nothing to doing work with documented commands
criteria:
- An explicit command (h9k project materialise <name> or equivalent) bare-clones the project's configured remote into a default path under ~/.hall9k/repos/ keyed by project, overridable by flag or prompt; one plain line reports what it did
- The clone's fetch refspec maps remote branches into refs/remotes/origin/ so --no-track branch creation from origin/main, remote fetching, and pushing behave exactly as in an ordinary checkout; core.longpaths is set on Windows
- Materialisation is idempotent - against an already-materialised project it reports and exits, never re-clones
- Worktree creation works against the materialised bare repo with no further setup; credentials are the machine's own git credential helper (gh auth setup-git named in the teaching output when auth fails)
- The second-node preflight (git, gh authenticated, Docker, claude CLI logged in) is NOT built here - the criterion is a documented pointer: this command's failure messages name the missing tool and backlog 24's doctor owns the full check
- A second machine can go from bootstrap (backlog 42) through materialisation to claiming and completing a task against the shared store, using only documented commands - recorded as the acceptance walkthrough
- dotnet build and dotnet test pass
---
Gap 3 of backlog/IDEA-installation-and-materialisation.md, plus the sitting's
rulings: explicit command first (lazy-on-claim waits for P2P), credential helper
assumed, long paths proven by CI. The shared-store connection itself is backlog
24's connection-string precedence; the store ruling (tower hosts) is in the idea
doc's addendum and becomes a decisions-log entry with this work.

DRAFT - Brian reviews criteria before publish.
