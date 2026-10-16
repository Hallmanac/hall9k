using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Trust;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The Nodes cell of <c>h9k project members</c>: which node is the owner's root. The reported fleet
/// listed all three nodes as successors, including the root itself, so the operator could not tell
/// which one to approve on.
/// </summary>
public sealed class ProjectMembersFleetLabelTests
{
    private const string RootFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Guid RootNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ThirdNodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static TrustedOwner OwnerWithSuccessors(params Guid[] successors) =>
        new(RootFingerprint, "ssh-ed25519 AAAAROOT root", [], RootNodeId: RootNodeId.ToString(), SuccessorNodeIds: [.. successors.Select(id => id.ToString())]);

    [Fact]
    public void TheNodeHoldingTheOriginalRootKeyIsMarkedRootEvenWhenItIsAlsoListedAsASuccessor()
    {
        TrustedOwner owner = OwnerWithSuccessors(RootNodeId, SecondNodeId, ThirdNodeId);

        string cell = ProjectMembersCommand.DescribeFleet([RootNodeId, SecondNodeId, ThirdNodeId], owner);

        string[] lines = cell.Split('\n');
        lines[0].Should().Be($"{RootNodeId} [dim](root)[/]");
        lines[1].Should().Be($"{SecondNodeId} [dim](successor)[/]");
        lines[2].Should().Be($"{ThirdNodeId} [dim](successor)[/]");
        cell.Should().NotContain($"{RootNodeId} [dim](successor)[/]");
    }

    [Fact]
    public void AFleetNodeWithNoSuccessorRecordKeepsNoSuffix()
    {
        TrustedOwner owner = OwnerWithSuccessors(SecondNodeId);

        string cell = ProjectMembersCommand.DescribeFleet([RootNodeId, SecondNodeId, ThirdNodeId], owner);

        cell.Split('\n')[2].Should().Be(ThirdNodeId.ToString());
    }

    [Fact]
    public void ANodeWhoseKeyWasPromotedIntoTheRootKeySetStillSaysRootKey()
    {
        TrustedOwner owner = OwnerWithSuccessors() with
        {
            RootKeys =
            [
                new LiveRootKey("ssh-ed25519 AAAAROOT root", RootFingerprint, null),
                new LiveRootKey("ssh-ed25519 AAAASECOND second", "bbbb", SecondNodeId.ToString()),
            ],
        };

        ProjectMembersCommand.DescribeFleet([RootNodeId, SecondNodeId], owner)
            .Should().Contain($"{SecondNodeId} [dim](root key)[/]");
    }
}
