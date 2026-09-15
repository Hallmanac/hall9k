using System.Globalization;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Connectors.Messaging;

/// <summary>
/// The in-memory <see cref="IMessageTransport"/> every test but A1's own and the chain reader's
/// drives (Brian's 2026-09-13 testing rule). Keeps every envelope a test sent, keyed by repository
/// and sender node, and answers the sender-verification question the same way
/// <see cref="GitLedgerMessageTransport"/> does — by asking <see cref="ILedger"/> (real or
/// <c>FakeLedger</c>) whether the sender has a node file — without ever touching git or a real
/// signature: that half of the check is A1's own (<c>GitLedgerTests</c>), not this transport's.
/// <para>
/// <paramref name="chainReader"/> is optional, defaulting to null: a test that does not care about
/// chain-level trust (the overwhelming majority — M1a/M1b's own seam tests predate T1 and never
/// exercised a vouch or a revocation) gets exactly the node-file-only check it always did. A test
/// that does care passes a chain reader (real <see cref="GitLedgerChainReader"/>'s own tests are a
/// real repository; here it is whatever fake a test builds) and gets the identical chain-level gate
/// <see cref="GitLedgerMessageTransport"/> now enforces unconditionally in production.
/// </para>
/// </summary>
public sealed class InMemoryMessageTransport(ILedger ledger, ILedgerChainReader? chainReader = null) : IMessageTransport
{
    private readonly Dictionary<(string RepositoryPath, Guid NodeId), SortedList<long, string>> outboxes = [];

    /// <summary>Stands in for a real commit sha: bumped every time <see cref="SendAsync"/>,
    /// <see cref="FlushAsync"/>, or <see cref="SquashAsync"/> changes a given outbox, so
    /// <see cref="ProbeAsync"/> can tell "moved since last time" the same way a real ls-remote
    /// tip comparison does, without ever touching git.</summary>
    private readonly Dictionary<(string RepositoryPath, Guid NodeId), int> versions = [];

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
        BumpVersion(key);
        return Task.CompletedTask;
    }

    public Task FlushAsync(
        string repositoryPath,
        Guid fromNodeId,
        IReadOnlyList<TransportEnvelope> envelopes,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signingKey);

        (string repositoryPath, Guid fromNodeId) key = (repositoryPath, fromNodeId);
        if (!outboxes.TryGetValue(key, out SortedList<long, string>? existing))
        {
            existing = [];
            outboxes[key] = existing;
        }

        // Upsert, never a duplicate-seq refusal the way SendAsync gives: a retried flush after a
        // push that actually landed but crashed before this node recorded it is expected to see
        // its own already-there seqs again, with byte-for-byte identical content since a queued
        // envelope's wire bytes never change between attempts.
        foreach (TransportEnvelope envelope in envelopes)
        {
            existing[envelope.Seq] = envelope.Content;
        }

        BumpVersion(key);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MessageOutboxTip>> ProbeAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        IReadOnlyList<MessageOutboxTip> tips = [.. versions
            .Where(pair => pair.Key.RepositoryPath == repositoryPath)
            .Select(pair => new MessageOutboxTip(pair.Key.NodeId, pair.Value.ToString(CultureInfo.InvariantCulture)))];
        return Task.FromResult(tips);
    }

    public Task SquashAsync(
        string repositoryPath,
        Guid fromNodeId,
        IReadOnlyList<TransportEnvelope> survivors,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signingKey);

        (string repositoryPath, Guid fromNodeId) key = (repositoryPath, fromNodeId);
        SortedList<long, string> replacement = [];
        foreach (TransportEnvelope envelope in survivors)
        {
            replacement[envelope.Seq] = envelope.Content;
        }

        outboxes[key] = replacement;
        BumpVersion(key);
        return Task.CompletedTask;
    }

    private void BumpVersion((string RepositoryPath, Guid NodeId) key) =>
        versions[key] = versions.GetValueOrDefault(key) + 1;

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

        if (chainReader is not null)
        {
            string? publicKeyLine = GitLedgerChainReader.ExtractQuotedYamlValue(nodeFile.Content ?? string.Empty, "public_key");
            string? fingerprint = null;
            try
            {
                fingerprint = publicKeyLine is null ? null : NodeKeyStore.Fingerprint(publicKeyLine);
            }
            catch (DomainValidationException)
            {
                // Malformed key line — falls through to SenderNotVouched below, same as "no key at all".
            }

            TrustChain trustChain = await chainReader.ComputeAsync(repositoryPath, cancellationToken);
            if (fingerprint is null || !trustChain.IsAllowedSigner(fingerprint))
            {
                return TransportReadResult.SenderNotVouched;
            }
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
