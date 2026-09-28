using System.Globalization;
using System.Text;
using Hall9k.Connectors.Ledger;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.Trust;

/// <summary>
/// Writes <c>members/&lt;candidateOwnerFingerprint&gt;.yaml</c> — the one write idea 1bb803e1
/// restricts to a live root key of the project's own owner. Shared by
/// <c>Hall9k.Daemon.Invites.InviteSweepEngine</c>'s own root-direct path (this node's key
/// already is a live root key) and the owner-act watch loop's root-side reaction to a non-root
/// node's own request (idea 6be68ee2, companion 1bb803e1: "minting is a request, not a local
/// capability") - one write, one place it is actually made, whichever caller decided it may run.
/// Named in plain <c>&lt;c&gt;</c> text rather than <c>&lt;see cref&gt;</c> (independent pre-PR
/// review, cycle 1, conformance lens, low): this project, Hall9k.Connectors, does not reference
/// Hall9k.Daemon, so a cref naming a Daemon type here can never resolve.
/// </summary>
public static class MemberVouchLedgerWriter
{
    private const string MembersRefName = "refs/hall9k/ledger/members";
    private const int MaxConflictRetries = 5;

    /// <summary>
    /// Writes (or, on a retry whose content is already byte-identical - the same candidate, role,
    /// and <paramref name="issuedAt"/> a first attempt already used - cheaply confirms without a new
    /// commit) <c>members/&lt;candidateOwnerFingerprint&gt;.yaml</c>. Returns the commit id the write
    /// actually landed under, or null when the content already matched and nothing new was written.
    /// </summary>
    public static async Task<string?> WriteAsync(
        ILedger ledger, string repositoryPath, string candidateOwnerFingerprint, ProjectMemberRole role,
        DateTimeOffset issuedAt, LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string path = $"members/{candidateOwnerFingerprint}.yaml";
        string content = BuildYaml(
            ("root_fingerprint", candidateOwnerFingerprint),
            ("role", role.Value),
            ("issued_at", issuedAt.ToString("o", CultureInfo.InvariantCulture)));

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, MembersRefName, path, cancellationToken);
            if (current.Content == content)
            {
                return null;
            }

            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, MembersRefName, path, content, current.BlobId,
                    $"Vouch member {candidateOwnerFingerprint} (invite)", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return outcome.CommitId;
            }
        }

        throw new InvalidOperationException(
            $"{path} kept changing out from under this write after {MaxConflictRetries} attempts - "
            + "something else is writing it at the same time; will retry next sweep.");
    }

    private static string BuildYaml(params (string Key, string Value)[] fields)
    {
        StringBuilder builder = new();
        foreach ((string key, string value) in fields)
        {
            builder.Append(key).Append(": \"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).AppendLine("\"");
        }

        return builder.ToString();
    }
}
