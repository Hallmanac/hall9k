using Hall9k.Connectors.Ledger;

namespace Hall9k.Connectors.Messaging;

/// <summary>
/// The in-memory <see cref="IMessageTransport"/> every test but A1's own and the chain reader's
/// drives (Brian's 2026-09-13 testing rule). Keeps every envelope a test sent, keyed by repository
/// and sender node, and answers the sender-verification question the same way
/// <see cref="GitLedgerMessageTransport"/> does — by asking <see cref="ILedger"/> (real or
/// <c>FakeLedger</c>) whether the sender has a node file — without ever touching git or a real
/// signature: that half of the check is A1's own (<c>GitLedgerTests</c>), not this transport's, per
/// the acceptance criterion this task ships against.
/// </summary>
public sealed class InMemoryMessageTransport(ILedger ledger) : IMessageTransport
{
    private readonly Dictionary<(string RepositoryPath, Guid NodeId), SortedList<long, string>> outboxes = [];

    public Task SendAsync(
        string repositoryPath,
        Guid fromNodeId,
        long seq,
        string content,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signingKey);

        (string repositoryPath, Guid fromNodeId) key = (repositoryPath, fromNodeId);
        if (!outboxes.TryGetValue(key, out SortedList<long, string>? envelopes))
        {
            envelopes = [];
            outboxes[key] = envelopes;
        }

        if (envelopes.ContainsKey(seq))
        {
            throw new MessageSeqAlreadyUsedException(
                fromNodeId, seq,
                $"Node {fromNodeId} already has an envelope at seq {seq} — seq is allocated once, "
                + "from this node's own store, so this should never happen.");
        }

        envelopes.Add(seq, content);
        return Task.CompletedTask;
    }

    public async Task<TransportReadResult> ReadSinceAsync(
        string repositoryPath, Guid senderNodeId, long sinceSeq, CancellationToken cancellationToken)
    {
        LedgerFile nodeFile = await ledger.ReadAsync(
            repositoryPath, $"refs/hall9k/ledger/nodes/{senderNodeId}", $"nodes/{senderNodeId}/node.yaml",
            cancellationToken);
        if (!nodeFile.Exists)
        {
            return TransportReadResult.SenderNotVouched;
        }

        if (!outboxes.TryGetValue((repositoryPath, senderNodeId), out SortedList<long, string>? envelopes))
        {
            return TransportReadResult.Ok([], sinceSeq);
        }

        List<TransportEnvelope> result = [.. envelopes
            .Where(pair => pair.Key > sinceSeq)
            .OrderBy(pair => pair.Key)
            .Select(pair => new TransportEnvelope(pair.Key, pair.Value))];
        long highestSeqInspected = result.Count > 0 ? result[^1].Seq : sinceSeq;
        return TransportReadResult.Ok(result, highestSeqInspected);
    }
}
