using FluentAssertions;
using Hall9k.Cli.Commands;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The closing line of <c>h9k review resolve --merge-ready</c> on a pr-review task. It may speak only
/// for this command: the findings walk can already have posted, and the daemon finalizes the review
/// rather than completing the task (a full review waits on the pull request).
/// </summary>
public sealed class ReviewResolveClosingLineTests
{
    [Fact]
    public void The_closing_line_disclaims_only_this_commands_posts_and_does_not_claim_completion()
    {
        Guid runId = Guid.Parse("01a0bc05-a960-7657-b708-1aed37b5ec69");

        string line = ReviewResolveCommand.PrReviewResolvedLine(runId);

        line.Should().Be(
            "Run 01a0bc05-a960-7657-b708-1aed37b5ec69 resolved; the daemon finalizes the review from here. "
            + "This command posted nothing to the pull request; anything posted during the findings walk stands.");
        line.Should().NotContain("completes the task");
    }
}
