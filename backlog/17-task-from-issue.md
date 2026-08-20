---
project: hall9k
type: feature
objective: Turn an external work item into a task with one command, starting with GitHub issues and shaped so Jira slots in next
criteria:
- h9k task add --from-issue <number-or-url> fetches the GitHub issue via gh from the project's repository, maps title to the objective seed and body to agent context, and records the issue as the task's ExternalReference
- The command opens an interactive gap: acceptance criteria are never invented from an issue body (never-guess rule) - the human supplies or confirms criteria before the task queues, or passes --criteria flags explicitly
- The issue reference renders as a link in h9k task show, and the PR description the daemon generates mentions the source issue so GitHub links the work back
- The import layer is source-agnostic by construction: the fetch-and-map step is a seam (per-source resolver behind a shared shape), so backlog 18's Jira import adds a resolver, not a rewrite
- A closed or missing issue is refused with a self-correcting message; the issue's state is recorded as observed at import time, never assumed current afterward
- dotnet build and dotnet test pass
---
This is SLICE-1 task S1-11. ExternalReference has carried the design slot since the
task model was written; this makes it real. GitHub first because gh is already the
platform's authenticated seam; the resolver seam is the deliberate down payment on
backlog 18 (Jira), which Brian needs close behind.

Design constraints:
- Criteria are the readiness contract; an issue body is context, not criteria. The
  command must make supplying them easy, never automatic.
- No issue-state syncing here: import is a one-time observed snapshot. Mirroring
  and closeout-time updates belong to backlog 18's design space.
