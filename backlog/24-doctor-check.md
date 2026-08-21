---
project: hall9k
type: feature
objective: Teach the CLI to diagnose its own database situation - a doctor check that answers what is wrong and what to do, the same way forever
criteria:
- The first command that needs a database and cannot reach one runs the check instead of failing raw; a standalone h9k doctor runs it on demand
- The check answers four questions in order and stops at the first failing one: (1) is a connection string configured at all - if not, that is the entire answer; (2) is it reachable - distinguishing nothing-listening-on-that-port from reached-it-credentials-rejected, which are completely different fixes; (3) is the schema present and current - mostly an offer, since Marten creates its own tables ("shall I set that up?"); (4) only if nothing was configured - what is available: a running container runtime, a native Postgres on the standard port, and above all a STOPPED hall9k-postgres container from a previous session ("your database exists, it is just not running" is a one-line fix)
- Every answer is a teaching message naming the fix, per the CLI standards; no stack traces, no conflated causes
- The check lives in the CLI and works while the daemon is down (a raw Npgsql connection attempt, cheap enough that the thin-CLI rule survives)
- Install remains boring per decision #58: no database prompt, no provisioning, no silent guessing of a local default - the check does the teaching at the moment the user can act on it
- Connection-string configuration gets a documented home and precedence order (environment variable, then a platform config file, then any per-project override), resolving open-decision row 29 - and the decisions log records the choice
- dotnet build and dotnet test pass
---
This is decisions #57-#58 (Postgres is a connection string; install stays boring, a
doctor check does the teaching) promoted to a near-term task - the decisions arrived
inside the P2P corpus but are not P2P work at all: they extend backlog 12's
install-registers-nothing principle to the database. Read the #57/#58 entries in
PLAN.md section 16 for the full rationale, including the rejected alternatives
(embedded Postgres, install-time prompting, silent defaults).

Design constraints:
- The same check works forever, not only at onboarding: database moved, container
  stopped, credentials rotated, laptop reimaged - same diagnostic, same teaching.
- Remote Postgres is supported but never suggested: it quietly forfeits the
  local-first offline promise, and the docs must say why (decision #57).
- Open-decision row 28 (Aspire dev-loop Postgres vs docker-compose installed mode)
  is decided BY this task or explicitly left separate - either way, documented.
