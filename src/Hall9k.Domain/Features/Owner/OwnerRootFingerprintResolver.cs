using Marten;

namespace Hall9k.Domain.Features.Owner;

/// <summary>
/// The identity core's Guid-to-fingerprint mapping (idea 202383dc, ruled 2026-09-12): an owner
/// Guid stays the identifier everywhere a task or run already names one, and this is the one
/// place code resolves what that Guid's owner currently claims as their cross-node root
/// fingerprint, from the Owner stream's own projection. Used both to stamp a fresh assignment or
/// claim event with the fingerprint it carries from here on, and — for one written before that
/// field existed, where it is absent — to resolve the same fingerprint after the fact, so a
/// replicated event compares owners across nodes either way.
/// </summary>
public static class OwnerRootFingerprintResolver
{
    public static async Task<string?> ResolveAsync(
        IQuerySession session, Guid ownerId, CancellationToken cancellationToken) =>
        (await session.LoadAsync<OwnerDetails>(ownerId, cancellationToken))?.RootFingerprint;
}
