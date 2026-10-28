using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The row analysis-only closeout shows for what it reported: which go it names when a report holds
/// several findings at once, and that a report quoting nothing a reviewer wrote renders with none of it.
/// </summary>
public sealed class CloseoutReportAttentionTests
{
    private const string Id = "0e866341";
    private const string ReviewUrl = "https://github.com/x/y/pull/7#pullrequestreview-42";

    private static CloseoutThreadReport Threads(string status = "Assessed") =>
        new(status, 3, 2, 1, 0, "/run/closeout-triage-0123456789ab.md", null);

    [Fact]
    public void A_changes_requested_review_outranks_threads_and_checks_because_only_the_clear_lever_dispatches_it()
    {
        CloseoutReport report = CloseoutReport.Nothing with
        {
            ChangesRequestedReviews = [ReviewUrl],
            Threads = Threads(),
            FailingChecks = ["build"],
        };

        TaskAttention row = CloseoutReportAttention.Compose(report, Id);

        row.NeedsYou.Should().BeTrue();
        row.Lever.Should().Be($"h9k task revise {Id} --clear-interactive-mode");
        row.Cause.Should().Contain(ReviewUrl).And.Contain("triaged: 2 fix, 1 decline, 0 route").And.Contain("build");
    }

    [Fact]
    public void Threads_outrank_checks_and_checks_outrank_nothing_else_in_the_order_closeout_would_have_acted()
    {
        CloseoutReportAttention.Lever(CloseoutReport.Nothing with { Threads = Threads(), FailingChecks = ["build"] }, Id)
            .Should().Be($"h9k pr resolve {Id}");
        CloseoutReportAttention.Lever(CloseoutReport.Nothing with { FailingChecks = ["build"] }, Id)
            .Should().Be($"h9k pr resolve {Id} --checks");
        CloseoutReportAttention.Lever(CloseoutReport.Nothing with { Conflicting = true }, Id)
            .Should().Be($"h9k pr resolve {Id} --rebase");
        CloseoutReportAttention.Lever(CloseoutReport.Nothing with { StackedParent = "its parent merged" }, Id)
            .Should().Be($"h9k task revise {Id} --clear-interactive-mode");
        CloseoutReportAttention.Lever(CloseoutReport.Nothing with { MergeHeld = true }, Id)
            .Should().Be($"h9k task revise {Id} --clear-interactive-mode");
    }

    [Theory]
    [InlineData("Running", "a read-only session is triaging them")]
    [InlineData("Failed", "no triage: the triage session wrote no report")]
    [InlineData("PastCap", "no triage ran: this task has already used its allowance of 3 triage sessions")]
    public void A_thread_report_without_a_triage_says_why_there_is_none(string status, string expected)
    {
        string reason = status == "PastCap"
            ? "this task has already used its allowance of 3 triage sessions"
            : "the triage session wrote no report";
        CloseoutReport report = CloseoutReport.Nothing with
        {
            Threads = new CloseoutThreadReport(status, 3, 0, 0, 0, null, reason),
        };

        TaskAttention row = CloseoutReportAttention.Compose(report, Id);

        row.Cause.Should().Contain(expected).And.Contain("3 unresolved review threads");
        row.Lever.Should().Be($"h9k pr resolve {Id}");
    }

    [Fact]
    public void Two_reports_with_the_same_findings_have_the_same_key_whatever_order_they_list_them_in()
    {
        CloseoutReport first = CloseoutReport.Nothing with { FailingChecks = ["build", "lint"] };
        CloseoutReport second = CloseoutReport.Nothing with { FailingChecks = ["lint", "build"] };

        first.Key().Should().Be(second.Key());
        first.Key().Should().NotBe(CloseoutReport.Nothing.Key());
        CloseoutReport.Nothing.IsEmpty().Should().BeTrue();
        first.IsEmpty().Should().BeFalse();
    }
}
