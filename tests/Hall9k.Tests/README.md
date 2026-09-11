# Hall9k.Tests — tiers, traits, and what a filter can drop

Two tiers live in this project (AGENTS.md, "Tests"): a **unit** tier that touches no database —
aggregates through `Apply`, projections through a `FakeEvent<T>` stub, source-scanning guards — and
an **integration** tier backed by a real Postgres in Testcontainers. `dotnet test` with no filter
runs both, and that is the only run that proves anything about a branch.

Two xUnit traits exist so a narrower run can leave a tier out on purpose. Both are `Category`
values, because that is what `dotnet test --filter` reads, and neither is decoration: each one is
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

## `Hall9k.Tests.LockHolder`

Not a third tier: a standalone executable `CrossProcessContainerGateTests` launches and kills to
prove permit reclaim against a real process death (PLAN.md §16 #132). It carries no tests of its
own.
