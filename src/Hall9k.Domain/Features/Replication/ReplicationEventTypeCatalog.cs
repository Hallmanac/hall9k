using Hall9k.Domain.Infrastructure.Persistence;

namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// Resolves a replicated event's own <see cref="Type.FullName"/> back to a real .NET type, using
/// the identical set <see cref="EventScopeRegistry"/> already classifies — every event type this
/// platform ships, and no others, so a receiving node never deserializes into an arbitrary type a
/// malicious or corrupted envelope named. Built once and cached: <see cref="EventScopeRegistry.KnownEventTypes"/>
/// never changes at runtime.
/// </summary>
public static class ReplicationEventTypeCatalog
{
    private static readonly IReadOnlyDictionary<string, Type> ByFullName = EventScopeRegistry.KnownEventTypes
        .Where(type => type.FullName is not null)
        .ToDictionary(type => type.FullName!, type => type);

    /// <summary>Null when <paramref name="fullName"/> names no type this build's own registry knows
    /// — idea 202383dc's own "an unknown kind is stored and skipped, never refused" extends here:
    /// a receiver on an older build than the sender skips a type it does not know rather than
    /// failing the whole batch.</summary>
    public static Type? Resolve(string fullName) => ByFullName.GetValueOrDefault(fullName);
}
