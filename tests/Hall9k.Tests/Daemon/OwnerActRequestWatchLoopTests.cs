using FluentAssertions;
using Hall9k.Daemon.Messaging;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="OwnerActRequestWatchLoop.Decide"/> — the root's own verdict on one owner-act request,
/// pure and database-free, the same shape <c>ClaimRequestWatchLoopTests</c> already takes for
/// <see cref="Hall9k.Daemon.Messaging.ClaimRequestWatchLoop.IsRequesterOwnerVerified"/>.
/// </summary>
public sealed class OwnerActRequestWatchLoopTests
{
    [Fact]
    public void AnUnverifiedRequesterIsRefusedRegardlessOfEverythingElse()
    {
        OwnerActRequestWatchLoop.Decide(
            requesterVerified: false, expired: true, isLiveRootKey: false, ProjectMemberRole.Owner)
            .Should().Be(OwnerActRequestVerdict.RefuseNotVerified);
    }

    [Fact]
    public void AnExpiredRequestIsAnsweredExpiredEvenWhenStillALiveRootKey()
    {
        OwnerActRequestWatchLoop.Decide(
            requesterVerified: true, expired: true, isLiveRootKey: true, ProjectMemberRole.Member)
            .Should().Be(OwnerActRequestVerdict.Expired, "the wait itself is what settles a late answer, not whether this node could still technically act on it");
    }

    [Fact]
    public void ANoLongerLiveRootKeyIsRefused()
    {
        OwnerActRequestWatchLoop.Decide(
            requesterVerified: true, expired: false, isLiveRootKey: false, ProjectMemberRole.Member)
            .Should().Be(OwnerActRequestVerdict.RefuseNotLiveRootKey);
    }

    [Fact]
    public void AMemberRoleWriteIsPerformedAutomatically()
    {
        OwnerActRequestWatchLoop.Decide(
            requesterVerified: true, expired: false, isLiveRootKey: true, ProjectMemberRole.Member)
            .Should().Be(OwnerActRequestVerdict.PerformMemberWrite);
    }

    [Fact]
    public void AnOwnerRoleWriteIsHeldRatherThanPerformedAutomatically()
    {
        OwnerActRequestWatchLoop.Decide(
            requesterVerified: true, expired: false, isLiveRootKey: true, ProjectMemberRole.Owner)
            .Should().Be(OwnerActRequestVerdict.Hold);
    }

    [Fact]
    public void AnUnrecognizedRoleIsRefused()
    {
        OwnerActRequestWatchLoop.Decide(
            requesterVerified: true, expired: false, isLiveRootKey: true, ProjectMemberRole.Unknown)
            .Should().Be(OwnerActRequestVerdict.RefuseUnrecognizedRole);
    }
}
