---
project: hall9k
type: bugfix
objective: Make the windows-latest CI leg green by keeping Docker-dependent tests off it
criteria:
- Integration tests that need Docker (Testcontainers Postgres) carry an xUnit trait such as Category=RequiresDocker
- The CI workflow's windows leg filters those tests out; the ubuntu leg still runs the entire suite
- dotnet build and dotnet test pass locally (full suite, Docker available)
---
Main's windows-latest CI has failed since the Testcontainers integration tests arrived:
GitHub's Windows runners cannot run Linux containers, so every PostgresFixture-based test
fails at Docker.DotNet. The fix is exclusion, not emulation.

Pointers: the workflow is .github/workflows/ci.yml (matrix ubuntu-latest/windows-latest).
Integration tests live in tests/Hall9k.Tests/Integration/ and share PostgresFixture.
Prefer a trait on the test classes plus `--filter` on the windows test step. Do not touch
the unit tier or the ubuntu leg. Read AGENTS.md for house style.
