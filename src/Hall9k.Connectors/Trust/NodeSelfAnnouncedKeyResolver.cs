using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Connectors.Trust;

/// <summary>
/// Resolves a node's own self-announced device-key fingerprint from its own <c>node.yaml</c>
/// (idea 202383dc, A2a) — the identical read <c>GitLedgerMessageTransport.ReadSinceAsync</c>
/// already performs to authenticate a sender's outbox before ever trusting its content, exposed
/// here so a caller that already trusts <c>nodeId</c> (because <c>MessageInbox</c> only ever
/// stores a message under the sender whose outbox it actually read it from) can go one step
/// further and ask which owner root the ledger's own trust chain currently vouches that exact
/// device key under — <see cref="TrustedOwner.ContainsForNode"/> is that answer, once this
/// resolves the key. Null on anything short of a genuine, well-formed key: no node file, no
/// <c>public_key</c> line, or one that does not parse — a caller reads null the same way it
/// would an outright verification failure, never as "trust it anyway".
/// </summary>
public static class NodeSelfAnnouncedKeyResolver
{
    public static async Task<string?> ResolveFingerprintAsync(
        ILedger ledger, string repositoryPath, Guid nodeId, CancellationToken cancellationToken)
    {
        string refName = $"refs/hall9k/ledger/nodes/{nodeId}";
        string path = $"nodes/{nodeId}/node.yaml";
        LedgerFile nodeFile = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
        if (!nodeFile.Exists)
        {
            return null;
        }

        string? publicKeyLine = ExtractQuotedYamlValue(nodeFile.Content!, "public_key");
        if (publicKeyLine is null)
        {
            return null;
        }

        try
        {
            return NodeKeyStore.Fingerprint(publicKeyLine);
        }
        catch (DomainValidationException)
        {
            return null;
        }
    }

    /// <summary>Reverses <c>ProjectJoinCommand</c>'s own <c>QuoteYaml</c> — the same small, flat,
    /// every-value-double-quoted YAML shape node.yaml is written in (A2a), the identical parsing
    /// <c>GitLedgerMessageTransport</c>'s own copy already implements for the same file.</summary>
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
