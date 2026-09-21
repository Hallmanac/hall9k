using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The row <c>h9k project show</c> prints for <c>--qa-review-drive</c> (idea b9b09779, piece
/// 2). Off is the default, so the row's job is mostly reassurance
/// — nothing on this machine gets started by a review unless somebody said so — and it still
/// has to tell the untouched default apart from a choice somebody made, the same distinction
/// every other settings row draws (Decisions Log #161).
/// </summary>
public sealed class ProjectQaReviewDriveTests
{
    [Fact]
    public void Off_and_never_chosen_reads_as_the_untouched_default()
    {
        string row = ProjectShowCommand.QaReviewDriveRow(
            Project(), ReviewDriveSetting.UnrecordedFor(ReviewPersona.Qa));

        row.Should().Contain("off");
        row.Should().Contain("nothing recorded here");
        row.Should().Contain("never launches the product");
        row.Should().Contain("--qa-review-drive on", "the row names the command that reverses it");
    }

    [Fact]
    public void Off_chosen_on_purpose_says_so_rather_than_reading_as_the_default()
    {
        string row = ProjectShowCommand.QaReviewDriveRow(
            Project(), new ReviewDriveSetting(ReviewPersona.Qa, Enabled: false, Recorded: true));

        row.Should().Contain("explicit");
        row.Should().NotContain("nothing recorded here");
    }

    [Fact]
    public void On_says_what_a_review_may_now_do_on_this_machine()
    {
        string row = ProjectShowCommand.QaReviewDriveRow(
            Project(), new ReviewDriveSetting(ReviewPersona.Qa, Enabled: true, Recorded: true));

        row.Should().StartWith("on ");
        row.Should().Contain("ephemeral port");
        row.Should().Contain("A project with no run skill drives nothing regardless.");
        row.Should().Contain("--qa-review-drive off");
    }

    private static ProjectDetails Project() => new()
    {
        Id = DomainId.New(),
        Name = "acme",
        BaseBranch = "main",
    };
}
