using FluentAssertions;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class MessageDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid FromNode = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Queue_ProducesAQueuedEventCarryingTheEnvelopesOwnContent()
    {
        MessageQueued @event = MessageDecider.Queue(
            FromNode, 1, "fingerprint-1", MessageAudience.Project, "idea-9", MessageKind.Note, "hello", Now);

        @event.FromNodeId.Should().Be(FromNode);
        @event.Seq.Should().Be(1);
        @event.FromOwner.Should().Be("fingerprint-1");
        @event.To.Should().Be("project");
        @event.About.Should().Be("idea-9");
        @event.Kind.Should().Be("note");
        @event.Body.Should().Be("hello");
        @event.At.Should().Be(Now);
    }

    [Fact]
    public void Queue_RefusesASeqBelowOne()
    {
        Action act = () => MessageDecider.Queue(
            FromNode, 0, "fingerprint-1", MessageAudience.Project, null, MessageKind.Note, "hello", Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Send_ProducesASentEventCarryingTheNodeAndSeq()
    {
        MessageSent @event = MessageDecider.Send(FromNode, 1, Now);

        @event.FromNodeId.Should().Be(FromNode);
        @event.Seq.Should().Be(1);
        @event.At.Should().Be(Now);
    }

    [Fact]
    public void Send_RefusesASeqBelowOne()
    {
        Action act = () => MessageDecider.Send(FromNode, 0, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void FailSend_RefusesABlankReason()
    {
        MessageEnvelopeV1 envelope = new(1, Now, FromNode, "fp", MessageAudience.Project, null, MessageKind.Note, "hi");

        Action act = () => MessageDecider.FailSend(envelope, string.Empty, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void FailSend_CarriesTheEnvelopesOwnContentSoAResendCanRebuildIt()
    {
        MessageEnvelopeV1 envelope = new(
            Seq: 1, At: Now, FromNode: FromNode, FromOwner: "fingerprint-1",
            To: MessageAudience.Project, About: "idea-9", Kind: MessageKind.Note, Body: "hello");

        MessageSendFailed @event = MessageDecider.FailSend(envelope, "push rejected", Now);

        @event.FromNodeId.Should().Be(FromNode);
        @event.Seq.Should().Be(1);
        @event.FromOwner.Should().Be("fingerprint-1");
        @event.To.Should().Be("project");
        @event.About.Should().Be("idea-9");
        @event.Kind.Should().Be("note");
        @event.Body.Should().Be("hello");
        @event.Reason.Should().Be("push rejected");
    }

    [Fact]
    public void Resend_RefusesAMessageThatHasNotFailed()
    {
        MessageAggregate message = new();
        message.Apply(MessageDecider.Send(FromNode, 1, Now));

        Action act = () => MessageDecider.Resend(message, Now.AddMinutes(1));

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Resend_ClearsTheFailureAndCountsTheAttempt()
    {
        MessageEnvelopeV1 envelope = new(1, Now, FromNode, "fp", MessageAudience.Project, null, MessageKind.Note, "hi");
        MessageAggregate message = new();
        message.Apply(MessageDecider.FailSend(envelope, "push rejected", Now));

        message.Apply(MessageDecider.Resend(message, Now.AddMinutes(1)));

        message.SendFailed.Should().BeFalse();
        message.SendFailureReason.Should().BeNull();
        message.ResendCount.Should().Be(1);
        message.SentAt.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void Receive_FlattensTheEnvelopesOwnFieldsOntoTheEvent()
    {
        MessageEnvelopeV1 envelope = new(
            Seq: 2, At: Now, FromNode: FromNode, FromOwner: "fingerprint-1",
            To: MessageAudience.Project, About: "idea-9", Kind: MessageKind.Note, Body: "hello");

        MessageReceived @event = MessageDecider.Receive(FromNode, envelope, Now.AddSeconds(5));

        @event.FromNodeId.Should().Be(FromNode);
        @event.Seq.Should().Be(2);
        @event.SentAt.Should().Be(Now);
        @event.FromOwnerFingerprint.Should().Be("fingerprint-1");
        @event.To.Should().Be("project");
        @event.About.Should().Be("idea-9");
        @event.Kind.Should().Be("note");
        @event.Body.Should().Be("hello");
        @event.ReceivedAt.Should().Be(Now.AddSeconds(5));
    }

    [Fact]
    public void Handle_RefusesAMessageThatHasNotBeenReceived()
    {
        MessageAggregate message = new();
        message.Apply(MessageDecider.Send(FromNode, 1, Now));

        Action act = () => MessageDecider.Handle(message, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Handle_MarksAReceivedMessageHandled()
    {
        MessageEnvelopeV1 envelope = new(1, Now, FromNode, "fp", MessageAudience.Project, null, MessageKind.Note, "hi");
        MessageAggregate message = new();
        message.Apply(MessageDecider.Receive(FromNode, envelope, Now));

        message.Apply(MessageDecider.Handle(message, Now.AddMinutes(1)));

        message.HandledAt.Should().Be(Now.AddMinutes(1));
    }
}
