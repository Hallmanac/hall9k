using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="NodePlacementResolver.Resolve"/> is <c>h9k task assign --node</c>'s own fragment
/// matcher (idea 202383dc: an owner can place a task on one of their own nodes), pure and I/O-free
/// so the refusal for an unknown node is a plain unit test against a fixed candidate list rather
/// than one that needs a ledger.
/// </summary>
public sealed class NodePlacementResolverTests
{
    private static readonly Guid VouchedNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SelfNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AmbiguousNodeIdA = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AmbiguousNodeIdB = Guid.Parse("33333333-4444-4444-4444-444444444444");

    [Fact]
    public void A_full_id_already_in_the_fleet_resolves()
    {
        Guid resolved = NodePlacementResolver.Resolve(VouchedNodeId.ToString(), [VouchedNodeId, SelfNodeId]);

        resolved.Should().Be(VouchedNodeId);
    }

    [Fact]
    public void A_full_id_not_vouched_into_the_fleet_is_refused()
    {
        Guid unknownNodeId = Guid.Parse("99999999-9999-9999-9999-999999999999");

        Action act = () => NodePlacementResolver.Resolve(unknownNodeId.ToString(), [VouchedNodeId, SelfNodeId]);

        act.Should().Throw<DomainValidationException>().WithMessage("*not vouched*");
    }

    [Fact]
    public void An_unambiguous_fragment_resolves_against_the_fleet()
    {
        string fragment = VouchedNodeId.ToString("N")[..8];

        Guid resolved = NodePlacementResolver.Resolve(fragment, [VouchedNodeId, SelfNodeId]);

        resolved.Should().Be(VouchedNodeId);
    }

    [Fact]
    public void A_fragment_matching_no_node_in_the_fleet_is_refused_as_not_found()
    {
        Action act = () => NodePlacementResolver.Resolve("deadbeef", [VouchedNodeId, SelfNodeId]);

        act.Should().Throw<DomainNotFoundException>().WithMessage("*No node*");
    }

    [Fact]
    public void An_ambiguous_fragment_is_refused()
    {
        Action act = () => NodePlacementResolver.Resolve("333333", [AmbiguousNodeIdA, AmbiguousNodeIdB]);

        act.Should().Throw<DomainConflictException>().WithMessage("*ambiguous*");
    }

    [Fact]
    public void This_installs_own_node_id_always_resolves_even_with_an_otherwise_empty_fleet()
    {
        Guid resolved = NodePlacementResolver.Resolve(SelfNodeId.ToString(), [SelfNodeId]);

        resolved.Should().Be(SelfNodeId);
    }
}
