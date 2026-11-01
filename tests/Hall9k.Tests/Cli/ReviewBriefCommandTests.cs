using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run.Projections;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>The pure half of <c>h9k review brief</c>: what the briefing counts as a finding left unfixed.</summary>
public sealed class ReviewBriefCommandTests
{
    [Fact]
    public void Findings_not_fixed_counts_every_residual_the_branch_carries_to_the_pull_request()
    {
        RunDetails details = new()
        {
            ReviewResidualsUnfixed = 1,
            ReviewResidualsRideAlong = 2,
            ReviewResidualsRouted = 3,
            ReviewResidualsRoutingFailed = 4,
            ReviewResidualsFixed = 9,
        };

        ReviewBriefCommand.FindingsNotFixed(details).Should().Be(10, "fixed findings are not residuals, everything else is");
    }
}
