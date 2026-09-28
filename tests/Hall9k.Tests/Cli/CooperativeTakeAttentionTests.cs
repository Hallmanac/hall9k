using FluentAssertions;
using Hall9k.Cli.Commands;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Idea 202383dc, item 5's own timeout wording: no answer within the project's own take-timeout
/// names <c>--force</c> as the way on. Pure and database-free, the same
/// <c>TaskShowCommand.ComposePassageLines</c> idiom this file's own sibling tests already follow.
/// </summary>
public sealed class CooperativeTakeAttentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsOverdue_is_false_within_the_timeout()
    {
        DateTimeOffset requestedAt = Now - TimeSpan.FromMinutes(29);

        CooperativeTakeAttention.IsOverdue(requestedAt, timeoutMinutes: 30, Now).Should().BeFalse();
    }

    [Fact]
    public void IsOverdue_is_true_past_the_timeout()
    {
        DateTimeOffset requestedAt = Now - TimeSpan.FromMinutes(31);

        CooperativeTakeAttention.IsOverdue(requestedAt, timeoutMinutes: 30, Now).Should().BeTrue();
    }

    [Fact]
    public void ComposeStatusLine_names_force_once_overdue_for_the_requester()
    {
        string line = CooperativeTakeAttention.ComposeStatusLine(
            "28b19893", "Add rate limiting", "node abcd1234", "Picking this back up.", isHolder: false,
            overdue: true, timeoutMinutes: 30);

        line.Should().Contain("--force");
        line.Should().Contain("no answer");
        line.Should().Contain("28b19893");
        line.Should().NotContain("h9k task grant");
    }

    [Fact]
    public void ComposeStatusLine_never_offers_force_to_the_requester_before_the_timeout()
    {
        string line = CooperativeTakeAttention.ComposeStatusLine(
            "28b19893", "Add rate limiting", "node abcd1234", "Picking this back up.", isHolder: false,
            overdue: false, timeoutMinutes: 30);

        line.Should().NotContain("--force");
        line.Should().NotContain("h9k task grant");
        line.Should().NotContain("h9k task refuse");
    }

    [Fact]
    public void ComposeStatusLine_names_grant_and_refuse_levers_for_the_holder()
    {
        string line = CooperativeTakeAttention.ComposeStatusLine(
            "28b19893", "Add rate limiting", "node abcd1234", "Picking this back up.", isHolder: true,
            overdue: false, timeoutMinutes: 30);

        line.Should().NotContain("--force");
        line.Should().Contain("h9k task grant 28b19893");
        line.Should().Contain("h9k task refuse 28b19893");
    }

    [Fact]
    public void ComposeStatusLine_never_offers_force_to_the_holder_once_overdue()
    {
        string line = CooperativeTakeAttention.ComposeStatusLine(
            "28b19893", "Add rate limiting", "node abcd1234", "Picking this back up.", isHolder: true,
            overdue: true, timeoutMinutes: 30);

        line.Should().NotContain("--force");
        line.Should().Contain("h9k task grant 28b19893");
        line.Should().Contain("h9k task refuse 28b19893");
    }

    [Fact]
    public void ComposeTaskShowLine_names_force_once_overdue()
    {
        string line = CooperativeTakeAttention.ComposeTaskShowLine("28b19893", overdue: true, timeoutMinutes: 30);

        line.Should().Contain("--force");
    }

    [Fact]
    public void ComposeTaskShowLine_names_grant_and_refuse_before_the_timeout()
    {
        string line = CooperativeTakeAttention.ComposeTaskShowLine("28b19893", overdue: false, timeoutMinutes: 30);

        line.Should().NotContain("--force");
        line.Should().Contain("h9k task grant 28b19893");
    }
}
