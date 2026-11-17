using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The join behind the per-account spend lines on <c>h9k config show</c>: a run's spend counts toward the
/// account its own stream froze at dispatch, and everything else in the period total is unattributed.
/// </summary>
public sealed class PeriodSpendByAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_run_counts_toward_its_frozen_account_and_the_rest_is_unattributed()
    {
        Guid first = DomainId.New();
        Guid second = DomainId.New();
        Guid runOnFirst = DomainId.New();
        Guid runOnSecond = DomainId.New();
        Guid runOnDefault = DomainId.New();
        Guid runWithNoRecord = DomainId.New();

        TokensRecorded[] runEvents =
        [
            Tokens(runOnFirst, input: 100, cacheRead: 20, cacheWrite: 3),
            Tokens(runOnFirst, input: 1_000),
            Tokens(runOnSecond, input: 2_000),
            Tokens(runOnDefault, input: 300),
            Tokens(runWithNoRecord, input: 40),
        ];
        RunClaudeCodeAccountRecorded[] records =
        [
            new(runOnFirst, first, Now),
            new(runOnSecond, second, Now),
            new(runOnDefault, null, Now),
        ];
        // The run spend above plus 500 tokens recorded outside any run.
        long periodTotal = 1_123 + 2_000 + 300 + 40 + 500;

        PeriodSpendByAccount split = PeriodSpendByAccount.Split(runEvents, records, periodTotal);

        split.ByAccount.Should().Equal(
            new PeriodAccountSpend(second, 2_000),
            new PeriodAccountSpend(first, 1_123),
            new PeriodAccountSpend(null, 300));
        split.UnattributedInputTokens.Should().Be(540, "the unrecorded run's 40 and the 500 spent outside a run");
        (split.ByAccount.Sum(entry => entry.TotalInputTokens) + split.UnattributedInputTokens).Should().Be(periodTotal);
    }

    [Fact]
    public void A_run_recorded_twice_keeps_the_latest_record_the_way_its_projection_does()
    {
        Guid run = DomainId.New();
        Guid latest = DomainId.New();

        PeriodSpendByAccount split = PeriodSpendByAccount.Split(
            [Tokens(run, input: 10)],
            [new(run, latest, Now.AddMinutes(5)), new(run, DomainId.New(), Now)],
            periodTotal: 10);

        split.ByAccount.Should().Equal(new PeriodAccountSpend(latest, 10));
        split.UnattributedInputTokens.Should().Be(0);
    }

    [Fact]
    public void The_lines_name_each_account_and_always_print_the_unattributed_line()
    {
        Guid bound = DomainId.New();
        Guid removed = DomainId.New();
        ConnectionDetails connection = new()
        {
            Id = bound,
            Provider = WorkItemProvider.ClaudeCode,
            ExternalAccountId = "/Users/someone/.claude-alt",
            ClaudeCodeEmail = "alt@example.com",
        };
        PeriodSpendByAccount split = new(
            [new(bound, 2_000), new(null, 300), new(removed, 7)],
            UnattributedInputTokens: 0);

        // The token counts are formatted with the current culture's group separator, so pin one.
        CultureScope.Run("en-US", () =>
        {
            IReadOnlyList<string> lines = ConfigShowCommand.AccountSpendLines(split, [connection]);

            lines.Should().HaveCount(5);
            lines[1].Should().Be("  alt@example.com (/Users/someone/.claude-alt): 2,000 tokens");
            lines[2].Should().Be("  the default Claude Code login: 300 tokens");
            lines[3].Should().Contain(removed.ToString()).And.EndWith(": 7 tokens");
            lines[4].Should().StartWith("  unattributed (")
                .And.Contain("other nodes' runs")
                .And.Contain("spend outside a run")
                .And.EndWith(": 0 tokens");
        });
    }

    private static TokensRecorded Tokens(Guid runId, long input, long cacheRead = 0, long cacheWrite = 0) =>
        new(runId, input, OutputTokens: 5, CostUsd: null, Now, cacheRead, cacheWrite, AgentModel.Sonnet);
}
