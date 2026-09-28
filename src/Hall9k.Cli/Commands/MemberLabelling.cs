using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Loads and renders a project's own member labels (task b7d8222e's resolver; task
/// 21c8f2f3's remaining person-facing surfaces) for the CLI commands that name another member by
/// node id beside the id itself — "node 1a2b3c4d (Brian)", the same shape
/// <c>OrchestratorFeedDescription.NodeLine</c> already prints. A small shared seam rather than
/// each command repeating <see cref="Hall9k.Connectors.Orchestrator.OrchestratorFeedReader"/>'s
/// own <c>LabelLookupAsync</c> (a different assembly, so it cannot be referenced from here) —
/// within this assembly, every command below reuses it instead of re-querying the identical
/// single-owner-per-install fact.
/// </summary>
internal static class MemberLabelling
{
    /// <summary>
    /// This project's own current member labels, plus this machine's own owner root fingerprint
    /// (the same "single owner per install" lookup <c>NodeBootstrap.EnsureAsync</c> already relies
    /// on) — so a node-id line about this owner's own fleet reads as a bare id rather than naming
    /// "me" (<see cref="MemberLabelResolver.LabelForNodeId"/>'s own doc).
    /// </summary>
    public static async Task<MemberLabelLookup> LoadAsync(
        IQuerySession session, Guid projectId, CancellationToken cancellationToken)
    {
        ProjectMemberLabels? labels = await session.LoadAsync<ProjectMemberLabels>(projectId, cancellationToken);
        OwnerDetails? owner = (await session.Query<OwnerDetails>().Take(1).ToListAsync(cancellationToken))
            .FirstOrDefault();
        return new MemberLabelLookup(labels, owner?.RootFingerprint);
    }

    /// <summary>
    /// Same lookup, for a caller that has already resolved this machine's own owner root
    /// fingerprint for another reason (a ledger identity, a message envelope) — never a second
    /// query for the identical fact.
    /// </summary>
    public static async Task<MemberLabelLookup> LoadAsync(
        IQuerySession session, Guid projectId, string? ownRootFingerprint, CancellationToken cancellationToken)
    {
        ProjectMemberLabels? labels = await session.LoadAsync<ProjectMemberLabels>(projectId, cancellationToken);
        return new MemberLabelLookup(labels, ownRootFingerprint);
    }

    /// <summary>
    /// A node named by its short id, with the owning member's own label appended in parentheses
    /// when the lookup knows one. Markup-escaped and bounded
    /// (<see cref="MemberLabelResolver.RenderLimit"/>) for a caller whose composed line reaches
    /// <c>AnsiConsole.MarkupLine</c> as literal, unescaped markup rather than through an
    /// auto-escaping interpolated hole — a display name is read from another member's own
    /// self-signed <c>node.yaml</c>, not authored by this node (independent pre-PR review, cycle
    /// 1, both lenses, medium — the same bound every b7d8222e surface already applies).
    /// </summary>
    public static string NodeMarkup(Guid nodeId, MemberLabelLookup labels)
    {
        string id = $"node {DomainId.Short(nodeId)}";
        return labels.LabelForNodeId(nodeId) is { } label
            ? $"{id} ({ExternalText.OneLineMarkup(RelayedText.Truncate(label, MemberLabelResolver.RenderLimit))})"
            : id;
    }

    /// <summary>
    /// The same rendering as <see cref="NodeMarkup"/>, sanitised but not markup-escaped — for a
    /// caller whose composed line is itself passed through an auto-escaping interpolated hole
    /// (<c>AnsiConsole.MarkupLineInterpolated</c>), which would otherwise double-escape the label.
    /// </summary>
    public static string NodeText(Guid nodeId, MemberLabelLookup labels)
    {
        string id = $"node {DomainId.Short(nodeId)}";
        return labels.LabelForNodeId(nodeId) is { } label
            ? $"{id} ({ExternalText.OneLine(RelayedText.Truncate(label, MemberLabelResolver.RenderLimit))})"
            : id;
    }
}
