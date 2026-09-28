using Hall9k.Domain.Infrastructure.Extensions;

namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// The one place a fingerprint or a node id is turned into the label a feed, courier, or message
/// surface names a member by (task b7d8222e) — answered from <see cref="ProjectMemberLabels"/>
/// alone, with no ledger read.
/// </summary>
public static class MemberLabelResolver
{
    /// <summary>
    /// The bound every surface applies to a resolved label before it reaches a terminal
    /// (independent pre-PR review, cycle 1, both lenses, medium): a display name is read from
    /// another member's own self-signed <c>node.yaml</c> through <c>DisplayName.Trusted</c>, which
    /// skips <c>DisplayName.Parse</c>'s own length and control-character rule, and a declared login
    /// is read with no rule at all — so the value this method returns can carry a raw escape
    /// sequence or run to any length. The same bound <c>ProjectMembersCommand.RenderDisplayName</c>
    /// already applies to the identical value.
    /// </summary>
    public const int RenderLimit = 64;

    /// <summary>
    /// The label for <paramref name="fingerprint"/> in one project: its member's newest display
    /// name, else their newest declared login, else the fingerprint's own short form — the honest
    /// fallback for a fingerprint the projection has never recorded a label for (an unknown member,
    /// or a daemon that has not swept since this member joined).
    /// </summary>
    public static string LabelForFingerprint(ProjectMemberLabels? labels, string fingerprint)
    {
        ProjectMemberLabel? label = Find(labels, fingerprint);
        if (label?.DisplayName.HasValue == true)
        {
            return label.DisplayName.Value;
        }

        if (label?.DeclaredLogin.IsNotBlank() == true)
        {
            return label.DeclaredLogin!;
        }

        return ShortFingerprint(fingerprint);
    }

    /// <summary>
    /// The label for the member who currently owns <paramref name="nodeId"/> in one project, or
    /// null when the projection names no member with that node in their fleet — an unknown node,
    /// or one revoked out of every fleet the projection still knows. <paramref name="ownRootFingerprint"/>,
    /// when given, also reads as unknown for that owner's own fleet: a node-id line about this
    /// machine's own owner already reads as "me" without a label (Brian's 2026-09-26 ruling: "a
    /// courier or feed line about ANOTHER member's node names them by display name").
    /// </summary>
    public static string? LabelForNodeId(ProjectMemberLabels? labels, Guid nodeId, string? ownRootFingerprint = null)
    {
        ProjectMemberLabel? owning = labels?.Labels.FirstOrDefault(label => label.FleetNodeIds.Contains(nodeId));
        return owning is null || owning.RootFingerprint == ownRootFingerprint
            ? null
            : LabelForFingerprint(labels, owning.RootFingerprint);
    }

    private static ProjectMemberLabel? Find(ProjectMemberLabels? labels, string fingerprint) =>
        labels?.Labels.FirstOrDefault(label => label.RootFingerprint == fingerprint);

    /// <summary>The same short prefix <c>PublishedFacts.HeldElsewhereFact</c> and the orchestrator
    /// feed's own pre-existing fallback already use for a fingerprint with nothing friendlier to
    /// print.</summary>
    private static string ShortFingerprint(string fingerprint) => fingerprint[..Math.Min(12, fingerprint.Length)];
}
