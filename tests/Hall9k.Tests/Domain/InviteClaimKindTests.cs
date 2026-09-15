using FluentAssertions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>An invite's claim is closed to node-of-owner and member-of-project (idea 202383dc, T2).</summary>
public sealed class InviteClaimKindTests
{
    [Theory]
    [InlineData("node-of-owner", "node-of-owner")]
    [InlineData("NODE-OF-OWNER", "node-of-owner")]
    [InlineData("member-of-project", "member-of-project")]
    [InlineData("something-else", "")]
    [InlineData(null, "")]
    public void Input_maps_to_the_closed_set_and_never_guesses(string? input, string expected)
    {
        InviteClaimKind claim = input;
        claim.Value.Should().Be(expected);
    }

    [Fact]
    public void Round_trips_through_its_own_string_conversion()
    {
        string asString = InviteClaimKind.NodeOfOwner;
        asString.Should().Be("node-of-owner");

        InviteClaimKind backAgain = asString;
        backAgain.Should().Be(InviteClaimKind.NodeOfOwner);
    }
}
