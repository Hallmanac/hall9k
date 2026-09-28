using FluentAssertions;
using Hall9k.Connectors.Trust;
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

/// <summary>
/// <see cref="OwnerActRequestWatchLoop.ValidateInviteAgainstLedger"/> alone (independent pre-PR
/// review, cycle 1, adversarial lens, high): a genuinely-vouched-but-compromised fleet node must
/// not be able to manufacture a member write for an invite that does not exist, is already spent
/// or expired, grants a different role than the one asked for, or would silently overwrite an
/// existing member's or owner's own role. Pure and database-free, the same shape
/// <see cref="OwnerActRequestWatchLoopTests"/> already takes for <c>Decide</c>.
/// </summary>
public sealed class OwnerActRequestWatchLoopValidateInviteAgainstLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static InviteLedgerRecord Outstanding(
        InviteClaimKind claim, ProjectMemberRole? role, DateTimeOffset? expiresAt = null, bool spent = false) =>
        new("secret-hash", claim, role, expiresAt ?? Now.AddMinutes(30), spent);

    [Fact]
    public void NoInviteByThatIdIsRefused()
    {
        OwnerActRequestWatchLoop.ValidateInviteAgainstLedger(
            inviteRecord: null, ProjectMemberRole.Member, existingMemberRole: null, Now)
            .Should().NotBeNull("a request naming an invite this owner's own ledger has never heard of has nothing behind it");
    }

    [Fact]
    public void AnAlreadySpentInviteIsRefused()
    {
        InviteLedgerRecord record = Outstanding(InviteClaimKind.MemberOfProject, ProjectMemberRole.Member, spent: true);

        OwnerActRequestWatchLoop.ValidateInviteAgainstLedger(record, ProjectMemberRole.Member, existingMemberRole: null, Now)
            .Should().NotBeNull("a spent invite has already been honored once and grants nothing further");
    }

    [Fact]
    public void AnAlreadyExpiredInviteIsRefused()
    {
        InviteLedgerRecord record = Outstanding(InviteClaimKind.MemberOfProject, ProjectMemberRole.Member, expiresAt: Now.AddMinutes(-1));

        OwnerActRequestWatchLoop.ValidateInviteAgainstLedger(record, ProjectMemberRole.Member, existingMemberRole: null, Now)
            .Should().NotBeNull("the ledger's own record already says this invite's own window has closed");
    }

    [Fact]
    public void ARequestNamingARoleTheInviteNeverGrantedIsRefused()
    {
        // The adversarial scenario itself: a genuinely vouched fleet node names a REAL, outstanding
        // invite id, but claims a role the invite's own ledger record never granted — an owner-role
        // escalation riding a real member-of-project invite's own existence check.
        InviteLedgerRecord record = Outstanding(InviteClaimKind.MemberOfProject, ProjectMemberRole.Member);

        OwnerActRequestWatchLoop.ValidateInviteAgainstLedger(record, ProjectMemberRole.Owner, existingMemberRole: null, Now)
            .Should().NotBeNull("the invite's own ledger record grants member, never owner");
    }

    [Fact]
    public void ANodeOfOwnerInviteNeverGrantsAMemberWrite()
    {
        InviteLedgerRecord record = Outstanding(InviteClaimKind.NodeOfOwner, role: null);

        OwnerActRequestWatchLoop.ValidateInviteAgainstLedger(record, ProjectMemberRole.Member, existingMemberRole: null, Now)
            .Should().NotBeNull("a node-of-owner invite never grants a member-of-project write at all");
    }

    [Fact]
    public void ACandidateAlreadyAMemberUnderADifferentRoleIsRefused()
    {
        // The other half of the adversarial scenario: a real, outstanding, correctly-roled invite,
        // but the candidate fingerprint the request names is already an OWNER — demoting them via
        // this door would be a silent takeover, mirroring InviteSweepEngine.MemberSlotCheckAsync's
        // own guard against exactly this.
        InviteLedgerRecord record = Outstanding(InviteClaimKind.MemberOfProject, ProjectMemberRole.Member);

        OwnerActRequestWatchLoop.ValidateInviteAgainstLedger(
            record, ProjectMemberRole.Member, existingMemberRole: ProjectMemberRole.Owner.Value, Now)
            .Should().NotBeNull("this candidate already holds a different role that this write would silently overwrite");
    }

    [Fact]
    public void ACandidateAlreadyAtTheSameRoleIsNotBlocked()
    {
        // Mirrors InviteSweepEngine.MemberSlotCheckAsync's own "same role already granted by
        // something this invite never wrote itself" carve-out — a retry or an equivalent prior
        // write is this candidate's own, not a foreign takeover.
        InviteLedgerRecord record = Outstanding(InviteClaimKind.MemberOfProject, ProjectMemberRole.Member);

        OwnerActRequestWatchLoop.ValidateInviteAgainstLedger(
            record, ProjectMemberRole.Member, existingMemberRole: ProjectMemberRole.Member.Value, Now)
            .Should().BeNull();
    }

    [Fact]
    public void AGenuinelyOutstandingInviteForABrandNewCandidateIsNotBlocked()
    {
        InviteLedgerRecord record = Outstanding(InviteClaimKind.MemberOfProject, ProjectMemberRole.Member);

        OwnerActRequestWatchLoop.ValidateInviteAgainstLedger(record, ProjectMemberRole.Member, existingMemberRole: null, Now)
            .Should().BeNull("a real, outstanding, correctly-roled invite naming a brand-new candidate is exactly what this door is for");
    }
}
