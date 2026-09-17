using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Connectors.Replication;

/// <summary>
/// Idea 202383dc, M2b, task 9408d525: <see cref="EventCatchUpCoordinator.RankCandidates"/> is pure,
/// DB-free logic — the voucher first, then owner-role members, then any other member, most recently
/// moved outbox first within a rank (the caller's own precondition on <c>knownNodeIds</c>'s
/// ordering, not something this method computes itself — see its own doc comment).
/// </summary>
public sealed class EventCatchUpCoordinatorRankCandidatesTests
{
    [Fact]
    public void Orders_the_voucher_first_then_owner_role_members_then_any_other_member()
    {
        Guid myNodeId = DomainId.New();
        Guid voucherNodeId = DomainId.New();
        Guid ownerNodeId = DomainId.New();
        Guid plainMemberNodeId = DomainId.New();

        const string ownerRoot = "owner-root-fingerprint";
        const string plainMemberRoot = "plain-member-root-fingerprint";

        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [ownerRoot] = new TrustedOwner(
                    ownerRoot, "ssh-ed25519 AAAAFAKEOWNER owner",
                    [
                        new TrustedNode(
                            voucherNodeId.ToString(), "ssh-ed25519 AAAAFAKEVOUCHER voucher", "voucher-fingerprint", DateTimeOffset.UtcNow),
                        new TrustedNode(
                            ownerNodeId.ToString(), "ssh-ed25519 AAAAFAKEOWNERNODE owner-node", "owner-node-fingerprint", DateTimeOffset.UtcNow),
                    ]),
                [plainMemberRoot] = new TrustedOwner(
                    plainMemberRoot, "ssh-ed25519 AAAAFAKEPLAIN plain",
                    [
                        new TrustedNode(
                            plainMemberNodeId.ToString(), "ssh-ed25519 AAAAFAKEPLAINNODE plain-node", "plain-node-fingerprint",
                            DateTimeOffset.UtcNow),
                    ]),
            },
            [
                new ProjectMember(ownerRoot, MembershipRole.Owner, DateTimeOffset.UtcNow),
                new ProjectMember(plainMemberRoot, MembershipRole.Member, DateTimeOffset.UtcNow),
            ]);

        // Deliberately ordered so a passing test can only mean the TIERS are respected, never that
        // this method silently re-sorted knownNodeIds by something of its own: the plain member
        // sorts first here, the voucher last, the opposite of the expected output order.
        IReadOnlyList<Guid> knownNodeIds = [plainMemberNodeId, ownerNodeId, voucherNodeId];

        IReadOnlyList<Guid> ranked = EventCatchUpCoordinator.RankCandidates(knownNodeIds, myNodeId, voucherNodeId, trustChain);

        ranked.Should().Equal(voucherNodeId, ownerNodeId, plainMemberNodeId);
    }

    [Fact]
    public void Excludes_this_node_itself_and_a_node_no_chain_currently_recognizes()
    {
        Guid myNodeId = DomainId.New();
        Guid unenrolledNodeId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        const string memberRoot = "member-root-fingerprint";

        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [memberRoot] = new TrustedOwner(
                    memberRoot, "ssh-ed25519 AAAAFAKEMEMBER member",
                    [
                        new TrustedNode(
                            memberNodeId.ToString(), "ssh-ed25519 AAAAFAKEMEMBERNODE member-node", "member-node-fingerprint",
                            DateTimeOffset.UtcNow),
                    ]),
            },
            [new ProjectMember(memberRoot, MembershipRole.Member, DateTimeOffset.UtcNow)]);

        // myNodeId is always excluded even though it is never enrolled anywhere here; unenrolledNodeId
        // is a node no chain currently recognizes at all — neither is ever a candidate.
        IReadOnlyList<Guid> knownNodeIds = [myNodeId, unenrolledNodeId, memberNodeId];

        IReadOnlyList<Guid> ranked = EventCatchUpCoordinator.RankCandidates(knownNodeIds, myNodeId, voucherNodeId: null, trustChain);

        ranked.Should().Equal(memberNodeId);
    }

    [Fact]
    public void A_voucher_this_project_does_not_recognize_as_a_member_is_never_a_candidate()
    {
        Guid myNodeId = DomainId.New();
        Guid unrecognizedVoucherNodeId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        const string memberRoot = "member-root-fingerprint";

        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [memberRoot] = new TrustedOwner(
                    memberRoot, "ssh-ed25519 AAAAFAKEMEMBER member",
                    [
                        new TrustedNode(
                            memberNodeId.ToString(), "ssh-ed25519 AAAAFAKEMEMBERNODE member-node", "member-node-fingerprint",
                            DateTimeOffset.UtcNow),
                    ]),
            },
            [new ProjectMember(memberRoot, MembershipRole.Member, DateTimeOffset.UtcNow)]);

        IReadOnlyList<Guid> knownNodeIds = [memberNodeId];

        IReadOnlyList<Guid> ranked = EventCatchUpCoordinator.RankCandidates(
            knownNodeIds, myNodeId, unrecognizedVoucherNodeId, trustChain);

        // The named voucher belongs to no chain this project currently trusts, so it is skipped,
        // never inserted first anyway.
        ranked.Should().Equal(memberNodeId);
    }
}
