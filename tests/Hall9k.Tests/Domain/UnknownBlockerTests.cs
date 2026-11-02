using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The sentence a node says about a blocker it has no record of. It is one string shared by
/// <c>h9k status</c>, <c>h9k task show</c>, and the orchestrator feed, so the golden here pins
/// the wording for all three.
/// </summary>
public sealed class UnknownBlockerTests
{
    private static readonly Guid First = Guid.Parse("01a0bc05-a960-7657-b708-1aed37b5ec69");
    private static readonly Guid Second = Guid.Parse("01a0bc05-a960-7657-b708-1aed37b5aaaa");

    [Fact]
    public void One_unknown_blocker_is_named_by_short_id_with_a_hint_at_whom_to_ask()
    {
        UnknownBlocker.Line([First]).Should().Be(
            "unknown blocker 37b5ec69: it most likely belongs to a project in a solution held on a "
            + "teammate's node, so ask that teammate about it");
    }

    [Fact]
    public void Several_unknown_blockers_are_named_together_in_the_plural()
    {
        UnknownBlocker.Line([First, Second]).Should().Be(
            "unknown blockers 37b5ec69, 37b5aaaa: they most likely belong to projects in a solution held on a "
            + "teammate's node, so ask that teammate about them");
    }

    [Fact]
    public void No_unknown_blocker_means_no_line()
    {
        UnknownBlocker.Line([]).Should().BeNull();
    }

    [Fact]
    public void Only_the_blockers_absent_from_the_known_set_are_unknown_and_each_is_listed_once()
    {
        HashSet<Guid> known = [First];

        UnknownBlocker.Among([First, Second, Second], known).Should().Equal(Second);
    }
}
