using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k messages</c>'s own Sender column (task b7d8222e): the sending node's short id, with the
/// owning member's own label appended in parentheses when known — a node id line, so the id is
/// kept rather than replaced, unlike a fingerprint line.
/// </summary>
public sealed class MessagesSenderCellTests
{
    private static readonly Guid NodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Appends_the_owning_members_label_in_parentheses_when_the_projection_knows_it()
    {
        ProjectMemberLabels labels = new()
        {
            Id = Guid.NewGuid(),
            Labels = [new("root-fingerprint", [NodeId], DisplayName.Parse("Brian"), null)],
        };

        MessagesCommand.SenderCell(NodeId, labels).Should()
            .Be($"{Hall9k.Domain.Infrastructure.Ids.DomainId.Short(NodeId)} (Brian)");
    }

    [Fact]
    public void Shows_the_id_alone_when_the_projection_does_not_know_the_node()
    {
        MessagesCommand.SenderCell(NodeId, projectLabels: null).Should()
            .Be(Hall9k.Domain.Infrastructure.Ids.DomainId.Short(NodeId));
    }

    [Fact]
    public void Escapes_markup_characters_a_label_can_legally_carry()
    {
        ProjectMemberLabels labels = new()
        {
            Id = Guid.NewGuid(),
            Labels = [new("root-fingerprint", [NodeId], DisplayName.Parse("[Brian]"), null)],
        };

        MessagesCommand.SenderCell(NodeId, labels).Should().Contain("[[Brian]]");
    }

    /// <summary>
    /// A node of this machine's own owner shows the id alone (independent pre-PR review, cycle 1,
    /// both lenses, medium): before this fix, <c>SenderCell</c> never passed an owner root
    /// fingerprint through to <see cref="MemberLabelResolver.LabelForNodeId"/> at all, so a message
    /// from this owner's own other node was labelled the same as a message from anyone else's.
    /// </summary>
    [Fact]
    public void Shows_the_id_alone_for_a_node_of_this_machines_own_owner()
    {
        ProjectMemberLabels labels = new()
        {
            Id = Guid.NewGuid(),
            Labels = [new("root-fingerprint", [NodeId], DisplayName.Parse("Brian"), null)],
        };

        MessagesCommand.SenderCell(NodeId, labels, ownRootFingerprint: "root-fingerprint").Should()
            .Be(Hall9k.Domain.Infrastructure.Ids.DomainId.Short(NodeId));
    }
}
