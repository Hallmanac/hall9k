using FluentAssertions;
using Hall9k.Cli.Commands;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// A task's <c>linear:ENG-123</c> reference records no workspace, so a key from a different workspace
/// may replace the connection only when no task carries a Linear reference or the human said so.
/// </summary>
public sealed class ConnectionAddLinearWorkspaceTests
{
    private static readonly Uri Registered = new("https://linear.app/hall9k");
    private static readonly Uri Other = new("https://linear.app/other-co");

    [Fact]
    public void A_key_from_another_workspace_is_refused_while_tasks_carry_linear_references()
    {
        string? refusal = ConnectionAddLinearCommand.WorkspaceChangeRefusal(Registered, Other, linkedTasks: 3, confirmed: false);

        refusal.Should().Contain("https://linear.app/hall9k").And.Contain("https://linear.app/other-co")
            .And.Contain("3 task(s)").And.Contain("--change-workspace");
    }

    [Theory]
    [InlineData("https://linear.app/hall9k/", 3, false)]
    [InlineData("https://linear.app/HALL9K", 3, false)]
    [InlineData("https://linear.app/other-co", 0, false)]
    [InlineData("https://linear.app/other-co", 3, true)]
    public void A_rotated_key_an_unlinked_install_or_a_confirmed_change_goes_through(string proven, int linkedTasks, bool confirmed) =>
        ConnectionAddLinearCommand.WorkspaceChangeRefusal(Registered, new Uri(proven), linkedTasks, confirmed).Should().BeNull();

    [Fact]
    public void A_first_registration_has_nothing_to_replace() =>
        ConnectionAddLinearCommand.WorkspaceChangeRefusal(null, Other, linkedTasks: 3, confirmed: false).Should().BeNull();
}
