using System.Security.Cryptography;
using System.Text;

namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// The deterministic Marten stream id for one project's own record of one unverifiable ledger
/// writer, the same reasoning <c>Hall9k.Domain.Features.Message.MessageStreamId</c> already
/// applies: never <see cref="Guid.NewGuid"/>, a stable address derived from a key that already
/// exists (the project, the kind of write, the identifier the write names, and the root
/// fingerprint it names it under) so the identical writer observed on a later sweep folds into
/// the same stream rather than opening a new one every tick.
/// </summary>
public static class UnverifiedLedgerWriteStreamId
{
    public static Guid For(Guid projectId, string kind, string identifier, string rootFingerprint) =>
        Derive("hall9k-unverified-ledger-write", projectId.ToString("N"), kind, identifier, rootFingerprint);

    private static Guid Derive(params string[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return new Guid(hash[..16]);
    }
}
