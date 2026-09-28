using FluentAssertions;
using Hall9k.Domain.Features.Message;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class MessageKindTests
{
    [Fact]
    public void Parse_RecognizesNote()
    {
        MessageKind kind = MessageKind.Parse("note");

        kind.Should().Be(MessageKind.Note);
        kind.IsRecognized.Should().BeTrue();
    }

    [Fact]
    public void Parse_RecognizesHandoff()
    {
        MessageKind kind = MessageKind.Parse("handoff");

        kind.Should().Be(MessageKind.Handoff);
        kind.IsRecognized.Should().BeTrue();
    }

    [Fact]
    public void Parse_RoundTripsAnUnrecognizedKindRatherThanFailing()
    {
        MessageKind kind = MessageKind.Parse("bookmark-announcement");

        kind.Value.Should().Be("bookmark-announcement");
        kind.IsRecognized.Should().BeFalse();
    }

    [Theory]
    [InlineData("claim-request")]
    [InlineData("claim-granted")]
    [InlineData("claim-refused")]
    [InlineData("owner-act-request")]
    [InlineData("owner-act-outcome")]
    public void Parse_RecognizesTheCooperativeTakeKinds(string raw)
    {
        MessageKind kind = MessageKind.Parse(raw);

        kind.Value.Should().Be(raw);
        kind.IsRecognized.Should().BeTrue();
    }

    [Fact]
    public void Parse_RecognizesTheOwnerActPair()
    {
        MessageKind.Parse("owner-act-request").Should().Be(MessageKind.OwnerActRequest);
        MessageKind.Parse("owner-act-outcome").Should().Be(MessageKind.OwnerActOutcome);
    }

    [Fact]
    public void MechanicalKindValues_names_exactly_the_cooperative_take_and_owner_act_kinds()
    {
        MessageKind.MechanicalKindValues.Should().BeEquivalentTo(
            [
                MessageKind.ClaimRequest.Value, MessageKind.ClaimGranted.Value, MessageKind.ClaimRefused.Value,
                MessageKind.OwnerActRequest.Value, MessageKind.OwnerActOutcome.Value,
            ]);
    }

    [Fact]
    public void ClaimKindValues_names_exactly_the_claim_kinds_never_the_owner_act_pair()
    {
        // ClaimRequestWatchLoop's own poll must read this narrower list, never
        // MechanicalKindValues — independent pre-PR review, cycle 1, both lenses, high: polling the
        // wider list let that loop grab an owner-act message before OwnerActRequestWatchLoop's own
        // separate hosted service ever saw it.
        MessageKind.ClaimKindValues.Should().BeEquivalentTo(
            [MessageKind.ClaimRequest.Value, MessageKind.ClaimGranted.Value, MessageKind.ClaimRefused.Value]);
        MessageKind.ClaimKindValues.Should().NotContain(MessageKind.OwnerActRequest.Value);
        MessageKind.ClaimKindValues.Should().NotContain(MessageKind.OwnerActOutcome.Value);
    }
}
