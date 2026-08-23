---
project: hall9k
type: bugfix
objective: A verification gate that fails on infrastructure - container startup, connection loss, port collision - is retried once and recorded as what it was, so an agent's finished work is never failed by a Postgres container dying under the test run
criteria:
- The verification runner classifies a failed test gate's output conservatively: connection-class signatures (Npgsql connection refused/reset/timeout, Testcontainers startup failure, the SSLRequest handshake mismatch) mark the failure as infrastructure; anything carrying a test assertion stays a real failure - the never-guess rule, applied exactly as backlog 40 applied it to budget exhaustion
- An infrastructure-classified gate failure retries the gate once, in place, without failing the run or spending any budget; the retry and its cause are recorded on the run stream so the record says the flake happened
- A second consecutive infrastructure failure fails the run honestly with the classification in the reason, so a genuinely broken environment surfaces instead of looping
- The window that produces the collisions shrinks where cheap: each run's Testcontainers instance binds an ephemeral port chosen by the OS (never a fixed mapping), and if the collision source is identified during this work it is named in the wrap-up
- dotnet build and dotnet test pass
---
Origin (2026-08-23, twice in one afternoon, same lane): task 40's follow-up
failed gate 'test' first with 7 failures all reading "Npgsql: Received unknown
response H for SSLRequest", then after retry with 4 failures all reading
"Failed to connect to 127.0.0.1:55821 / exception while reading from stream".
Both were the Testcontainers Postgres dying or losing its port under the
suite, both burned a full lap plus an orchestrator diagnosis and retry, and
both times the agent's actual work was fine. The concurrency ceiling keeps
gate runs mostly serial today, so the collision window will only grow as
concurrency does. Detection is deterministic (the signatures are unambiguous
connection-class errors), so this is platform code per the recipe line, not
agent judgment.
