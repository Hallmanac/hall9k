namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// What a render-time caller hands <see cref="Hall9k.Domain.Features.Orchestrator.OrchestratorFeedDescription.Of"/>
/// and every other surface that names a member or a node (task b7d8222e): one project's own
/// current labels, plus this machine's own owner root fingerprint so a node-id line about this
/// owner's own fleet reads as bare id rather than naming "me". A plain class, never a record —
/// <c>EventScopeRegistryTests</c>' own completeness scan would otherwise mistake it for a
/// candidate event, the same reason every other non-event helper this feature ships is either
/// nested or, like this one, not a record at all.
/// </summary>
public sealed class MemberLabelLookup(ProjectMemberLabels? labels, string? ownRootFingerprint = null)
{
    public static readonly MemberLabelLookup Empty = new(null);

    public string LabelForFingerprint(string fingerprint) =>
        MemberLabelResolver.LabelForFingerprint(labels, fingerprint);

    public string? LabelForNodeId(Guid nodeId) =>
        MemberLabelResolver.LabelForNodeId(labels, nodeId, ownRootFingerprint);
}
