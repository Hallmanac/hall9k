using FluentAssertions;
using Hall9k.Connectors.Trust;
using Hall9k.Daemon.AutoPrReview;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The in-process handoff from the message sweep's trust chain to auto-pr-review's mint rank: what
/// counts as the owner's fleet, and what reads as "no fleet to defer to".
/// </summary>
public sealed class EnrolledNodeSnapshotsTests
{
    private const string Root = "owner-root";
    private static readonly DateTimeOffset IssuedAt = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid RootNode = Guid.Parse("01a00d41-0000-7000-8000-000000000001");
    private static readonly Guid Vouched = Guid.Parse("01a04de3-0000-7000-8000-000000000002");
    private static readonly Guid Revoked = Guid.Parse("01a09999-0000-7000-8000-000000000003");
    private static readonly Guid Project = Guid.Parse("01a0aaaa-0000-7000-8000-000000000004");

    private static TrustChain ChainOf(TrustedOwner? owner) => new(
        owner is null ? new Dictionary<string, TrustedOwner>() : new Dictionary<string, TrustedOwner> { [Root] = owner },
        []);

    private static TrustedOwner Owner(params TrustedNode[] nodes) => new(
        Root, "ssh-ed25519 AAAAFAKE root", nodes, RootNodeId: RootNode.ToString(),
        RevokedNodeIds: new HashSet<string> { Revoked.ToString() });

    private static TrustedNode Node(Guid id) => new(id.ToString(), $"ssh-ed25519 AAAAFAKE{id:N} test", $"fp-{id:N}", IssuedAt);

    [Fact]
    public void The_fleet_is_the_roots_own_node_plus_every_vouched_node_and_never_a_revoked_one()
    {
        IReadOnlyCollection<Guid>? fleet = EnrolledNodeSnapshots.FleetOf(ChainOf(Owner(Node(Vouched))), Root);

        fleet.Should().BeEquivalentTo([RootNode, Vouched]);
    }

    [Fact]
    public void A_chain_that_does_not_name_this_owner_has_no_fleet()
    {
        EnrolledNodeSnapshots.FleetOf(ChainOf(null), Root).Should().BeNull();
    }

    [Fact]
    public void A_recorded_chain_is_readable_per_project_and_nothing_is_recorded_before_the_first_sweep()
    {
        EnrolledNodeSnapshots snapshots = new();

        snapshots.TryGet(Project).Should().BeNull();

        snapshots.Record(Project, ChainOf(Owner(Node(Vouched))), Root);

        snapshots.TryGet(Project).Should().BeEquivalentTo([RootNode, Vouched]);
        snapshots.TryGet(Guid.NewGuid()).Should().BeNull("another project's chain says nothing about this one");
    }

    [Fact]
    public void A_later_chain_that_no_longer_names_the_owner_erases_the_snapshot_so_the_node_reads_as_leader()
    {
        EnrolledNodeSnapshots snapshots = new();
        snapshots.Record(Project, ChainOf(Owner(Node(Vouched))), Root);

        snapshots.Record(Project, ChainOf(null), Root);

        snapshots.TryGet(Project).Should().BeNull();
    }
}
