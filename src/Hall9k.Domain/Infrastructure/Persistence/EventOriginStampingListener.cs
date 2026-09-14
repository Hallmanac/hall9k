using JasperFx.Events;
using Marten;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// The one place every appended event is stamped with this process's own origin — the owner
/// root fingerprint and node id — as Marten event metadata headers, never as a change to any
/// event's own shape (idea 202383dc, ruled 2026-09-12). Registered once, centrally, in
/// <see cref="MartenConfiguration.ConfigureHall9k"/>, one instance per <see cref="StoreOptions"/>.
/// <para>
/// <see cref="NodeId"/> and <see cref="OwnerRootFingerprint"/> are mutable because this listener
/// exists before identity resolves — the store is configured before
/// <c>Hall9k.Domain.Infrastructure.Bootstrap.NodeBootstrap.EnsureAsync</c> ever runs. That method
/// is the one place both are set, by finding this listener on the session's own store
/// (<c>session.DocumentStore.Options.Listeners</c>) once it resolves this node's id and its
/// owner's root fingerprint. A short-lived CLI store resolves identity fresh on its one command;
/// a long-lived daemon store resolves it once at startup and every later session from that same
/// store reads the same, still-correct values, since node and owner identity do not change
/// during a process's own lifetime.
/// </para>
/// </summary>
public sealed class EventOriginStampingListener : DocumentSessionListenerBase
{
    public const string NodeIdHeader = "nodeId";
    public const string OwnerRootFingerprintHeader = "ownerRootFingerprint";

    public Guid? NodeId { get; set; }

    public string? OwnerRootFingerprint { get; set; }

    public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken cancellationToken)
    {
        foreach (StreamAction stream in session.PendingChanges.Streams())
        {
            foreach (IEvent @event in stream.Events)
            {
                @event.SetHeader(NodeIdHeader, (NodeId ?? Guid.Empty).ToString());
                @event.SetHeader(OwnerRootFingerprintHeader, OwnerRootFingerprint ?? string.Empty);
            }
        }

        return Task.CompletedTask;
    }
}
