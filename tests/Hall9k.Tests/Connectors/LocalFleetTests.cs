using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Trust;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// Reading the local owner's fleet for a prompt: the ids the ledger chain vouches for that owner,
/// and null, which fences, for every way the answer can be missing (security review idea 6be68ee2,
/// prompt-builders findings 1 to 6).
/// </summary>
public sealed class LocalFleetTests
{
    [Fact]
    public void The_fleet_is_the_owners_root_node_plus_every_vouched_node_and_no_teammate()
    {
        LocalFleet fleet = ForeignNoteFixtures.Fleet();

        fleet.NodeIds.Should().BeEquivalentTo(
            [ForeignNoteFixtures.LocalRootNode, ForeignNoteFixtures.LocalSecondNode]);
        fleet.Chain.Should().NotBeNull("the chain rides along for labelling a foreign note's owner");
    }

    [Fact]
    public void An_owner_the_chain_does_not_name_has_no_fleet_rather_than_an_empty_one()
    {
        LocalFleet.Of(ForeignNoteFixtures.Chain(), "SHA256:nobody").Should().BeNull();
        LocalFleet.Of(ForeignNoteFixtures.Chain(), ownerRootFingerprint: null).Should().BeNull();
    }

    [Fact]
    public async Task The_fleet_is_read_from_the_projects_ledger()
    {
        FakeLedgerChainReader reader = new(ForeignNoteFixtures.Chain());

        LocalFleet? fleet = await LocalFleet.ReadAsync(
            reader, "/repos/hall9k", ForeignNoteFixtures.LocalRoot, CancellationToken.None);

        fleet!.NodeIds.Should().Contain(ForeignNoteFixtures.LocalSecondNode);
    }

    [Fact]
    public async Task A_missing_input_or_an_unreadable_chain_is_no_fleet_and_never_an_exception()
    {
        FakeLedgerChainReader unreadable = new(_ => throw new InvalidOperationException("remote unreachable"));
        FakeLedgerChainReader readable = new(ForeignNoteFixtures.Chain());

        (await LocalFleet.ReadAsync(unreadable, "/repos/hall9k", ForeignNoteFixtures.LocalRoot, CancellationToken.None))
            .Should().BeNull("a prompt still has to compose, and no fleet fences");
        (await LocalFleet.ReadAsync(readable, "  ", ForeignNoteFixtures.LocalRoot, CancellationToken.None)).Should().BeNull();
        (await LocalFleet.ReadAsync(readable, "/repos/hall9k", ownerRootFingerprint: null, CancellationToken.None))
            .Should().BeNull();
    }
}
