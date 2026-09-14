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
    public void Parse_RoundTripsAnUnrecognizedKindRatherThanFailing()
    {
        MessageKind kind = MessageKind.Parse("bookmark-announcement");

        kind.Value.Should().Be("bookmark-announcement");
        kind.IsRecognized.Should().BeFalse();
    }
}
