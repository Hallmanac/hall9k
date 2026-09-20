# Hall9k.Tests — tiers, traits, and what a filter can drop

Two tiers live in this project (AGENTS.md, "Tests"): a **unit** tier that touches no database —
aggregates through `Apply`, projections through a `FakeEvent<T>` stub, source-scanning guards — and
an **integration** tier backed by a real Postgres in Testcontainers. `dotnet test` with no filter
runs both, and that is the only run that proves anything about a branch.

Four xUnit traits exist so a narrower run can leave a tier out on purpose. All four are `Category`
values, because that is what `dotnet test --filter` reads, and none is decoration: each one is
the handle a real gate command already uses.

## `Category=RequiresDocker`

Carried by every class that takes `IClassFixture<PostgresFixture>` (or otherwise needs the Docker
daemon). CI's windows-latest leg runs `--filter "Category!=RequiresDocker"` because Windows runners
cannot run Linux containers; its ubuntu-latest leg runs everything. `PostgresFixture` itself, and
`CrossProcessContainerGate` beside it, document how many containers that tier is allowed to have
alive at once (four, machine-wide) and why the bound is where it is.

## `Category=PublishesBinary`

Carried by the two classes that shell out to a real `dotnet publish` —
`PublishExcludesDevelopmentSettingsTests` (the daemon's Development settings file must not ship) and
`PublishStampsInformationalVersionTests` (an `InformationalVersion` override must survive into the
binary). They exist because only a real publish exercises the MSBuild items and the SDK ordering
each one is a net for; everything else about the install path is covered without one.

They are the most expensive tests in the suite by wall-clock, so a **cycle** gate may drop them:

    dotnet test --filter "Category!=PublishesBinary"

The mandatory final full pass never does. A regression they catch reaches a tagged release
otherwise, which is the failure that put them here in the first place.

Both also sit in the `[Collection("PublishesBinary")]` collection, and that part is a correctness
requirement rather than throughput bookkeeping. Each publish runs `--no-restore` against the
`obj/` the build preceding `dotnet test` already wrote — that is what makes a dropped NuGet
connection unable to fail them (PR #274's ubuntu leg and the Mac's 2026-09-07 21:25 EDT failure
were both a restore reaching the network mid-test) — and reading that assets file means publishing
against the repository's own `obj/Release` rather than a private intermediate tree, which two
concurrent publishes would then be writing at once. The collection is what keeps them one at a
time; `PublishLaneGuardTests` fails the build if a third publish test is ever added without both
attributes. A budget miss under load is thrown as `PublishBudgetExceededException`, which names the
elapsed publish time and the dotnet-family process count it saw and classifies as an
infrastructure-class timeout rather than a product assertion (`GateInfrastructureFailureClassifier`).

## Redirecting the platform home and the database connection string

`PlatformPaths.Home` and `Hall9kDatabase.Resolve` each consult a flow-scoped `AsyncLocal` override
before their own `HALL9K_HOME`/`HALL9K_CONNECTION_STRING` environment variable (Decisions Log
PLACEHOLDER-98484f36), so redirecting either one for a test no longer means racing every other test
that also redirects it — the override lives on that test's own async flow, invisible to every other
flow running in parallel in the same process. Use `Hall9k.Tests.TestSupport.ScopedTestHome`:

- `private readonly ScopedTestHome _home = new();` — a fresh temporary directory, opened from a
  constructor or a field initializer, disposed (directory deleted, override restored) from the
  class's own `Dispose`/`DisposeAsync`.
- `new ScopedTestHome(postgres.ConnectionString)` — the same, plus redirecting the connection string
  to a `PostgresFixture`'s own container for the scope's whole lifetime.
- `using ScopedConnectionString scope = new(postgres.ConnectionString);` — a narrower, one-off swap
  of only the connection string, opened inside a single test or helper method, for a class whose own
  `ScopedTestHome` is already open elsewhere and must not have its home swapped out from under it for
  the swap's duration.

**Never** open either type inside an `async Task InitializeAsync()` (xUnit's `IAsyncLifetime`
method): `await` unwinds the `ExecutionContext` a callee mutated back to what its caller held once
that callee's own task completes, so an override set there is already gone by the time the test
method itself runs. A constructor or a field initializer is a plain synchronous call, mutating the
same context the test method goes on to run in, which is why it works. A test never sets
`HALL9K_HOME`/`HALL9K_CONNECTION_STRING` directly at all, except to hand `HALL9K_HOME` to a real
child process it spawns (which still needs the literal environment variable, since a child inherits
its parent's environment, not its parent's `AsyncLocal` state).

`HomeEnvironmentIsolationTests` enforces two rules over the whole test project: no class writes
either variable directly outside `[Collection("Environment")]` (below), and no class opens either
scope type inside `InitializeAsync`.

## `[Collection("Environment")]` / `Category=Environment`

The one collection left for a process-wide environment variable, for whichever
one has no flow-scoped alternative: the claude path
(`HALL9K_CLAUDE_PATH`), a `Hall9k__*` operating setting, the MSBuild node-reuse flag
(`MSBUILDDISABLENODEREUSE`), and a couple of narrower ones (`CLAUDE_PID`,
`CLAUDE_CODE_SESSION_ID`). `Hall9k.Tests.Fakes.EnvironmentVariableScope` is the shared save/restore
helper for this category; every caller still needs `[Collection("Environment")]` +
`[Trait("Category", "Environment")]` on its own class. `HomeEnvironmentIsolationTests` mechanically
guards only the `HALL9K_HOME`/`HALL9K_CONNECTION_STRING` shape of this rule, the same "carries both
attributes" check `PublishLaneGuardTests` enforces for `PublishesBinary` — a class that writes some
other process-wide variable directly (`HALL9K_CLAUDE_PATH`, a `Hall9k__*` setting,
`MSBUILDDISABLENODEREUSE`) without both attributes is not caught by any scan, the same
judgment-call basis `[Collection("RealProcessSpawn")]` membership already rests on below, not a
mechanically-enforced one. A class that needs a genuinely process-wide variable with no test-unique
alternative (unlike a Jira credential, which can just use its own class-unique `HALL9K_TEST_*` name
instead of the shared production one) belongs here; a class that only touches
`HALL9K_HOME`/`HALL9K_CONNECTION_STRING` belongs on `ScopedTestHome`/`ScopedConnectionString` above
instead, never in this collection.

**Renamed from `Category=Hall9kHome`.** A project's own `--verify-gate-filter` that still names the
retired `Category=Hall9kHome` trait (`h9k project set <project> --verify-gate-filter`) has to be
re-pointed to `Category=Environment` when this merges: the old trait matches nothing once it is
gone, so the clause naming it silently becomes a no-op — the fast gate's exclusion stops excluding
anything and a host-coupled gate built from it stops selecting anything at all — rather than an
error a project owner would notice.

## `[Collection("RealProcessSpawn")]`

Carried by every class whose tests shell out to real `git` or `dotnet` subprocesses heavily enough
to contend with `ProcessManagerParityTests`' own nested process spawn and teardown for the host
runner's process-creation throughput (PLAN.md §16 PLACEHOLDER-f70cc244; three windows-latest
failures on 2026-09-10, none touching `ProcessManagement`, traced to exactly this contention). The
same idea as `[Collection("PublishesBinary")]` above, not the same mechanism: `PublishesBinary`
membership is guarded mechanically by `PublishLaneGuardTests` because every caller goes through one
named helper, `PublishTestSupport.RunPublishAsync`. There is no equivalent single call site for
"spawns real processes heavily enough to matter": `GitDescribedVersionTests` spawns `git` once and
stays out; `GitWorktreeManagerTests` and `Hall9k.Tests.Cli.RepoMaterialiserTests` spawn it dozens of
times per test and join, so membership here is a judgment call recorded in the Decisions Log entry
above, not a mechanically-enforced one, and there is no guard test for it.

