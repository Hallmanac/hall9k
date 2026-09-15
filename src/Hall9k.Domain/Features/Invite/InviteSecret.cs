using System.Security.Cryptography;
using System.Text;
using Hall9k.Domain.Infrastructure.Extensions;

namespace Hall9k.Domain.Features.Invite;

/// <summary>
/// The whole invite-secret shape (idea 202383dc, T2): a single opaque string, printed once by the
/// minting command and never written anywhere the ledger holds — "keeps it only in the minting
/// node's store". Deliberately self-describing (the minting root's own fingerprint and the invite's
/// own id ride along in cleartext, only the trailing random component is secret) so
/// <c>h9k project join --invite &lt;secret&gt;</c> can locate and read
/// <c>owners/&lt;root&gt;/invites/&lt;id&gt;.yaml</c> with a single, already-known-path
/// <see cref="Hall9k.Connectors.Ledger.ILedger.ReadAsync"/> call — no enumeration of every owner
/// root's own invites folder, and no second round trip to learn which root or which invite a
/// human-typed secret even belongs to. Leaking the root and the invite id costs nothing: both are
/// already public the moment the ledger file exists (its own ref name and path), and 32 random
/// bytes of trailing entropy is what actually gates a claim — nobody without the exact secret can
/// reproduce <see cref="ComputeProof"/>'s HMAC for any node's own key fingerprint.
/// </summary>
public static class InviteSecret
{
    private const char Separator = '.';
    private const int RandomByteCount = 32;

    /// <summary>A fresh, single-use secret for <paramref name="inviteId"/>, minted under
    /// <paramref name="minterRootFingerprint"/>'s own invites folder.</summary>
    public static string Generate(string minterRootFingerprint, Guid inviteId)
    {
        byte[] randomBytes = RandomNumberGenerator.GetBytes(RandomByteCount);
        return string.Join(
            Separator, minterRootFingerprint, inviteId.ToString("N"), Convert.ToHexString(randomBytes).ToLowerInvariant());
    }

    /// <summary>
    /// Splits a secret back into the minting root's fingerprint and the invite id it names —
    /// never validated against the ledger here, only against this string's own shape; a secret
    /// that parses but names nothing real is the join command's own refusal to report, not this
    /// method's.
    /// </summary>
    public static bool TryParse(string? secret, out string minterRootFingerprint, out Guid inviteId)
    {
        minterRootFingerprint = string.Empty;
        inviteId = Guid.Empty;

        if (secret.IsBlank())
        {
            return false;
        }

        string[] parts = secret.Split(Separator, 3);
        if (parts.Length != 3 || parts[2].Length == 0 || !IsFingerprintShape(parts[0])
            || !Guid.TryParseExact(parts[1], "N", out Guid parsedId))
        {
            return false;
        }

        minterRootFingerprint = parts[0];
        inviteId = parsedId;
        return true;
    }

    /// <summary>What <c>owners/&lt;root&gt;/invites/&lt;id&gt;.yaml</c> carries instead of the
    /// secret itself — lets the ledger record "this exact secret was minted" and "is it spent"
    /// without ever holding anything a reader could invert into the secret's own random bytes.</summary>
    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>
    /// What a joining node writes into its own node file's <c>proof</c> field, and what the
    /// minting node's sweep recomputes from the secret it alone holds locally to check a candidate
    /// node file against: HMAC-SHA256 keyed by the secret, over the node's own key fingerprint —
    /// binding the proof to one specific node's key, not merely to knowing the secret, so a
    /// vouched-in node can never be substituted for another that also happened to learn it.
    /// </summary>
    public static string ComputeProof(string secret, string nodeKeyFingerprint) =>
        Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(nodeKeyFingerprint)))
            .ToLowerInvariant();

    /// <summary>The same 64-lowercase-hex shape <c>NodeKeyStore.IsFingerprint</c> checks — duplicated
    /// rather than referenced: Domain cannot reference <c>Hall9k.Connectors</c> (AGENTS.md's own
    /// reference graph), and this is a narrow, already-proven shape cheaper to repeat than to widen
    /// a seam across that boundary for.</summary>
    private static bool IsFingerprintShape(string value) =>
        value.Length == 64 && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
