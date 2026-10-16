using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Message;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k project member approve</c> says on a node that holds no write for the invite: it
/// names the node that does, or says plainly that nothing is held, so the operator is not left
/// trying each node in the fleet.
/// </summary>
public sealed class ProjectMemberApproveMissingHoldTests
{
    private static readonly Guid InviteId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid RootNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ThisNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private static OwnerActAskLookup.PendingOwnerAct Ask(string? verdict, string? reason = null) =>
        new(InviteId, Now, verdict, reason, RootNodeId, Now.AddHours(72));

    private static string OnMintingNode(OwnerActAskLookup.PendingOwnerAct? ask) =>
        ProjectMemberApproveCommand.MissingHoldMessage(
            "demo", InviteId, RootNodeId, requestSent: true, ask, rootNodeId: null, rootNodeDescription: string.Empty, ThisNodeId);

    [Fact]
    public void TheMintingNodeNamesTheNodeHoldingTheWriteOnlyOnceTheRootSaidHeld()
    {
        string message = OnMintingNode(Ask(OwnerActEnvelopeCodec.OwnerActVerdict.Held));

        message.Should().Contain($"held on node {RootNodeId}")
            .And.Contain($"h9k project member approve demo {InviteId}");
    }

    [Theory]
    [InlineData(null, "has not answered yet")]
    [InlineData(OwnerActEnvelopeCodec.OwnerActVerdict.Refused, "refused this node's own request")]
    [InlineData(OwnerActEnvelopeCodec.OwnerActVerdict.Expired, "answered this node's own request for the invite expired")]
    public void TheMintingNodeDoesNotClaimAHoldTheRootNeverMade(string? verdict, string expectedWording)
    {
        string message = OnMintingNode(Ask(verdict, "that invite does not grant the role"));

        message.Should().Contain(expectedWording).And.NotContain("It is held on");
    }

    [Fact]
    public void ARequestThatIsNoLongerOutstandingSaysNothingIsHeld()
    {
        OnMintingNode(ask: null).Should().Contain("no longer outstanding").And.NotContain("It is held on");
    }

    [Fact]
    public void AnyOtherNodeNamesTheNodeRequestsAreRoutedTo()
    {
        string message = ProjectMemberApproveCommand.MissingHoldMessage(
            "demo", InviteId, requestSentTo: null, requestSent: false, outstandingAsk: null, RootNodeId,
            $" (the root node, node {RootNodeId})", ThisNodeId);

        message.Should().Contain($"(the root node, node {RootNodeId})").And.Contain("marks that node (root)");
    }

    [Fact]
    public void TheRootNodeItselfIsNotToldToRunTheCommandThere()
    {
        string message = ProjectMemberApproveCommand.MissingHoldMessage(
            "demo", InviteId, requestSentTo: null, requestSent: false, outstandingAsk: null, ThisNodeId,
            $" (the root node, node {ThisNodeId})", ThisNodeId);

        message.Should().Contain("This node is the owner's root node").And.NotContain("run h9k project member approve");
    }
}
