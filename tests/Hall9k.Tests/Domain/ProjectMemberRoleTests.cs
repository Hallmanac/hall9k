using FluentAssertions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>A project member's role is closed to owner and member (idea 202383dc, T1;
/// PLAN.md bearings: "Roles are owner and member only").</summary>
public sealed class ProjectMemberRoleTests
{
    [Theory]
    [InlineData("owner", "owner")]
    [InlineData("OWNER", "owner")]
    [InlineData("member", "member")]
    [InlineData("Member", "member")]
    [InlineData("something-else", "")]
    [InlineData(null, "")]
    public void Input_maps_to_the_closed_set_and_never_guesses(string? input, string expected)
    {
        ProjectMemberRole role = input;
        role.Value.Should().Be(expected);
    }

    [Fact]
    public void Round_trips_through_its_own_string_conversion()
    {
        string asString = ProjectMemberRole.Owner;
        asString.Should().Be("owner");

        ProjectMemberRole backAgain = asString;
        backAgain.Should().Be(ProjectMemberRole.Owner);
    }
}
