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
    public void Parse_RecognizesTheCooperativeTakeKinds(string raw)
    {
        MessageKind kind = MessageKind.Parse(raw);

        kind.Value.Should().Be(raw);
        kind.IsRecognized.Should().BeTrue();
    }

    [Fact]
    public void MechanicalKindValues_names_exactly_the_cooperative_take_kinds()
    {
        MessageKind.MechanicalKindValues.Should().BeEquivalentTo(
            [MessageKind.ClaimRequest.Value, MessageKind.ClaimGranted.Value, MessageKind.ClaimRefused.Value]);
    }
}
