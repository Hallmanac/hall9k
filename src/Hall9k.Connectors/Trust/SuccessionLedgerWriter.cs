using System.Globalization;
using System.Text;
using Hall9k.Connectors.Ledger;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Connectors.Trust;

/// <summary>
/// Writes the two record kinds this task's succession model (idea 6be68ee2) adds beside an ordinary
/// vouch or revoke: a successor record (<c>owners/&lt;root&gt;/successors/&lt;node-id&gt;.yaml</c>)
/// and a revoked-successor record (<c>owners/&lt;root&gt;/revoked-successors/&lt;node-id&gt;.yaml</c>).
/// Both are written unconditionally, by whichever key the calling command already signs with —
/// <see cref="GitLedgerChainReader"/>'s own succession pass is what decides, on read, whether that
/// key actually was a live root key at the time, the identical "write it, let the reader verify it"
/// shape every other ledger write in this project already follows. Never writes a rotation record:
/// that is the deliberate, loud act a later task's own <c>h9k owner promote</c> performs, not
/// something a vouch or a revoke ever does on its own.
/// </summary>
public static class SuccessionLedgerWriter
{
    private const int MaxConflictRetries = 5;

    /// <summary>
    /// Writes <c>owners/&lt;root&gt;/successors/&lt;nodeId&gt;.yaml</c> naming
    /// <paramref name="nodeId"/> and the exact key already vouched for it — no separate heir keypair,
    /// no node-file field (idea 6be68ee2, journal finding 3): the successor IS the node's own vouched
    /// key. Called at <c>h9k node vouch</c> and at the invite sweep's own node vouch for the node just
    /// vouched, and by the root-node backfill one-shot for a node already vouched before this record
    /// existed — always attempted, whoever is doing the vouching, since only a root-signed one ever
    /// counts on read.
    /// </summary>
    public static Task WriteSuccessorAsync(
        ILedger ledger, string repositoryPath, string root, Guid nodeId, string publicKeyLine, DateTimeOffset issuedAt,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string refName = $"refs/hall9k/ledger/owners/{root}";
        string path = $"owners/{root}/successors/{nodeId}.yaml";
        string content = BuildYaml(
            ("node_id", nodeId.ToString()),
            ("public_key", publicKeyLine),
            ("issued_at", issuedAt.ToString("o", CultureInfo.InvariantCulture)));
        return WriteWithRetryAsync(
            ledger, repositoryPath, refName, path, content, $"Successor {nodeId}", committer, signingKey, cancellationToken);
    }

    /// <summary>
    /// Writes <c>owners/&lt;root&gt;/revoked-successors/&lt;nodeId&gt;.yaml</c> — only counts on read
    /// when signed by a root key ranked above whatever <paramref name="nodeId"/> currently holds
    /// (a pending candidacy, or an already-rotated-in key). Called by <c>h9k node revoke</c> only when
    /// the revoking node's own key is itself currently a live root key of <paramref name="root"/>
    /// (idea 6be68ee2, journal finding 5): an ordinary enrolled node's revoke never writes this.
    /// </summary>
    public static Task WriteRevokedSuccessorAsync(
        ILedger ledger, string repositoryPath, string root, Guid nodeId, DateTimeOffset revokedAt,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string refName = $"refs/hall9k/ledger/owners/{root}";
        string path = $"owners/{root}/revoked-successors/{nodeId}.yaml";
        string content = BuildYaml(
            ("node_id", nodeId.ToString()), ("revoked_at", revokedAt.ToString("o", CultureInfo.InvariantCulture)));
        return WriteWithRetryAsync(
            ledger, repositoryPath, refName, path, content, $"Revoke successor {nodeId}", committer, signingKey, cancellationToken);
    }

    private static async Task WriteWithRetryAsync(
        ILedger ledger, string repositoryPath, string refName, string path, string content, string message,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            if (current.Content == content)
            {
                return;
            }

            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(repositoryPath, refName, path, content, current.BlobId, message, committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return;
            }
        }

        throw new DomainConflictException(
            $"{path} kept changing out from under this write after {MaxConflictRetries} attempts — "
            + "something else is writing it at the same time.");
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
