using System.Security.Cryptography;
using System.Text;
using Hall9k.Domain.Infrastructure.Extensions;

namespace Hall9k.Domain.Features.Invite;

/// <summary>
/// The invite-secret shape (idea 202383dc, T2; shortened and re-derived per the 2026-09-26/27
/// security review, idea 6be68ee2): a fixed four-character tag (<see cref="NewShapeTag"/>) followed
/// by 26 lowercase base32 characters encoding a 16-byte (128-bit) CSPRNG tail — 30 characters in
/// all, printed once by the minting command and never written anywhere the ledger holds. Unlike the
/// legacy 162-character shape (still accepted by <see cref="TryParse"/> until every invite minted
/// under it expires), this shape carries no minting root and no invite id of its own — the id is
/// instead DERIVED from the secret itself (<see cref="DeriveId"/>: a <see cref="Guid"/> from the
/// first 16 bytes of <see cref="Hash"/>, which is already public the moment the ledger file exists
/// under it), which is what lets the join drop the root prefix entirely: a joiner who does not yet
/// know which owner root minted an invite can still find it, by listing every
/// <c>refs/hall9k/ledger/owners/*</c> ref and reading <c>invites/&lt;derived-id&gt;.yaml</c> under
/// each rather than being handed the root in cleartext. The secret is generated before the id is
/// ever computed — there is nothing else the id could be derived from.
/// </summary>
public static class InviteSecret
{
    private const char Separator = '.';

    /// <summary>The new shape's fixed prefix — four characters, none of them valid hex digits
    /// (<c>n</c>, <c>t</c>, and <c>v</c> all fall outside 0-9a-f), so a new-shape secret can never be
    /// mistaken for the start of a legacy shape's 64-hex-character root fingerprint.</summary>
    private const string NewShapeTag = "invt";

    /// <summary>128 bits — ample inside the invite's own 72-hour default expiry (Brian's 2026-09-26
    /// ruling), and exactly what 26 base32 characters encode with no padding
    /// (<c>ceil(128 / 5) == 26</c>).</summary>
    private const int NewShapeRandomByteCount = 16;

    private const int Base32TailLength = 26;

    private static readonly int NewShapeLength = NewShapeTag.Length + Base32TailLength;

    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>A fresh, single-use secret — <see cref="NewShapeTag"/> plus 26 base32 characters of a
    /// 16-byte CSPRNG tail, about 30 characters in all. Carries no invite id or minting root of its
    /// own; call <see cref="DeriveId"/> on the result to learn the id it names.</summary>
    public static string Generate() => NewShapeTag + ToBase32(RandomNumberGenerator.GetBytes(NewShapeRandomByteCount));

    /// <summary>The invite id this secret names: a <see cref="Guid"/> built from the first 16 bytes
    /// of <see cref="Hash"/>'s own SHA-256 digest — already public the moment
    /// <c>owners/&lt;root&gt;/invites/&lt;id&gt;.yaml</c> exists, so deriving it from the secret
    /// leaks nothing a reader of the ledger could not already see.</summary>
    public static Guid DeriveId(string secret) => new(SHA256.HashData(Encoding.UTF8.GetBytes(secret)).AsSpan(0, 16));

    /// <summary>
    /// Recognizes both shapes. A new-shape secret yields <paramref name="inviteId"/> via
    /// <see cref="DeriveId"/> and a null <paramref name="legacyMinterRootFingerprint"/> — the caller
    /// has to search for which owner root actually holds it. A legacy 162-character secret
    /// (minting root, invite id, and random tail, dot-joined) yields both directly, exactly as it
    /// always has, so a join against an invite minted before this change still finds it with one
    /// direct read. Never validated against the ledger here, only against the string's own shape; a
    /// secret that parses but names nothing real is the join command's own refusal to report, not
    /// this method's.
    /// </summary>
    public static bool TryParse(string? secret, out Guid inviteId, out string? legacyMinterRootFingerprint)
    {
        inviteId = Guid.Empty;
        legacyMinterRootFingerprint = null;

        if (secret.IsBlank())
        {
            return false;
        }

        if (IsNewShape(secret))
        {
            inviteId = DeriveId(secret);
            return true;
        }

        string[] parts = secret.Split(Separator, 3);
        if (parts.Length != 3 || parts[2].Length == 0 || !IsFingerprintShape(parts[0])
            || !Guid.TryParseExact(parts[1], "N", out Guid parsedId))
        {
            return false;
        }

        legacyMinterRootFingerprint = parts[0];
        inviteId = parsedId;
        return true;
    }

    /// <summary>What <c>owners/&lt;root&gt;/invites/&lt;id&gt;.yaml</c> carries instead of the
    /// secret itself — lets the ledger record "this exact secret was minted" and "is it spent"
    /// without ever holding anything a reader could invert into the secret's own random bytes.
    /// Unchanged by the shortened shape: still SHA-256 of the whole string.</summary>
    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>
    /// What a joining node writes into its own node file's <c>proof</c> field, and what the
    /// minting node's sweep recomputes from the secret it alone holds locally to check a candidate
    /// node file against: HMAC-SHA256 keyed by the secret, over the node's own key fingerprint —
    /// binding the proof to one specific node's key, not merely to knowing the secret, so a
    /// vouched-in node can never be substituted for another that also happened to learn it.
    /// Deliberately unchanged by the 2026-09-26/27 security review: once the sweep only ever vouches
    /// a candidate whose node file is signed by its own key (<c>InviteSweepEngine</c>'s own class
    /// doc), a copied proof can no longer be redirected — the attacker's own file would have to carry
    /// the victim's <c>public_key</c>, which only the victim's own private key can sign — so rebinding
    /// this HMAC to anything else would only strand a joiner against a minting node that has not yet
    /// updated, for no remaining security benefit.
    /// </summary>
    public static string ComputeProof(string secret, string nodeKeyFingerprint) =>
        Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(nodeKeyFingerprint)))
            .ToLowerInvariant();

    private static bool IsNewShape(string secret) =>
        secret.Length == NewShapeLength
        && secret.StartsWith(NewShapeTag, StringComparison.Ordinal)
        && IsBase32(secret.AsSpan(NewShapeTag.Length));

    private static bool IsBase32(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (Base32Alphabet.IndexOf(character) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>RFC 4648 base32, lowercase, no padding — the exact, pinned encoding
    /// <see cref="Generate"/>'s own doc comment names, so <see cref="IsNewShape"/> can check a
    /// candidate secret against it precisely rather than merely by length.</summary>
    private static string ToBase32(byte[] bytes)
    {
        StringBuilder builder = new((bytes.Length * 8 + 4) / 5);
        int buffer = 0;
        int bitsInBuffer = 0;
        foreach (byte b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsInBuffer += 8;
            while (bitsInBuffer >= 5)
            {
                bitsInBuffer -= 5;
                builder.Append(Base32Alphabet[(buffer >> bitsInBuffer) & 0b11111]);
            }
        }

        if (bitsInBuffer > 0)
        {
            builder.Append(Base32Alphabet[(buffer << (5 - bitsInBuffer)) & 0b11111]);
        }

        return builder.ToString();
    }

    /// <summary>The same 64-lowercase-hex shape <c>NodeKeyStore.IsFingerprint</c> checks — duplicated
    /// rather than referenced: Domain cannot reference <c>Hall9k.Connectors</c> (AGENTS.md's own
    /// reference graph), and this is a narrow, already-proven shape cheaper to repeat than to widen
    /// a seam across that boundary for.</summary>
    private static bool IsFingerprintShape(string value) =>
        value.Length == 64 && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
