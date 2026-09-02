using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="SpendPressure.ReadAsync"/> reconciles a published <see cref="NodeDispatchLoad"/>
/// row against this shell's freshly-resolved config the same way for both halves of the spend
/// setting — the budget and the period it resets on — because a daemon that has never had a
/// budget still publishes a compiled-default period on every sweep (independent pre-PR review,
/// cycle 7, adversarial lens: trusting that default whenever it was merely non-empty, rather than
/// gating it on the same <c>BudgetIsEnforced</c> flag the budget itself uses, reported a window
/// nobody configured and the daemon was not enforcing).
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class SpendPressureTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] EnvironmentVariables =
    [
        "Hall9k__SpendBudgetTokens",
        "Hall9k__SpendPeriod",
    ];

    private readonly string _home = Path.Combine(Path.GetTempPath(), $"h9k-spend-pressure-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
    private readonly Dictionary<string, string?> _previous =
        EnvironmentVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        foreach ((string name, string? value) in _previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    private DocumentStore Store() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    /// <summary>
    /// A daemon that has never had a budget still publishes its compiled-default period
    /// ("week") on every sweep, alongside a null budget. This shell resolves a genuinely
    /// configured budget and period ("day") that daemon has never seen. The period this shell
    /// reports must be its own configured one, not the unconfirmed daemon's compiled default —
    /// otherwise the calibration line (run, observe a real burn, set the budget under it) sums
    /// the wrong window entirely.
    /// </summary>
    [Fact]
    public async Task An_unconfirmed_budget_reports_this_shells_own_period_not_the_daemons_stale_default()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("HALL9K_HOME", _home);
        Environment.SetEnvironmentVariable("Hall9k__SpendBudgetTokens", "5000000");
        Environment.SetEnvironmentVariable("Hall9k__SpendPeriod", "day");

        using DocumentStore store = Store();
        // This class's tests share one PostgresFixture container: reset first, since both seed
        // a NodeDispatchLoad for this same machine and a leftover row from a sibling test would
        // make DispatchPressure.ReadFreshMeasurementAsync's freshest-row pick ambiguous.
        await store.Advanced.ResetAllData(cts.Token);
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new NodeDispatchLoad
            {
                Id = Guid.NewGuid(),
                MachineName = Environment.MachineName,
                LiveRuns = 0,
                MaxConcurrentRuns = 3,
                ObservedAt = Now,
                SpendBudgetTokens = null,
                SpendPeriod = SpendPeriod.Week.Value,
            });
            await seed.SaveChangesAsync(cts.Token);
        }

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(cts.Token);
        report.SpendPeriod.Value.Should().Be("day", "the env var this test set is what should resolve");

        await using IQuerySession query = store.QuerySession();
        SpendPressure spend = await SpendPressure.ReadAsync(query, report, Now, cts.Token);

        spend.BudgetIsEnforced.Should().BeFalse("the published row's own budget is null — nothing is enforcing yet");
        spend.Period.Should().Be(
            "day",
            "an unconfirmed budget must read this shell's own configured period, not the daemon's " +
            "compiled-default 'week' it publishes even while unbudgeted");
    }

    /// <summary>
    /// A budget change note already exists for the budget half of this setting pair
    /// (<see cref="SpendPressure"/>'s own <c>PendingChangeNote</c>); the period half needs the
    /// identical treatment. Here the enforced budget and this shell's configured budget agree, so
    /// the note must still fire on the period disagreeing alone — the exact gap the adversarial
    /// finding named (a period-only config change showed no disagreement at all).
    /// </summary>
    [Fact]
    public async Task A_period_only_config_change_still_surfaces_the_pending_change_note()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("HALL9K_HOME", _home);
        Environment.SetEnvironmentVariable("Hall9k__SpendBudgetTokens", "5000000");
        Environment.SetEnvironmentVariable("Hall9k__SpendPeriod", "day");

        using DocumentStore store = Store();
        // This class's tests share one PostgresFixture container: reset first, since both seed
        // a NodeDispatchLoad for this same machine and a leftover row from a sibling test would
        // make DispatchPressure.ReadFreshMeasurementAsync's freshest-row pick ambiguous.
        await store.Advanced.ResetAllData(cts.Token);
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new NodeDispatchLoad
            {
                Id = Guid.NewGuid(),
                MachineName = Environment.MachineName,
                LiveRuns = 0,
                MaxConcurrentRuns = 3,
                ObservedAt = Now,
                SpendBudgetTokens = 5_000_000,
                SpendPeriod = SpendPeriod.Week.Value,
            });
            await seed.SaveChangesAsync(cts.Token);
        }

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        SpendPressure spend = await SpendPressure.ReadAsync(query, report, Now, cts.Token);

        spend.BudgetIsEnforced.Should().BeTrue("the published row's budget matches what's confirmed enforced");
        spend.Period.Should().Be("week", "the enforced period is the daemon's own published one, still in force");
        spend.SummaryLine.Should().Contain(
            "differs from what the daemon is enforcing",
            "the budgets agree but the periods do not, and that disagreement must still be named");
    }
}
