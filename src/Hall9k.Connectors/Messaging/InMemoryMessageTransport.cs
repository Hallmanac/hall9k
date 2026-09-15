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

        List<TransportEnvelope> candidates = [.. envelopes
            .Where(pair => pair.Key > sinceSeq)
            .OrderBy(pair => pair.Key)
            .Select(pair => new TransportEnvelope(pair.Key, pair.Value))];

        // Mirrors GitLedgerMessageTransport's own gap-stop rule (Brian's 2026-09-13 testing rule
        // reserves a real repository for that transport's own tests, so this is the one place a
        // test can actually exercise the rule): a numeric gap in the sender's own seq sequence — a
        // failed send with no resend yet, most often, since this fake never fabricates forgery or
        // corruption — is never trusted as "inspected". The cursor stops short of it and
        // stalledAtSeq tells the caller there is unreached content rather than genuinely nothing new.
        List<TransportEnvelope> result = [];
        long highestSeqInspected = sinceSeq;
        long? stalledAtSeq = null;
        foreach (TransportEnvelope candidate in candidates)
        {
            if (candidate.Seq != highestSeqInspected + 1)
            {
                stalledAtSeq = candidate.Seq;
                break;
            }

            result.Add(candidate);
            highestSeqInspected = candidate.Seq;
        }

        return TransportReadResult.Ok(result, highestSeqInspected, stalledAtSeq: stalledAtSeq);
    }
}
