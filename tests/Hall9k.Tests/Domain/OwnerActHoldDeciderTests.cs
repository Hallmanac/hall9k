using FluentAssertions;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="OwnerActHoldDecider"/> and <see cref="OwnerActHoldAggregate"/> — pure, database-free
/// (Brian's 2026-09-13 testing rule), the same shape <see cref="InviteDeciderTests"/> already takes
/// for the sibling decider in this feature.
/// </summary>
public sealed class OwnerActHoldDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly string CandidateOwnerFingerprint = new('a', 64);

    private static OwnerActHoldAggregate BuildHeldAggregate()
    {
        OwnerActHeld held = OwnerActHoldDecider.Hold(
            DomainId.New(), DomainId.New(), "/repo", DomainId.New(), DomainId.New(), CandidateOwnerFingerprint,
            ProjectMemberRole.Owner, Now, Now);
        OwnerActHoldAggregate aggregate = new();
        aggregate.Apply(held);
        return aggregate;
    }

    [Fact]
    public void Hold_produces_an_owner_role_hold_carrying_every_field()
    {
        Guid id = DomainId.New();
        Guid projectId = DomainId.New();
        Guid inviteId = DomainId.New();
        Guid requesterNodeId = DomainId.New();

        OwnerActHeld held = OwnerActHoldDecider.Hold(
            id, projectId, "/repo", inviteId, requesterNodeId, CandidateOwnerFingerprint, ProjectMemberRole.Owner, Now, Now);

        held.Id.Should().Be(id);
        held.ProjectId.Should().Be(projectId);
        held.ProjectRepositoryPath.Should().Be("/repo");
        held.InviteId.Should().Be(inviteId);
        held.RequesterNodeId.Should().Be(requesterNodeId);
        held.CandidateOwnerFingerprint.Should().Be(CandidateOwnerFingerprint);
        held.Role.Should().Be(ProjectMemberRole.Owner);
    }

    [Fact]
    public void Hold_refuses_a_member_role_write_since_only_an_owner_role_write_is_ever_held()
    {
        Action act = () => OwnerActHoldDecider.Hold(
            DomainId.New(), DomainId.New(), "/repo", DomainId.New(), DomainId.New(), CandidateOwnerFingerprint,
            ProjectMemberRole.Member, Now, Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*only*owner-role*");
    }

    [Fact]
    public void Hold_refuses_a_missing_project()
    {
        Action act = () => OwnerActHoldDecider.Hold(
            DomainId.New(), Guid.Empty, "/repo", DomainId.New(), DomainId.New(), CandidateOwnerFingerprint,
            ProjectMemberRole.Owner, Now, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Hold_refuses_a_blank_candidate_fingerprint()
    {
        Action act = () => OwnerActHoldDecider.Hold(
            DomainId.New(), DomainId.New(), "/repo", DomainId.New(), DomainId.New(), string.Empty,
            ProjectMemberRole.Owner, Now, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Approve_produces_the_approval_naming_the_commit()
    {
        OwnerActHoldAggregate aggregate = BuildHeldAggregate();

        OwnerActApproved approved = OwnerActHoldDecider.Approve(aggregate, "abc123", Now.AddMinutes(1));

        approved.Id.Should().Be(aggregate.Id);
        approved.CommitId.Should().Be("abc123");
        approved.ApprovedAt.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void Approve_refuses_a_hold_already_approved()
    {
        OwnerActHoldAggregate aggregate = BuildHeldAggregate();
        aggregate.Apply(OwnerActHoldDecider.Approve(aggregate, "abc123", Now.AddMinutes(1)));

        Action act = () => OwnerActHoldDecider.Approve(aggregate, "def456", Now.AddMinutes(2));

        act.Should().Throw<DomainValidationException>().WithMessage("*already approved*");
    }

    [Fact]
    public void Approve_refuses_a_hold_already_expired()
    {
        OwnerActHoldAggregate aggregate = BuildHeldAggregate();
        aggregate.Apply(OwnerActHoldDecider.Expire(aggregate, Now.AddMinutes(10)));

        Action act = () => OwnerActHoldDecider.Approve(aggregate, "abc123", Now.AddMinutes(11));

        act.Should().Throw<DomainValidationException>().WithMessage("*already expired*");
    }

    [Fact]
    public void Expire_produces_the_expiry()
    {
        OwnerActHoldAggregate aggregate = BuildHeldAggregate();

        OwnerActHoldExpired expired = OwnerActHoldDecider.Expire(aggregate, Now.AddMinutes(10));

        expired.Id.Should().Be(aggregate.Id);
        expired.ExpiredAt.Should().Be(Now.AddMinutes(10));
    }

    [Fact]
    public void Expire_refuses_a_hold_already_approved()
    {
        OwnerActHoldAggregate aggregate = BuildHeldAggregate();
        aggregate.Apply(OwnerActHoldDecider.Approve(aggregate, "abc123", Now.AddMinutes(1)));

        Action act = () => OwnerActHoldDecider.Expire(aggregate, Now.AddMinutes(10));

        act.Should().Throw<DomainValidationException>().WithMessage("*already approved*");
    }

    [Fact]
    public void Expire_refuses_a_hold_already_expired()
    {
        OwnerActHoldAggregate aggregate = BuildHeldAggregate();
        aggregate.Apply(OwnerActHoldDecider.Expire(aggregate, Now.AddMinutes(10)));

        Action act = () => OwnerActHoldDecider.Expire(aggregate, Now.AddMinutes(20));

        act.Should().Throw<DomainValidationException>().WithMessage("*already expired*");
    }
}
