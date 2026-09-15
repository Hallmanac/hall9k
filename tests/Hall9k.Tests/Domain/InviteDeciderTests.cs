using FluentAssertions;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class InviteDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Root = new('a', 64);

    [Fact]
    public void Mint_produces_a_node_of_owner_invite_with_no_project_or_role()
    {
        Guid inviteId = DomainId.New();
        InviteMinted minted = InviteDecider.Mint(
            inviteId, DomainId.New(), Root, InviteClaimKind.NodeOfOwner, role: null, projectId: null,
            projectRepositoryPath: null, "secret", "hash", Now, Now.AddHours(72));

        minted.InviteId.Should().Be(inviteId);
        minted.Claim.Should().Be(InviteClaimKind.NodeOfOwner);
        minted.Role.Should().BeNull();
        minted.ProjectId.Should().BeNull();
        minted.ExpiresAt.Should().Be(Now.AddHours(72));
    }

    [Fact]
    public void Mint_refuses_a_node_of_owner_invite_that_names_a_role_or_a_project()
    {
        Action act = () => InviteDecider.Mint(
            DomainId.New(), DomainId.New(), Root, InviteClaimKind.NodeOfOwner, ProjectMemberRole.Member,
            DomainId.New(), "/repo", "secret", "hash", Now, Now.AddHours(72));

        act.Should().Throw<DomainValidationException>().WithMessage("*names no single project*");
    }

    [Fact]
    public void Mint_produces_a_member_of_project_invite_carrying_its_own_role_and_project()
    {
        Guid projectId = DomainId.New();
        InviteMinted minted = InviteDecider.Mint(
            DomainId.New(), DomainId.New(), Root, InviteClaimKind.MemberOfProject, ProjectMemberRole.Owner,
            projectId, "/repo", "secret", "hash", Now, Now.AddHours(72));

        minted.Claim.Should().Be(InviteClaimKind.MemberOfProject);
        minted.Role.Should().Be(ProjectMemberRole.Owner);
        minted.ProjectId.Should().Be(projectId);
    }

    [Fact]
    public void Mint_refuses_a_member_of_project_invite_missing_its_role_or_project()
    {
        Action act = () => InviteDecider.Mint(
            DomainId.New(), DomainId.New(), Root, InviteClaimKind.MemberOfProject, role: null, projectId: null,
            projectRepositoryPath: null, "secret", "hash", Now, Now.AddHours(72));

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Mint_refuses_an_unrecognized_claim()
    {
        Action act = () => InviteDecider.Mint(
            DomainId.New(), DomainId.New(), Root, InviteClaimKind.Unknown, role: null, projectId: null,
            projectRepositoryPath: null, "secret", "hash", Now, Now.AddHours(72));

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Mint_refuses_an_expiry_that_is_not_after_the_mint_time()
    {
        Action act = () => InviteDecider.Mint(
            DomainId.New(), DomainId.New(), Root, InviteClaimKind.NodeOfOwner, role: null, projectId: null,
            projectRepositoryPath: null, "secret", "hash", Now, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Spend_produces_the_claiming_nodes_own_identity()
    {
        InviteAggregate invite = Minted();
        Guid claimedBy = DomainId.New();

        InviteSpent spent = InviteDecider.Spend(invite, claimedBy, "root-of-claimer", Now.AddMinutes(5));

        spent.InviteId.Should().Be(invite.Id);
        spent.ClaimedByNodeId.Should().Be(claimedBy);
        spent.ClaimedByRootFingerprint.Should().Be("root-of-claimer");
    }

    [Fact]
    public void Spend_refuses_an_already_spent_invite()
    {
        InviteAggregate invite = Minted();
        invite.Apply(InviteDecider.Spend(invite, DomainId.New(), "root-of-claimer", Now.AddMinutes(5)));

        Action act = () => InviteDecider.Spend(invite, DomainId.New(), "someone-else", Now.AddMinutes(10));

        act.Should().Throw<DomainValidationException>().WithMessage("*already spent*");
    }

    private static InviteAggregate Minted()
    {
        InviteAggregate invite = new();
        invite.Apply(InviteDecider.Mint(
            DomainId.New(), DomainId.New(), Root, InviteClaimKind.NodeOfOwner, role: null, projectId: null,
            projectRepositoryPath: null, "secret", "hash", Now, Now.AddHours(72)));
        return invite;
    }
}
