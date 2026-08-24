---
project: hall9k
type: bugfix
objective: The test suite stops starting eleven Postgres containers at once - container-backed tests run with bounded parallelism, so a full test run has a predictable memory footprint and stops OOMing the machine and garbling its own connections
criteria:
- Concurrent Postgres containers during a full dotnet test run are bounded to a small fixed number (a shared fixture pool, fewer xUnit collections, or a maxParallelThreads cap - whichever fits the suite's shape best, with the chosen bound and its reasoning recorded in the test project)
- The bound is enforced by configuration or fixture design that new test classes inherit by default, so the twelfth Postgres-backed test class added next month does not silently reopen the problem
- DB-free unit tests keep their full parallelism - the bound applies to container-backed tests only, and total suite wall-clock time is reported before and after so the cost of the bound is known rather than guessed
- A full local dotnet test run's peak container count is observed and stated in the wrap-up, verifying the bound held in practice
- dotnet build and dotnet test pass
---
Origin (2026-08-24, second OOM in two days): a full test gate can start up to
eleven PostgresFixture containers concurrently - twenty Postgres-backed test
classes, ten outside the serialized Hall9kHome collection, xUnit parallel by
default - which decision #80's flake investigation already recorded while
diagnosing connection-class gate failures. Stacked with several agent
sessions and a Parallels VM, the machine OOMed and Brian had to kill nearly
everything; the previous day's OOM was the same recipe without the VM. The
same investigation named Docker daemon contention under simultaneous
container starts as the likelier source of the connection flakes backlog 53
now retries around, so bounding the parallelism attacks both failure modes
with one change: predictable memory, fewer garbled connections.

Relationship: machinery-hygiene family; reduces the load 53's retry exists to
absorb rather than replacing it.
