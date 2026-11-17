using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The per-account spend split over recorded events in a real store: runs frozen on two claude-code
/// connections and on the default login, a run with no account record, and spend recorded outside a run,
/// read the way <c>h9k config show</c> reads them, against the same period total <see cref="PeriodSpend"/>
/// gives every spend gate.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PeriodSpendByAccountStoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Each_account_line_and_the_unattributed_line_add_up_to_the_period_total()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);

        Guid first = DomainId.New();
        Guid second = DomainId.New();
        Guid runOnFirst = DomainId.New();
        Guid runOnSecond = DomainId.New();
        Guid runOnDefault = DomainId.New();
        Guid runWithNoRecord = DomainId.New();
        Guid staleRun = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Store(
                ClaudeCodeConnection(first, "/Users/someone/.claude-first", "first@example.com"),
                ClaudeCodeConnection(second, "/Users/someone/.claude-second", "second@example.com"));

            session.Events.Append(runOnFirst, new RunClaudeCodeAccountRecorded(runOnFirst, first, Now));
            session.Events.Append(runOnFirst, new TokensRecorded(runOnFirst, 100, 5, null, Now, 20, 3, AgentModel.Opus));
            session.Events.Append(runOnFirst, new TokensRecorded(runOnFirst, 1_000, 5, null, Now, Model: AgentModel.Sonnet));
            session.Events.Append(runOnSecond, new RunClaudeCodeAccountRecorded(runOnSecond, second, Now));
            session.Events.Append(runOnSecond, new TokensRecorded(runOnSecond, 2_000, 5, null, Now, Model: AgentModel.Opus));
            session.Events.Append(runOnDefault, new RunClaudeCodeAccountRecorded(runOnDefault, null, Now));
            session.Events.Append(runOnDefault, new TokensRecorded(runOnDefault, 300, 5, null, Now, Model: AgentModel.Opus));
            session.Events.Append(runWithNoRecord, new TokensRecorded(runWithNoRecord, 40, 5, null, Now));
            // Spend before the period counts toward nothing, frozen account or not.
            session.Events.Append(staleRun, new RunClaudeCodeAccountRecorded(staleRun, first, Now.AddDays(-30)));
            session.Events.Append(staleRun, new TokensRecorded(staleRun, 9_999, 5, null, Now.AddDays(-30)));

            Guid taskId = DomainId.New();
            session.Events.Append(taskId, new PublicationTokensRecorded(taskId, 5, 1, null, Now));
            Guid planId = DomainId.New();
            session.Events.Append(planId, new PlanTokensRecorded(planId, 7, 1, null, Now));
            Guid courierRun = DomainId.New();
            session.Events.Append(courierRun, new CourierTokensRecorded(courierRun, 11, 1, null, Now));
            session.Store(new PurgedSpendRecord(DomainId.New(), Now, 13, AgentModel.Opus));

            await session.SaveChangesAsync(cts.Token);
        }

        DateTimeOffset periodStart = Now.AddDays(-1);
        await using IQuerySession query = store.QuerySession();
        PeriodSpend total = await PeriodSpend.ReadAsync(query, periodStart, cts.Token);
        PeriodSpendByAccount split = await PeriodSpendByAccount.ReadAsync(
            query, periodStart, total.TotalInputTokens, cts.Token);

        total.TotalInputTokens.Should().Be(1_123 + 2_000 + 300 + 40 + 5 + 7 + 11 + 13);
        split.ByAccount.Should().Equal(
            new PeriodAccountSpend(second, 2_000),
            new PeriodAccountSpend(first, 1_123),
            new PeriodAccountSpend(null, 300));
        split.UnattributedInputTokens.Should().Be(
            40 + 5 + 7 + 11 + 13,
            "the unrecorded run and every token spent outside a run are unattributed");
        (split.ByAccount.Sum(entry => entry.TotalInputTokens) + split.UnattributedInputTokens)
            .Should().Be(total.TotalInputTokens);
    }

    [Fact]
    public async Task A_session_recorded_between_the_total_and_the_split_never_pushes_the_unattributed_line_below_zero()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid connection = DomainId.New();
        Guid run = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Store(ClaudeCodeConnection(connection, "/Users/someone/.claude-first", "first@example.com"));
            session.Events.Append(run, new RunClaudeCodeAccountRecorded(run, connection, Now));
            session.Events.Append(run, new TokensRecorded(run, 100, 5, null, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        DateTimeOffset periodStart = Now.AddDays(-1);
        await using SpendSnapshot snapshot = await SpendSnapshot.OpenAsync(store, postgres.ConnectionString, cts.Token);
        PeriodSpend total = await PeriodSpend.ReadAsync(snapshot.Session, periodStart, cts.Token);

        // The daemon records a session on the same run between the two reads.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(run, new TokensRecorded(run, 5_000, 5, null, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        PeriodSpendByAccount split = await PeriodSpendByAccount.ReadAsync(
            snapshot.Session, periodStart, total.TotalInputTokens, cts.Token);

        total.TotalInputTokens.Should().Be(100);
        split.ByAccount.Should().Equal(new PeriodAccountSpend(connection, 100));
        split.UnattributedInputTokens.Should().Be(0, "both reads see the one snapshot the total was read on");
    }

    private static ConnectionDetails ClaudeCodeConnection(Guid id, string directory, string email) => new()
    {
        Id = id,
        Provider = WorkItemProvider.ClaudeCode,
        ExternalAccountId = directory,
        ClaudeCodeEmail = email,
        RegisteredAt = Now,
    };
}
