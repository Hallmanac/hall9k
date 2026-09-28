using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="MemberLabelling"/>'s own node-naming shape (task 21c8f2f3): "node &lt;id&gt;
/// (&lt;label&gt;)" for a node the project's own labels know, and the bare id otherwise — shared
/// by every h9k status and task-command line that names another node. Standalone from
/// <see cref="MemberLabelResolverTests"/> (b7d8222e), which already proves the underlying
/// precedence; this proves the CLI-side wrapper every one of those lines actually calls.
/// </summary>
public sealed class MemberLabellingTests
{
    private const string Fingerprint = "abcdef0123456789";
    private static readonly Guid NodeId = DomainId.New();

    private static MemberLabelLookup LabelsFor(string? ownRootFingerprint = null) => new(
        new ProjectMemberLabels
        {
            Id = DomainId.New(),
            Labels = [new ProjectMemberLabel(Fingerprint, [NodeId], DisplayName.Parse("Brian"), null)],
        },
        ownRootFingerprint);

    [Fact]
    public void NodeMarkup_names_the_owning_members_own_label_in_parentheses()
    {
        MemberLabelling.NodeMarkup(NodeId, LabelsFor()).Should().Be($"node {DomainId.Short(NodeId)} (Brian)");
    }

    [Fact]
    public void NodeMarkup_of_a_node_the_projection_does_not_know_shows_the_bare_id()
    {
        Guid unknownNodeId = DomainId.New();

        MemberLabelling.NodeMarkup(unknownNodeId, LabelsFor()).Should().Be($"node {DomainId.Short(unknownNodeId)}");
    }

    /// <summary>
    /// A cooperative-take status line naming this owner's own node (task 21c8f2f3's own test
    /// coverage list) reads as a bare id: the node-id lines this task adds all lean on
    /// <see cref="MemberLabelResolver.LabelForNodeId"/>'s own-owner exclusion, and this is the
    /// wrapper StatusCommand actually calls for both directions of that pane.
    /// </summary>
    [Fact]
    public void NodeMarkup_of_this_machines_own_owners_node_shows_the_bare_id()
    {
        MemberLabelling.NodeMarkup(NodeId, LabelsFor(ownRootFingerprint: Fingerprint))
            .Should().Be($"node {DomainId.Short(NodeId)}");
    }

    [Fact]
    public void NodeText_carries_the_identical_label_unescaped()
    {
        MemberLabelling.NodeText(NodeId, LabelsFor()).Should().Be($"node {DomainId.Short(NodeId)} (Brian)");
    }
}
