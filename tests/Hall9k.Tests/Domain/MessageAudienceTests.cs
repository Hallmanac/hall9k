using FluentAssertions;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class MessageAudienceTests
{
    private static readonly Guid MyNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string MyOwnerFingerprint = "abc123";
    private const string OtherOwnerFingerprint = "def456";

    [Fact]
    public void Project_MatchesEveryReader()
    {
        MessageAudience.Project.Matches(MyNodeId, MyOwnerFingerprint).Should().BeTrue();
        MessageAudience.Project.Matches(OtherNodeId, OtherOwnerFingerprint).Should().BeTrue();
    }

    [Fact]
    public void Owner_MatchesOnlyThatOwnersFingerprint()
    {
        MessageAudience audience = MessageAudience.Owner(MyOwnerFingerprint);

        audience.Matches(MyNodeId, MyOwnerFingerprint).Should().BeTrue();
        audience.Matches(OtherNodeId, MyOwnerFingerprint).Should().BeTrue("owner audience is not node-scoped");
        audience.Matches(MyNodeId, OtherOwnerFingerprint).Should().BeFalse();
    }

    [Fact]
    public void Node_MatchesOnlyThatNode()
    {
        MessageAudience audience = MessageAudience.Node(MyNodeId);

        audience.Matches(MyNodeId, MyOwnerFingerprint).Should().BeTrue();
        audience.Matches(OtherNodeId, MyOwnerFingerprint).Should().BeFalse();
    }

    [Fact]
    public void Parse_RoundTripsEveryShape()
    {
        MessageAudience.Parse("project").Should().Be(MessageAudience.Project);
        MessageAudience.Parse($"owner:{MyOwnerFingerprint}").Should().Be(MessageAudience.Owner(MyOwnerFingerprint));
        MessageAudience.Parse($"node:{MyNodeId}").Should().Be(MessageAudience.Node(MyNodeId));
    }

    [Fact]
    public void Parse_RefusesAnythingNotOneOfTheThreeShapes()
    {
        Action act = () => MessageAudience.Parse("everyone");

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Parse_RefusesANodeAudienceWithANonGuidId()
    {
        Action act = () => MessageAudience.Parse("node:not-a-guid");

        act.Should().Throw<DomainValidationException>();
    }
}
