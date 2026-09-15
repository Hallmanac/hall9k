using FluentAssertions;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Connectors.Trust;

public sealed class InviteLedgerRecordTests
{
    private static readonly DateTimeOffset ExpiresAt = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_node_of_owner_record_round_trips_through_yaml_with_a_null_role()
    {
        InviteLedgerRecord record = new("deadbeef", InviteClaimKind.NodeOfOwner, Role: null, ExpiresAt, Spent: false);

        InviteLedgerRecord? parsed = InviteLedgerRecord.Parse(record.ToYaml());

        parsed.Should().Be(record);
    }

    [Fact]
    public void A_member_of_project_record_round_trips_through_yaml_with_its_own_role()
    {
        InviteLedgerRecord record = new("deadbeef", InviteClaimKind.MemberOfProject, ProjectMemberRole.Owner, ExpiresAt, Spent: true);

        InviteLedgerRecord? parsed = InviteLedgerRecord.Parse(record.ToYaml());

        parsed.Should().Be(record);
    }

    [Fact]
    public void Parse_returns_null_for_content_missing_a_required_field()
    {
        InviteLedgerRecord.Parse("claim: \"node-of-owner\"\n").Should().BeNull();
    }

    [Fact]
    public void Parse_returns_null_for_an_unrecognized_claim()
    {
        string yaml =
            "secret_hash: \"deadbeef\"\n"
            + "claim: \"not-a-real-claim\"\n"
            + "role: null\n"
            + $"expires_at: \"{ExpiresAt:O}\"\n"
            + "spent: \"false\"\n";

        InviteLedgerRecord.Parse(yaml).Should().BeNull();
    }

    [Fact]
    public void PathFor_folds_the_root_fingerprint_into_the_owners_invites_tree()
    {
        Guid inviteId = Guid.NewGuid();
        InviteLedgerRecord.PathFor("root123", inviteId).Should().Be($"owners/root123/invites/{inviteId}.yaml");
        InviteLedgerRecord.RefName("root123").Should().Be("refs/hall9k/ledger/owners/root123");
    }
}
