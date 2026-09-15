using FluentAssertions;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class MessageInboxDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid SenderNode = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void AdvanceCursor_RefusesASeqBelowOne()
    {
        Action act = () => MessageInboxDecider.AdvanceCursor(SenderNode, 0, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void AdvanceCursor_ThenIgnoreSender_ThenAdvanceCursorAgain_ClearsTheIgnoredMark()
    {
        MessageInboxAggregate inbox = new();
        inbox.Apply(MessageInboxDecider.AdvanceCursor(SenderNode, 3, Now));
        inbox.Apply(MessageInboxDecider.IgnoreSender(SenderNode, "no node file", verificationFailed: false, Now.AddMinutes(1)));

        inbox.SenderIgnored.Should().BeTrue();

        inbox.Apply(MessageInboxDecider.AdvanceCursor(SenderNode, 4, Now.AddMinutes(2)));

        inbox.HighestSeqReceived.Should().Be(4);
        inbox.SenderIgnored.Should().BeFalse();
        inbox.IgnoredReason.Should().BeNull();
        inbox.IgnoredForVerificationFailure.Should().BeFalse();
    }

    [Fact]
    public void IgnoreSender_RefusesABlankReason()
    {
        Action act = () => MessageInboxDecider.IgnoreSender(SenderNode, string.Empty, verificationFailed: false, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void ConfirmVouched_ClearsAnIgnoredMarkWithoutTouchingTheCursor()
    {
        MessageInboxAggregate inbox = new();
        inbox.Apply(MessageInboxDecider.IgnoreSender(SenderNode, "no node file", verificationFailed: false, Now));

        inbox.SenderIgnored.Should().BeTrue();

        inbox.Apply(MessageInboxDecider.ConfirmVouched(SenderNode, Now.AddMinutes(1)));

        inbox.SenderIgnored.Should().BeFalse();
        inbox.IgnoredReason.Should().BeNull();
        inbox.HighestSeqReceived.Should().Be(0, "a vouch confirmation is not a cursor advance");
    }

    [Fact]
    public void IgnoreSender_ForAVerificationFailure_MarksItDistinctlyFromAnUnvouchedSender()
    {
        MessageInboxAggregate inbox = new();
        inbox.Apply(MessageInboxDecider.IgnoreSender(SenderNode, "envelope verification failed for seq 5", verificationFailed: true, Now));

        inbox.SenderIgnored.Should().BeTrue();
        inbox.IgnoredForVerificationFailure.Should().BeTrue(
            "a specific envelope failing verification is a different fact than the sender not being vouched for at all");
    }
}
