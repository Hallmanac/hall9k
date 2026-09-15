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

    [Fact]
    public void Decode_RefusesAnUnrecognizedAudience_WithoutThrowing()
    {
        string json = """{"version":1,"seq":1,"at":"2026-09-14T12:00:00Z","fromNode":"11111111-1111-1111-1111-111111111111","fromOwner":"fp","to":"team:x","about":null,"kind":"note","body":"hi"}""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Malformed);
        result.Envelope.Should().BeNull();
    }

    [Fact]
    public void Decode_RefusesAMissingAudience_WithoutThrowing()
    {
        string json = """{"version":1,"seq":1,"at":"2026-09-14T12:00:00Z","fromNode":"11111111-1111-1111-1111-111111111111","fromOwner":"fp","about":null,"kind":"note","body":"hi"}""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Malformed);
    }

    [Fact]
    public void Decode_RefusesAMissingSeq_WithoutThrowing()
    {
        string json = """{"version":1,"at":"2026-09-14T12:00:00Z","fromNode":"11111111-1111-1111-1111-111111111111","fromOwner":"fp","to":"project","about":null,"kind":"note","body":"hi"}""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Malformed);
    }

    [Fact]
    public void Decode_RefusesAMissingAt_WithoutThrowing()
    {
        // A missing "at" property deserializes System.Text.Json's non-nullable DateTimeOffset to
        // its default (0001-01-01), not a thrown exception — silently accepting it here would
        // record and later display a corrupted sent timestamp instead of refusing the envelope.
        string json = """{"version":1,"seq":1,"fromNode":"11111111-1111-1111-1111-111111111111","fromOwner":"fp","to":"project","about":null,"kind":"note","body":"hi"}""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Malformed);
    }

    [Fact]
    public void Decode_RefusesAMissingFromNode_WithoutThrowing()
    {
        string json = """{"version":1,"seq":1,"at":"2026-09-14T12:00:00Z","fromOwner":"fp","to":"project","about":null,"kind":"note","body":"hi"}""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Malformed);
    }

    [Fact]
    public void Decode_RefusesAMissingFromOwner_WithoutThrowing()
    {
        string json = """{"version":1,"seq":1,"at":"2026-09-14T12:00:00Z","fromNode":"11111111-1111-1111-1111-111111111111","to":"project","about":null,"kind":"note","body":"hi"}""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Malformed);
    }

    [Fact]
    public void Decode_RefusesAMissingKind_WithoutThrowing()
    {
        string json = """{"version":1,"seq":1,"at":"2026-09-14T12:00:00Z","fromNode":"11111111-1111-1111-1111-111111111111","fromOwner":"fp","to":"project","about":null,"body":"hi"}""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Malformed);
    }

    [Fact]
    public void Decode_RefusesAMissingBody_WithoutThrowing()
    {
        string json = """{"version":1,"seq":1,"at":"2026-09-14T12:00:00Z","fromNode":"11111111-1111-1111-1111-111111111111","fromOwner":"fp","to":"project","about":null,"kind":"note"}""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Malformed);
    }

    [Fact]
    public void Decode_RefusesASeqBelowOne_WithoutThrowing()
    {
        string json = """{"version":1,"seq":0,"at":"2026-09-14T12:00:00Z","fromNode":"11111111-1111-1111-1111-111111111111","fromOwner":"fp","to":"project","about":null,"kind":"note","body":"hi"}""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Malformed);
    }

    [Fact]
    public void Decode_RefusesANonIntegerVersion_WithoutThrowing()
    {
        string json = """{"version":1.5,"seq":1,"at":"2026-09-14T12:00:00Z","fromNode":"11111111-1111-1111-1111-111111111111","fromOwner":"fp","to":"project","about":null,"kind":"note","body":"hi"}""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Malformed);
    }

    [Fact]
    public void Decode_RefusesANonObjectRoot_WithoutThrowing()
    {
        string json = "\"just a string\"";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Malformed);
    }

    [Fact]
    public void Decode_RefusesTruncatedJson_WithoutThrowing()
    {
        string json = """{"version":1,"seq":1,"at":"2026-09-14T""";

        MessageEnvelopeCodec.DecodeResult result = MessageEnvelopeCodec.Decode(json);

        result.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Malformed);
    }
}
