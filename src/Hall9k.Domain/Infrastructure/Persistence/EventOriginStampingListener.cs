using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using JasperFx.Events;
using Marten;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// The one place every appended event is stamped with this process's own origin — the owner
/// root fingerprint and node id — as Marten event metadata headers, never as a change to any
/// event's own shape (idea 202383dc, ruled 2026-09-12). Registered once, centrally, in
/// <see cref="MartenConfiguration.ConfigureHall9k"/>, one instance per <see cref="StoreOptions"/>.
/// <para>
/// Both values are resolved fresh from the session's own store on every
/// <see cref="BeforeSaveChangesAsync"/> call rather than cached once at bootstrap: a long-lived
/// daemon session outlives an owner's own root claim (or its retirement, <c>h9k project join
/// --owner</c>), and several short-lived CLI commands (<c>task verify</c>, <c>task release</c>,
/// <c>task register-session</c>) append events without ever calling
/// <c>Hall9k.Domain.Infrastructure.Bootstrap.NodeBootstrap.EnsureAsync</c> first. Resolving here,
/// from whichever session is actually saving, is correct for both without either caller needing
/// to know about this listener at all (cycle-1 pre-PR review, both lenses).
/// </para>
/// <para>
/// The store alone is not enough, though: Marten runs this listener before the save's SQL batch
/// executes, so a query here cannot see a <see cref="NodeDetails"/> or <see cref="OwnerDetails"/>
/// row the same save is about to write. On a fresh install nearly every command's first save is
/// exactly that shape (<c>NodeBootstrap.EnsureAsync</c> starts the Owner and Node streams, then the
/// command appends its own event to the same session), and <c>h9k project join</c> claims a root
/// and appends <see cref="NodeOwnerClaimed"/> in one save too. So the batch's own pending
/// <see cref="OwnerRegistered"/>, <see cref="NodeRegistered"/>, and <see cref="OwnerRootClaimed"/>
/// events are folded over what the store returned, and every event in the batch carries the
/// origin as it stands once this save commits (cycle-2 pre-PR review, conformance lens: without
/// the fold, a fresh install's first events were stamped with an empty node id and fingerprint,
/// permanently).
/// </para>
/// </summary>
public sealed class EventOriginStampingListener : DocumentSessionListenerBase
{
    public const string NodeIdHeader = "nodeId";
    public const string OwnerRootFingerprintHeader = "ownerRootFingerprint";

    /// <summary>
    /// Stamped when no owner has claimed a root yet at append time — a real, reachable state
    /// this listener cannot wait out: the bootstrap save that starts the Owner and Node streams
    /// commits before <c>h9k project join</c>'s own root claim ever runs (a separate, later save,
    /// since Marten can only <c>AggregateStreamAsync</c> a stream once it is actually committed),
    /// and a project registered with <c>--no-home</c> or an unreachable repository defers that
    /// join to a disconnected command an operator runs by hand, possibly much later. Never
    /// rewritten once appended — this is the one place that stamping happens, and an append-only
    /// event store has nowhere to rewrite a header into after the fact. Distinct from a real
    /// fingerprint (never empty in practice: it is a lowercase hex SHA-256 digest) so a later
    /// reader can tell "genuinely unclaimed at append time" apart from a value it can trust, and
    /// fall back the same way an event written before <see cref="Hall9k.Domain.Features.Tasks.Events.TaskAssigned.AssignedOwnerRootFingerprint"/>
    /// existed already does: resolve the owner's current root through
    /// <see cref="Hall9k.Domain.Features.Owner.OwnerRootFingerprintResolver.ResolveAsync"/> against
    /// whichever owner id the event's own stream or fields carry, rather than trust this sentinel
    /// as a final answer.
    /// </summary>
    public const string UnclaimedOwnerRootFingerprint = "";

    public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken cancellationToken)
    {
        List<StreamAction> streams = [.. session.PendingChanges.Streams()];
        if (streams.Count == 0)
        {
            return;
        }

        List<object> pendingEvents = [.. streams.SelectMany(s => s.Events).Select(e => e.Data)];

        string machineName = Environment.MachineName;
        NodeDetails? node = (await session.Query<NodeDetails>()
            .Where(n => n.MachineName == machineName)
            .Take(1).ToListAsync(cancellationToken)).FirstOrDefault();
        NodeRegistered? pendingNodeRegistration = pendingEvents.OfType<NodeRegistered>()
            .FirstOrDefault(r => r.MachineName == machineName);

        Guid? nodeId = node?.Id ?? pendingNodeRegistration?.Id;

        // This node's own owner, never an arbitrary one: NodeDetails.OwnerId (or the pending
        // NodeRegistered on a first save) is the one place that mapping lives, and a platform
        // that genuinely supports more than one registered owner (OwnerResolver's own "more than
        // one owner is registered, so name the one you mean") would otherwise get whichever
        // OwnerDetails row Postgres happened to return first from an unfiltered query — every
        // event silently stamped with a different owner's root fingerprint (follow-up review
        // finding, PR #370).
        Guid? ownerId = node?.OwnerId ?? pendingNodeRegistration?.OwnerId;
        OwnerDetails? owner = ownerId is { } resolvedOwnerId
            ? await session.LoadAsync<OwnerDetails>(resolvedOwnerId, cancellationToken)
            : null;
        string? ownerRootFingerprint = pendingEvents.OfType<OwnerRootClaimed>()
            .LastOrDefault(c => c.Id == ownerId)?.RootFingerprint
            ?? owner?.RootFingerprint;

        string nodeIdHeaderValue = (nodeId ?? Guid.Empty).ToString();
        string ownerRootFingerprintHeaderValue = ownerRootFingerprint ?? UnclaimedOwnerRootFingerprint;

        foreach (StreamAction stream in streams)
        {
            foreach (IEvent @event in stream.Events)
            {
                @event.SetHeader(NodeIdHeader, nodeIdHeaderValue);
                @event.SetHeader(OwnerRootFingerprintHeader, ownerRootFingerprintHeaderValue);
            }
        }
    }
}
