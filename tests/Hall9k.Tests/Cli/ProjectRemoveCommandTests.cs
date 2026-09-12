using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Tasks;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The pure state predicate <c>h9k project remove</c> refuses over (task: a project can be
/// archived, listed as archived, reactivated, and renamed): the four states a task may sit in
/// while its project is archived, and every other state, refused as a state the daemon may still
/// act on. Database-free, the same convention <c>ProjectResolverTests</c>/<c>ProjectSetCommandTests</c>
/// already use for a command's own pure helper logic.
/// </summary>
public sealed class ProjectRemoveCommandTests
{
    [Theory]
    [InlineData("Draft")]
    [InlineData("Published")]
    [InlineData("Done")]
    [InlineData("Abandoned")]
    public void A_task_in_an_inert_state_never_blocks_archiving(string state)
    {
        ProjectRemoveCommand.IsInertUnderArchive((TaskState)state).Should().BeTrue();
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Blocked")]
    [InlineData("Claimed")]
    [InlineData("NeedsHuman")]
    [InlineData("AwaitingAuthor")]
    [InlineData("Failed")]
    public void A_task_the_daemon_may_still_act_on_blocks_archiving(string state)
    {
        ProjectRemoveCommand.IsInertUnderArchive((TaskState)state).Should().BeFalse();
    }
}
