using FluentAssertions;
using Hall9k.Domain.Features.Message;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class MessageEnvelopeCodecTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid FromNode = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Encode_ThenDecode_RoundTripsEveryField()
    {
        MessageEnvelopeV1 envelope = new(
            Seq: 3, At: Now, FromNode: FromNode, FromOwner: "fingerprint-1",
            To: MessageAudience.Node(FromNode), About: "task-42", Kind: MessageKind.Note, Body: "hello there");

        string json = MessageEnvelopeCodec.Encode(envelope);
        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Parsed);
        result.Envelope.Should().Be(envelope);
    }

    [Fact]
    public void Decode_RefusesAnUnsupportedVersion_WithoutThrowing()
    {
        string json = """{"version":2,"seq":1,"at":"2026-09-14T12:00:00Z","fromNode":"11111111-1111-1111-1111-111111111111","fromOwner":"fp","to":"project","about":null,"kind":"note","body":"hi"}""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.UnsupportedVersion);
        result.Version.Should().Be(2);
        result.Envelope.Should().BeNull();
    }

    [Fact]
    public void Decode_RefusesAMissingVersionField_WithoutThrowing()
    {
        string json = """{"seq":1,"at":"2026-09-14T12:00:00Z","fromNode":"11111111-1111-1111-1111-111111111111","fromOwner":"fp","to":"project","about":null,"kind":"note","body":"hi"}""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.UnsupportedVersion);
        result.Version.Should().BeNull();
    }

    [Fact]
    public void Decode_ParsesAnUnrecognizedKind_RatherThanRefusing()
    {
        string json = """{"version":1,"seq":1,"at":"2026-09-14T12:00:00Z","fromNode":"11111111-1111-1111-1111-111111111111","fromOwner":"fp","to":"project","about":null,"kind":"bookmark","body":"hi"}""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Parsed);
        result.Envelope!.Kind.IsRecognized.Should().BeFalse();
        result.Envelope.Kind.Value.Should().Be("bookmark");
    }
}
