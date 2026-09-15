using System.Globalization;
using System.Text;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.Trust;

/// <summary>
/// The ledger's own record of one invite (idea 202383dc, T2):
/// <c>owners/&lt;minter-root&gt;/invites/&lt;invite-id&gt;.yaml</c>, on the same
/// <c>refs/hall9k/ledger/owners/&lt;root&gt;</c> ref <c>root.yaml</c>/<c>nodes/*.yaml</c>/
/// <c>revoked/*.yaml</c> already live on. Carries only what any reader may safely know — the
/// secret's own hash, never the secret — the plaintext lives solely in the minting node's own
/// local store (<c>InviteAggregate.Secret</c>). Shared between <c>Hall9k.Cli</c>'s minting
/// commands and <c>Hall9k.Daemon</c>'s own invite sweep (the one place that later rewrites
/// <see cref="Spent"/> to true) so both read and write the identical shape.
/// </summary>
public sealed record InviteLedgerRecord(string SecretHash, InviteClaimKind Claim, ProjectMemberRole? Role, DateTimeOffset ExpiresAt, bool Spent)
{
    public static string RefName(string minterRootFingerprint) => $"refs/hall9k/ledger/owners/{minterRootFingerprint}";

    public static string PathFor(string minterRootFingerprint, Guid inviteId) => $"owners/{minterRootFingerprint}/invites/{inviteId}.yaml";

    public string ToYaml()
    {
        StringBuilder builder = new();
        builder.Append("secret_hash").Append(": ").AppendLine(Quote(SecretHash));
        builder.Append("claim").Append(": ").AppendLine(Quote(Claim.Value));
        builder.Append("role").Append(": ").AppendLine(Role is null ? "null" : Quote(Role.Value));
        builder.Append("expires_at").Append(": ").AppendLine(Quote(ExpiresAt.ToString("o", CultureInfo.InvariantCulture)));
        builder.Append("spent").Append(": ").AppendLine(Quote(Spent ? "true" : "false"));
        return builder.ToString();
    }

    /// <summary>Reverses <see cref="ToYaml"/> — null on anything malformed, the same "ignore
    /// rather than throw" posture <c>GitLedgerChainReader</c> already gives a ledger file that
    /// does not parse as expected.</summary>
    public static InviteLedgerRecord? Parse(string? yaml)
    {
        if (yaml is null)
        {
            return null;
        }

        string? secretHash = ExtractQuotedYamlValue(yaml, "secret_hash");
        string? claimRaw = ExtractQuotedYamlValue(yaml, "claim");
        string? roleRaw = ExtractQuotedYamlValue(yaml, "role");
        string? expiresRaw = ExtractQuotedYamlValue(yaml, "expires_at");
        string? spentRaw = ExtractQuotedYamlValue(yaml, "spent");

        if (secretHash.IsBlank() || claimRaw.IsBlank() || expiresRaw.IsBlank()
            || !DateTimeOffset.TryParse(expiresRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset expiresAt))
        {
            return null;
        }

        InviteClaimKind claim = claimRaw;
        if (claim == InviteClaimKind.Unknown)
        {
            return null;
        }

        ProjectMemberRole? role = roleRaw.IsBlank() ? null : (ProjectMemberRole)roleRaw;
        bool spent = string.Equals(spentRaw, "true", StringComparison.OrdinalIgnoreCase);
        return new InviteLedgerRecord(secretHash, claim, role, expiresAt, spent);
    }

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    /// <summary>Mirrors <c>GitLedgerChainReader.ExtractQuotedYamlValue</c>'s own small, flat,
    /// every-value-double-quoted reader — duplicated rather than shared across that boundary for
    /// the identical reason its own doc comment already gives for its own duplication.</summary>
    private static string? ExtractQuotedYamlValue(string yaml, string key)
    {
        foreach (string rawLine in yaml.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string prefix = $"{key}: \"";
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith('"'))
            {
                continue;
            }

            string inner = line[prefix.Length..^1];
            return inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        return null;
    }
}
