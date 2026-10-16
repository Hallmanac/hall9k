using FluentAssertions;
using Hall9k.Cli.Commands;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k project member approve</c> says on a node that holds no write for the invite: it
/// names the node that does, so the operator is not left trying each node in the fleet.
/// </summary>
public sealed class ProjectMemberApproveMissingHoldTests
{
    private static readonly Guid InviteId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid RootNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void TheMintingNodeNamesTheNodeItsRequestWentTo()
    {
        string message = ProjectMemberApproveCommand.MissingHoldMessage("demo", InviteId, RootNodeId, string.Empty);

        message.Should().Contain($"held on node {RootNodeId}")
            .And.Contain($"h9k project member approve demo {InviteId}");
    }

    [Fact]
    public void AnyOtherNodeNamesTheRootNodeFromTheChain()
    {
        string message = ProjectMemberApproveCommand.MissingHoldMessage(
            "demo", InviteId, requestSentTo: null, $" (the root node, node {RootNodeId})");

        message.Should().Contain($"(the root node, node {RootNodeId})").And.Contain("marks that node (root)");
    }
}
