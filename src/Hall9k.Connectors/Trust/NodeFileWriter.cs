using System.Globalization;
using System.Text;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Connectors.Trust;

/// <summary>
/// The GitHub account a node declares for itself in its own signed <c>node.yaml</c>
/// (<c>github_login</c> and <c>github_account_id</c>): a self-declared claim, never proof. The
/// numeric id is what identifies the account across a rename; the login is what people read.
/// </summary>
public sealed record DeclaredGitHubAccount(long AccountId, string Login);

/// <summary>What <see cref="NodeFileWriter.RefreshGitHubDeclarationAsync"/> did.</summary>
public enum NodeFileRefreshOutcome
{
    /// <summary>The node has no <c>node.yaml</c> in this project, so nothing was written; the refresh never creates one.</summary>
    NoNodeFile,

    /// <summary>The file already carries exactly the account observed, so nothing was written.</summary>
    Unchanged,

    /// <summary>The two declaration fields were added or changed and the commit landed.</summary>
    Written,

    /// <summary>
    /// The file names a different public key than the one this node would sign with (its key was
    /// regenerated since the join), so nothing was written: a commit signed by another key than the
    /// file's own would fail the file's self-signature rule and cost the node its attachment.
    /// </summary>
    SigningKeyDiffers,
}

/// <summary>
/// The one writer of a node's own <c>nodes/&lt;id&gt;/node.yaml</c>, shared by <c>h9k project
/// join</c> (which writes the whole file) and the daemon's one-shot refresh (which changes only the
/// GitHub declaration in a file that already exists). Kept beside
/// <see cref="NodeSelfAnnouncedKeyResolver"/>, the reader of the same file.
/// </summary>
public static class NodeFileWriter
{
    /// <summary>How many times a conflicting ledger write retries against a fresh read before giving up.</summary>
    private const int MaxConflictRetries = 5;

    private const string LoginKey = "github_login";
    private const string AccountIdKey = "github_account_id";

