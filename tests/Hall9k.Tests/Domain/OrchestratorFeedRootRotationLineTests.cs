using FluentAssertions;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Trust;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The orchestrator feed's own line for a root-key rotation (idea 6be68ee2, PR B of the succession
/// chain): the courier has no trust arm today, so <c>OrchestratorFeedDescription.Of</c> gaining one
/// is itself the acceptance criterion, pinned here as a golden the same way
/// <see cref="OrchestratorFeedRendererTests"/> pins every other arm's own wording.
/// </summary>
public sealed class OrchestratorFeedRootRotationLineTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProjectId = Guid.Parse("01a0bc05-a960-7657-b708-1aed37b5ec69");
    private static readonly Guid PromotedNodeId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid RevokingNodeId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public void An_observed_rotation_names_the_promoted_node()
    {
        string? line = OrchestratorFeedDescription.Of(
            new RootRotationObserved(ProjectId, "owner-a-fingerprint", PromotedNodeId, At));

        line.Should().Be("root key rotated by node 44444444");
    }

    [Fact]
    public void A_revoked_rotation_names_both_the_voided_node_and_the_earlier_key_that_undid_it()
    {
        string? line = OrchestratorFeedDescription.Of(
            new RootRotationRevoked(ProjectId, "owner-a-fingerprint", PromotedNodeId, RevokingNodeId, At));

        line.Should().Be("rotation by node 44444444 revoked by an earlier root key (node 55555555)");
    }

    /// <summary>K0 itself can never be a rotation's own target, but it can be what undoes one — its
    /// own node id is not always resolvable (<c>TrustedOwner.RootNodeId</c> is null on an older
    /// ledger), so a revocation names no node at all rather than guessing.</summary>
    [Fact]
    public void A_revoked_rotation_with_no_resolvable_revoker_still_names_the_voided_node()
    {
        string? line = OrchestratorFeedDescription.Of(
            new RootRotationRevoked(ProjectId, "owner-a-fingerprint", PromotedNodeId, RevokedByNodeId: null, At));

        line.Should().Be("rotation by node 44444444 revoked by an earlier root key");
    }

    [Fact]
    public void Both_types_are_in_the_actionable_band()
    {
        OrchestratorFeedInterest.BandOf(typeof(RootRotationObserved)).Should().Be(OrchestratorFeedLevel.Actionable);
        OrchestratorFeedInterest.BandOf(typeof(RootRotationRevoked)).Should().Be(OrchestratorFeedLevel.Actionable);
    }
}