Every class in this collection also carries `[Trait("Category", "RealProcessSpawn")]` (task:
host-coupled tests run in their own gate once per task, never in parallel with another run's copy
— #225), the same "carries both attributes" shape `PublishesBinary` already has:
`--verify-gate-filter` selects a host-coupled gate by `Category`, and a bare `[Collection]` gives
`dotnet test --filter` nothing to match. `h9k project set --verify-gate-filter` can fold this trait
into a project's own host-coupled filter expression alongside `RequiresDocker`/`PublishesBinary`.

## `Hall9k.Tests.LockHolder`

Not a third tier: a standalone executable `CrossProcessContainerGateTests` launches and kills to
prove permit reclaim against a real process death (PLAN.md §16 #132). It carries no tests of its
own.

## Capturing output, and everything else the process shares

Three pieces of state in the test process belong to no single test: the two `Console` writers,
`Spectre.Console.AnsiConsole.Console`, and the ambient `CultureInfo`. xUnit runs distinct
collections in parallel inside one process, so a test that swaps any of them decides what every
other test running at that moment sees — and for the two `Console` writers and `AnsiConsole`, the
save-swap-restore idiom cannot fix that, because the redirect it installs is process-wide for as
long as it is installed. (`CultureInfo` is the exception, and the bullet below says why it is
guarded anyway.) Capture through the
`TestSupport` helpers instead (PLAN.md §16 #220):

- `ScopedConsoleCapture.StandardError()` / `.StandardOutput()` — what this test's own async flow
  wrote to `Console.Error` / `Console.Out`, and nothing else.
- `ScopedAnsiConsoleCapture.CaptureAsync(...)` / `.Capture(...)` — what this flow rendered through
  `AnsiConsole`, with Spectre's markup already consumed, at a width wide enough that nothing wraps
  mid-phrase (or a width you pass, when wrapping is the subject).
- `CultureScope.Run(...)` / `.RunToCompletion(...)` — a body under a named culture, on a thread of
  the case's own, so there is no restore to omit and nothing left behind. `CurrentCulture` is the
  one of the three that does flow with the test's own execution context and unwinds again with it,
  so a correctly written set-and-restore around it is sound; the guard covers the marker anyway,
  because the same marker covers `DefaultThreadCurrentCulture`, which is genuinely process-wide
  with no flow to unwind it, and because a helper cannot be written with the restore left out.

`ProcessWideStateGuardTests` fails the build for any file in `tests/` that mutates one of the three
directly, and names the helper to use instead. Two adjacent rules live elsewhere for reasons of
their own: `HALL9K_HOME` and `HALL9K_CONNECTION_STRING` are `HomeEnvironmentIsolationTests`' surface,
answered with `ScopedTestHome`/`ScopedConnectionString` (see "Redirecting the platform home and the
database connection string" above) rather than the `[Collection("Environment")]` serial lane the
rest of this project's residual process-wide variables still need, since both production readers
consult a flow-scoped override before their environment variable rather than re-reading the
environment variable with nowhere per-test to be scoped to; and `PostgresFixture`'s own
container-gate wait notice goes to a `TraceSource` (`CrossProcessContainerGate.WaitNotice`)
precisely so that no capture can pick it up.