    /// <summary>
    /// Writes the whole node file from this call's own arguments, returning whether a commit landed.
    /// When <paramref name="github"/> is null (gh could not answer), an account the existing file
    /// already declares is carried forward rather than erased, so a join that cannot read gh never
    /// removes a claim an earlier run made.
    /// </summary>
    public static async Task<bool> WriteAsync(
        ILedger ledger, string repositoryPath, Guid nodeId, NodeSigningKey key, string claimedOwnerFingerprint,
        string machineName, string operatingSystem, DateTimeOffset joinedAt, string? inviteProof,
        DeclaredGitHubAccount? github, LedgerCommitter committer, LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        string refName = RefName(nodeId);
        string path = NodePath(nodeId);

        // content depends only on this call's own arguments and, for a declaration gh could not
        // re-read, the file's own current one, so a Conflict (something else wrote node.yaml between
        // the read and the write) is always safe to retry against a fresh tip. The caller saves the
        // local identity events on the strength of this file actually landing, so a lost Conflict
        // silently treated as "nothing to write" would let that claim commit while node.yaml still
        // held the old facts.
        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            DeclaredGitHubAccount? declared = github ?? (current.Exists ? ReadDeclaration(current.Content!) : null);

            string content = BuildYaml(
                ("node_id", nodeId.ToString()),
                ("public_key", key.PublicKeyLine),
                ("key_fingerprint", key.Fingerprint),
                ("owner_fingerprint", claimedOwnerFingerprint),
                ("machine_name", machineName),
                ("operating_system", operatingSystem),
                ("joined_at", joinedAt.ToString("o", CultureInfo.InvariantCulture)),
                ("invite_proof", inviteProof),
                (LoginKey, declared?.Login),
                (AccountIdKey, declared?.AccountId.ToString(CultureInfo.InvariantCulture)));

            if (current.Content == content)
            {
                return false;
            }

            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, refName, path, content, current.BlobId,
                    current.Exists ? "Update node facts" : "Join node", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return true;
            }
        }

        throw new DomainConflictException(
            $"node.yaml for node {nodeId} kept changing out from under this join after "
            + $"{MaxConflictRetries} attempts — something else is writing it at the same time. "
            + "Re-run h9k project join once that settles.");
    }

    /// <summary>
    /// Brings the GitHub declaration in an existing node file up to <paramref name="account"/>,
    /// changing only <c>github_login</c> and <c>github_account_id</c> and leaving every other line
    /// exactly as read (<c>invite_proof</c> included: the invite sweep on the minting node matches on
    /// it, so regenerating the file from arguments would erase a pending proof). Never creates a
    /// node file: the invite sweep treats a new one as a join candidate. Writes only when the file's
    /// own <c>public_key</c> is <paramref name="signingPublicKeyLine"/>, the key that will sign the commit.
    /// </summary>
    public static async Task<NodeFileRefreshOutcome> RefreshGitHubDeclarationAsync(
        ILedger ledger, string repositoryPath, Guid nodeId, DeclaredGitHubAccount account,
        string signingPublicKeyLine, LedgerCommitter committer, LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        string refName = RefName(nodeId);
        string path = NodePath(nodeId);

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            if (!current.Exists)
            {
                return NodeFileRefreshOutcome.NoNodeFile;
            }

            if (!NamesSameKey(GitLedgerChainReader.ExtractQuotedYamlValue(current.Content!, "public_key"), signingPublicKeyLine))
            {
                return NodeFileRefreshOutcome.SigningKeyDiffers;
            }

            if (ReadDeclaration(current.Content!) == account)
            {
                return NodeFileRefreshOutcome.Unchanged;
            }

            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, refName, path, WithDeclaration(current.Content!, account), current.BlobId,
                    "Update node facts", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return NodeFileRefreshOutcome.Written;
            }
        }

        throw new DomainConflictException(
            $"node.yaml for node {nodeId} kept changing out from under the GitHub declaration refresh after "
            + $"{MaxConflictRetries} attempts.");
    }

    /// <summary>Whether two public key lines carry the same key: the type and base64 fields, ignoring the trailing comment.</summary>
    private static bool NamesSameKey(string? fileLine, string signingLine)
    {
        if (fileLine is null)
        {
            return false;
        }

        string[] file = fileLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] signing = signingLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return file.Length >= 2 && signing.Length >= 2 && file[0] == signing[0] && file[1] == signing[1];
    }

    /// <summary>The declaration a node file carries, or null when either field is absent or malformed (a file written before the declaration existed reads this way).</summary>
    internal static DeclaredGitHubAccount? ReadDeclaration(string yaml) =>
        GitLedgerChainReader.ExtractQuotedYamlValue(yaml, LoginKey) is { Length: > 0 } login
        && GitLedgerChainReader.ExtractQuotedYamlValue(yaml, AccountIdKey) is { } rawId
        && long.TryParse(rawId, NumberStyles.None, CultureInfo.InvariantCulture, out long accountId)
            ? new DeclaredGitHubAccount(accountId, login)
            : null;

    /// <summary>
    /// <paramref name="yaml"/> with the two declaration lines replaced in place when present and
    /// appended when not, every other byte untouched. New lines use the file's own line ending, so
    /// a file written on Windows stays consistently CRLF.
    /// </summary>
    private static string WithDeclaration(string yaml, DeclaredGitHubAccount account)
    {
        string newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string loginLine = $"{LoginKey}: {QuoteYaml(account.Login)}";
        string idLine = $"{AccountIdKey}: {QuoteYaml(account.AccountId.ToString(CultureInfo.InvariantCulture))}";

        bool sawLogin = false;
        bool sawId = false;
        string[] lines = yaml.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string carriageReturn = lines[index].EndsWith('\r') ? "\r" : string.Empty;
            if (lines[index].StartsWith($"{LoginKey}:", StringComparison.Ordinal))
            {
                lines[index] = loginLine + carriageReturn;
                sawLogin = true;
            }
            else if (lines[index].StartsWith($"{AccountIdKey}:", StringComparison.Ordinal))
            {
                lines[index] = idLine + carriageReturn;
                sawId = true;
            }
        }

        StringBuilder builder = new(string.Join('\n', lines));
        if ((!sawLogin || !sawId) && builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append(newline);
        }

        if (!sawLogin)
        {
            builder.Append(loginLine).Append(newline);
        }

        if (!sawId)
        {
            builder.Append(idLine).Append(newline);
        }

        return builder.ToString();
    }

    private static string RefName(Guid nodeId) => $"refs/hall9k/ledger/nodes/{nodeId}";

    private static string NodePath(Guid nodeId) => $"nodes/{nodeId}/node.yaml";

    /// <summary>
    /// A small, flat YAML document: every value double-quoted (a machine name or a comment on a
    /// public key line can carry spaces or colons a bare scalar would misparse), <c>null</c>
    /// rendered as the bare YAML null literal for the reserved invite-proof field, and a field whose
    /// value is null and whose name is a GitHub declaration key omitted altogether, so a node file
    /// written before a declaration existed keeps reading as one. Deliberately not a general YAML
    /// writer.
    /// </summary>
    private static string BuildYaml(params (string Key, string? Value)[] fields)
    {
        StringBuilder builder = new();
        foreach ((string key, string? value) in fields)
        {
            if (value is null && key is LoginKey or AccountIdKey)
            {
                continue;
            }

            builder.Append(key).Append(": ").AppendLine(value is null ? "null" : QuoteYaml(value));
        }

        return builder.ToString();
    }

    private static string QuoteYaml(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
