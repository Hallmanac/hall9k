using FluentAssertions;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="MemberLabelResolver"/> (task b7d8222e): answers a fingerprint's or a node's own
/// label from a <see cref="ProjectMemberLabels"/> projection alone — no ledger read.
/// </summary>
public sealed class MemberLabelResolverTests
{
    private const string Fingerprint = "abcdef0123456789";
    private static readonly Guid NodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProjectA = Guid.Parse("01a0bc05-a960-7657-b708-1aed4a1b2c3d");

    private static ProjectMemberLabels LabelsWith(Guid projectId, DisplayName displayName, string? login) => new()
    {
        Id = projectId,
        Labels = [new ProjectMemberLabel(Fingerprint, [NodeId], displayName, login)],
    };

    [Fact]
    public void LabelForFingerprint_PrefersTheDisplayNameOverTheLoginOverTheShortFingerprint()
    {
        ProjectMemberLabels labels = LabelsWith(ProjectA, DisplayName.Parse("Brian"), "brianhallmanac");

        MemberLabelResolver.LabelForFingerprint(labels, Fingerprint).Should().Be("Brian");
    }

    [Fact]
    public void LabelForFingerprint_FallsBackToTheLoginWhenNoDisplayNameIsSet()
    {
        ProjectMemberLabels labels = LabelsWith(ProjectA, DisplayName.None, "brianhallmanac");

        MemberLabelResolver.LabelForFingerprint(labels, Fingerprint).Should().Be("brianhallmanac");
    }

    [Fact]
    public void LabelForFingerprint_FallsBackToTheShortFingerprintWhenNeitherIsSet()
    {
        ProjectMemberLabels labels = LabelsWith(ProjectA, DisplayName.None, null);

        MemberLabelResolver.LabelForFingerprint(labels, Fingerprint).Should().Be("abcdef012345");
    }

    [Fact]
    public void LabelForFingerprint_OfAnUnknownFingerprintFallsBackToItsShortForm()
    {
        MemberLabelResolver.LabelForFingerprint(labels: null, Fingerprint).Should().Be("abcdef012345");
    }

    [Fact]
    public void LabelForNodeId_OfAnUnknownNodeResolvesToNothing()
    {
        ProjectMemberLabels labels = LabelsWith(ProjectA, DisplayName.Parse("Brian"), "brianhallmanac");

        MemberLabelResolver.LabelForNodeId(labels, Guid.Parse("99999999-9999-9999-9999-999999999999")).Should().BeNull();
    }

    [Fact]
    public void LabelForNodeId_ResolvesToTheOwningMembersLabel()
    {
        ProjectMemberLabels labels = LabelsWith(ProjectA, DisplayName.Parse("Brian"), "brianhallmanac");

        MemberLabelResolver.LabelForNodeId(labels, NodeId).Should().Be("Brian");
    }

    [Fact]
    public void LabelForNodeId_OfThisMachinesOwnOwnerResolvesToNothing()
    {
        ProjectMemberLabels labels = LabelsWith(ProjectA, DisplayName.Parse("Brian"), "brianhallmanac");

        MemberLabelResolver.LabelForNodeId(labels, NodeId, ownRootFingerprint: Fingerprint).Should().BeNull(
            "a node-id line about this machine's own owner already reads as \"me\"");
    }
}
