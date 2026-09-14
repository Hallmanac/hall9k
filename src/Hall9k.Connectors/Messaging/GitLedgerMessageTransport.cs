using System.Globalization;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;

namespace Hall9k.Connectors.Messaging;

/// <summary>
/// The real <see cref="IMessageTransport"/>: a write goes through A1 (<see cref="ILedger.WriteAsync"/>),
/// signed, one writer per node's own outbox ref. A read walks the outbox ref directly by git
/// plumbing — listing <c>messages/*.json</c> at the ref's tip and checking the tip commit was
/// actually signed by the key in that sender's own node file (idea 202383dc's sender-verification
/// rule) — because <see cref="ILedger"/>'s own contract is a single-path read, never an enumeration
/// or a signature check, and this is the one place in the whole platform that reads a message ref
/// at all; nothing else ever touches <c>refs/hall9k/messages/*</c>.
/// <para>
/// Never covered by this task's own tests: Brian's 2026-09-13 testing rule reserves a real
/// repository for <c>GitLedgerTests</c> and the chain reader's own tests, and every message-seam
/// test here drives <see cref="InMemoryMessageTransport"/> instead. Real signature verification
/// against A1's own <c>GitLedgerTests</c> is exactly what that rule is trusting to have already
/// proven the signing half works; this class only has to ask git the same question A1's tests do.
/// </para>
/// </summary>
public sealed class GitLedgerMessageTransport(ILedger ledger, ProcessRunner? runner = null) : IMessageTransport
{
    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;

    public async Task SendAsync(
        string repositoryPath,
        Guid fromNodeId,
        long seq,
        string content,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        string refName = OutboxRef(fromNodeId);
        string path = PathFor(seq);

        LedgerWriteOutcome outcome = await ledger.WriteAsync(
            new LedgerWriteRequest(
                repositoryPath, refName, path, content, ExpectedBlobId: null, $"Send message {seq}", committer, signingKey),
            cancellationToken);

        if (outcome.Verdict == LedgerWriteVerdict.Conflict)
        {
            throw new InvalidOperationException(
                $"{path} already exists in {refName} — seq {seq} was already used, which should never "
                + "happen since this node's own store allocates it once and this ref has exactly one writer.");
        }
    }

    public async Task<TransportReadResult> ReadSinceAsync(
        string repositoryPath, Guid senderNodeId, long sinceSeq, CancellationToken cancellationToken)
    {
        string nodeFileRefName = $"refs/hall9k/ledger/nodes/{senderNodeId}";
        string nodeFilePath = $"nodes/{senderNodeId}/node.yaml";
        LedgerFile nodeFile = await ledger.ReadAsync(repositoryPath, nodeFileRefName, nodeFilePath, cancellationToken);
        if (!nodeFile.Exists)
        {
            return TransportReadResult.SenderNotVouched;
        }

        string? publicKeyLine = ExtractQuotedYamlValue(nodeFile.Content!, "public_key");
        if (publicKeyLine is null)
        {
            return TransportReadResult.SenderNotVouched;
        }

        string refName = OutboxRef(senderNodeId);
        await FetchRefAsync(repositoryPath, refName, cancellationToken);

        string? tip = await RunGitCaptureAsync(
            repositoryPath, ["rev-parse", "--verify", "--quiet", $"{refName}^{{commit}}"], cancellationToken);
        if (tip is null)
        {
            return TransportReadResult.Ok([]);
        }

        string? treeListing = await RunGitCaptureAsync(
            repositoryPath, ["ls-tree", "-r", "--name-only", tip, "--", "messages/"], cancellationToken);
        List<long> candidateSeqs = [.. (treeListing ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseSeqFromPath)
            .Where(seq => seq is not null && seq > sinceSeq)
            .Select(seq => seq!.Value)
            .OrderBy(seq => seq)];

        if (candidateSeqs.Count == 0)
        {
            return TransportReadResult.Ok([]);
        }

        if (!await IsSignedByRegisteredKeyAsync(repositoryPath, tip, publicKeyLine, cancellationToken))
        {
            return TransportReadResult.SenderNotVouched;
        }

        List<TransportEnvelope> envelopes = [];
        foreach (long seq in candidateSeqs)
        {
            string? content = await RunGitCaptureAsync(repositoryPath, ["show", $"{tip}:{PathFor(seq)}"], cancellationToken);
            if (content is not null)
            {
                envelopes.Add(new TransportEnvelope(seq, content));
            }
        }

        return TransportReadResult.Ok(envelopes);
    }

    private static string OutboxRef(Guid nodeId) => $"refs/hall9k/messages/{nodeId}";

    private static string PathFor(long seq) => $"messages/{seq.ToString(CultureInfo.InvariantCulture)}.json";

    private static long? ParseSeqFromPath(string path)
    {
        string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        return long.TryParse(fileName, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq) ? seq : null;
    }

    private async Task FetchRefAsync(string repositoryPath, string refName, CancellationToken cancellationToken) =>
        await runner("git", ["fetch", "origin", $"+{refName}:{refName}"], repositoryPath, cancellationToken);

    /// <summary>
    /// Verifies the outbox ref's own tip commit — not every commit in its history, since M1a never
    /// batches (one commit per envelope) and a chain whose latest link is genuinely the registered
    /// key is enough to trust everything this sweep is about to read; a compromised earlier link
    /// would already have failed a previous sweep's own check.
    /// </summary>
    private async Task<bool> IsSignedByRegisteredKeyAsync(
        string repositoryPath, string tip, string publicKeyLine, CancellationToken cancellationToken)
    {
        string? committerEmail = await RunGitCaptureAsync(repositoryPath, ["log", "-1", "--format=%ce", tip], cancellationToken);
        if (committerEmail.IsBlank())
        {
            return false;
        }

        string allowedSignersFile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"h9k-message-allowed-signers-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(allowedSignersFile, $"{committerEmail} {publicKeyLine}\n", cancellationToken);
            ProcessResult result = await runner(
                "git",
                ["-c", "gpg.format=ssh", "-c", $"gpg.ssh.allowedSignersFile={allowedSignersFile}", "verify-commit", tip],
                repositoryPath,
                cancellationToken);
            return result.ExitCode == 0;
        }
        finally
        {
            try
            {
                File.Delete(allowedSignersFile);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp file; nothing downstream reads it again.
            }
        }
    }

    private async Task<string?> RunGitCaptureAsync(
        string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", arguments, repositoryPath, cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput : null;
    }

    /// <summary>Reverses <c>ProjectJoinCommand</c>'s own <c>QuoteYaml</c> — the same small, flat,
    /// every-value-double-quoted YAML shape node.yaml is written in (A2a). Internal, not private,
    /// so <c>GitLedgerMessageTransportTests</c> can exercise the round trip directly — it is a pure
    /// string function, so nothing about testing it needs a real repository.</summary>
    internal static string? ExtractQuotedYamlValue(string yaml, string key)
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
