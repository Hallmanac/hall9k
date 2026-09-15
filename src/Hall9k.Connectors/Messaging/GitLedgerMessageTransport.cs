using System.Globalization;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;

namespace Hall9k.Connectors.Messaging;

/// <summary>
/// The real <see cref="IMessageTransport"/>: a write goes through A1 (<see cref="ILedger.WriteAsync"/>),
/// signed, one writer per node's own outbox ref. A read walks the outbox ref directly by git
/// plumbing — listing <c>messages/*.json</c> at the ref's tip and, for each candidate envelope,
/// checking the commit that actually introduced its path was signed by the key in that sender's own
/// node file (idea 202383dc's sender-verification rule), never trusting the tip commit's own
/// signature for the whole tree beneath it — because <see cref="ILedger"/>'s own contract is a
/// single-path read, never an enumeration or a signature check, and this is the one place in the
/// whole platform that reads a message ref at all; nothing else ever touches
/// <c>refs/hall9k/messages/*</c>.
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
            throw new MessageSeqAlreadyUsedException(
                fromNodeId, seq,
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
        if (!await FetchRefAsync(repositoryPath, refName, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Could not fetch {refName} from origin for sender {senderNodeId} — refusing to read "
                + "whatever local copy of that outbox happens to remain, since it could be stale.");
        }

        string? tip = (await RunGitCaptureAsync(
            repositoryPath, ["rev-parse", "--verify", "--quiet", $"{refName}^{{commit}}"], cancellationToken))?.Trim();
        if (tip.IsBlank())
        {
            return TransportReadResult.Ok([], sinceSeq);
        }

        string? treeListing = await RunGitCaptureAsync(
            repositoryPath, ["ls-tree", "-r", "--name-only", tip, "--", "messages/"], cancellationToken);
        List<long> candidateSeqs = [.. (treeListing ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseSeqFromPath)
            .Where(seq => seq is not null && seq > sinceSeq)
            .Select(seq => seq.GetValueOrDefault())
            .OrderBy(seq => seq)];

        if (candidateSeqs.Count == 0)
        {
            return TransportReadResult.Ok([], sinceSeq);
        }

        // Every candidate path above the cursor is verified against the commit that actually
        // introduced it — never just the ref's own tip. M1a never batches (one commit per
        // envelope), but the tip's tree is still whatever the tip commit's own ancestry put there,
        // and anyone with push access to origin can insert an earlier commit onto this ref that the
        // sender's own next legitimate, correctly signed send then carries forward as a parent.
        // Trusting the tip's signature alone would trust that inserted commit's content too.
        Dictionary<string, bool> verifiedCommits = [];
        List<TransportEnvelope> envelopes = [];
        List<long> rejectedSeqs = [];
        long highestSeqInspected = sinceSeq;
        long? stalledAtSeq = null;
        foreach (long seq in candidateSeqs)
        {
            // MessageOutbox allocates the next seq as this node's own highest-plus-one, but a
            // failed push leaves that seq's own slot empty on the ref until something explicitly
            // resends it — so a numeric gap here is not proof of forgery or corruption the way it
            // would be if seq allocation were unconditionally contiguous; it can equally be this
            // same sender's own unresolved failed send. Either way, nothing beyond the gap has
            // actually been inspected, and trusting it as "inspected" would let it drag the cursor
            // past every real envelope still sitting beyond the gap. Stop instead: the gap is
            // re-examined next sweep rather than silently trusted, and stalledAtSeq tells the caller
            // there is unreached content here rather than genuinely nothing new.
            if (seq != highestSeqInspected + 1)
            {
                stalledAtSeq = seq;
                break;
            }

            string path = PathFor(seq);
            string? introducingCommit = await RunGitCaptureAsync(
                repositoryPath, ["log", "--format=%H", "-n", "1", tip, "--", path], cancellationToken);
            introducingCommit = introducingCommit?.Trim();
            if (introducingCommit.IsBlank())
            {
                // git itself failed to answer this — never a verdict on the envelope. Stop rather
                // than treat a tool failure as indistinguishable from a forged or corrupt envelope;
                // the cursor must never advance past content nobody has actually inspected yet.
                stalledAtSeq = seq;
                break;
            }

            if (!verifiedCommits.TryGetValue(introducingCommit, out bool isVerified))
            {
                isVerified = await IsSignedByRegisteredKeyAsync(repositoryPath, introducingCommit, publicKeyLine, cancellationToken);
                verifiedCommits[introducingCommit] = isVerified;
            }

            if (!isVerified)
            {
                rejectedSeqs.Add(seq);
                highestSeqInspected = seq;
                continue;
            }

            string? content = await RunGitCaptureAsync(repositoryPath, ["show", $"{introducingCommit}:{path}"], cancellationToken);
            if (content is null)
            {
                // Same reasoning as the missing-introducing-commit case above: git's own tree
                // listing already proved this blob exists, so a failure to read it back is a tool
                // failure, not a rejection — stop rather than skip past it.
                stalledAtSeq = seq;
                break;
            }

            envelopes.Add(new TransportEnvelope(seq, content));
            highestSeqInspected = seq;
        }

        return TransportReadResult.Ok(envelopes, highestSeqInspected, rejectedSeqs, stalledAtSeq);
    }

    private static string OutboxRef(Guid nodeId) => $"refs/hall9k/messages/{nodeId}";

    private const string MessagesDirectory = "messages/";
    private const string EnvelopeExtension = ".json";

    private static string PathFor(long seq) => $"{MessagesDirectory}{seq.ToString(CultureInfo.InvariantCulture)}{EnvelopeExtension}";

    /// <summary>Accepts only an exact, canonical <c>messages/&lt;seq&gt;.json</c> path — no nested
    /// directory, no other extension, no leading zero or sign — so that two different names (a
    /// stray <c>messages/5.txt</c>, a nested <c>messages/x/5.json</c>, a padded
    /// <c>messages/05.json</c>) can never both resolve to the same seq. <see cref="PathFor"/>'s own
    /// canonical form is the only name this ever matches, which is also what makes the round trip
    /// exact: a matched seq always formats back to the exact digits it was parsed from.</summary>
    internal static long? ParseSeqFromPath(string path)
    {
        if (!path.StartsWith(MessagesDirectory, StringComparison.Ordinal) ||
            !path.EndsWith(EnvelopeExtension, StringComparison.Ordinal))
        {
            return null;
        }

        string digits = path[MessagesDirectory.Length..^EnvelopeExtension.Length];
        if (digits.Contains('/') ||
            !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long seq))
        {
            return null;
        }

        return seq.ToString(CultureInfo.InvariantCulture) == digits ? seq : null;
    }

    /// <summary>Fetches the sender's own outbox ref fresh from origin. A missing remote ref (the
    /// sender has never sent anything yet) is not a failure — git's own exit code for it is
    /// indistinguishable from a genuine one, so the distinction is read from git's own message
    /// rather than the exit code alone. Any other non-zero exit — a network drop, a credential
    /// failure — is a real failure the caller must never treat as "nothing new": the local ref, if
    /// one exists from an earlier successful fetch, would otherwise be read as if it were current.
    /// </summary>
    private async Task<bool> FetchRefAsync(string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", ["fetch", "origin", $"+{refName}:{refName}"], repositoryPath, cancellationToken);
        return result.ExitCode == 0
            || result.StandardError.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies one specific commit — the commit that introduced the envelope path being
    /// read, never assumed from the ref's own tip alone — was actually signed by the key the
    /// sender's own node file names.</summary>
    private async Task<bool> IsSignedByRegisteredKeyAsync(
        string repositoryPath, string commitSha, string publicKeyLine, CancellationToken cancellationToken)
    {
        string? committerEmail = (await RunGitCaptureAsync(
            repositoryPath, ["log", "-1", "--format=%ce", commitSha], cancellationToken))?.Trim();
        if (committerEmail.IsBlank())
        {
            return false;
        }

        // git verify-commit picks the signature format from the gpgsig header itself
        // (get_format_by_sig), never from "-c gpg.format=ssh" — that setting only chooses what a
        // *new* signature is created as. A commit signed with an OpenPGP or X.509 key therefore
        // still verifies through gpg/gpgsm against whatever the local machine's own default
        // keyring trusts, never checked against the one SSH key this sender's own node file
        // names — so anyone whose key is in that keyring could forge a commit verify-commit
        // happily accepts. Confirming the header itself is an SSH signature is the only way this
        // check means what it claims to before ever trusting verify-commit's own exit code.
        string? rawCommit = await RunGitCaptureAsync(repositoryPath, ["cat-file", "commit", commitSha], cancellationToken);
        if (rawCommit is null || !HasSshSignatureHeader(rawCommit))
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
                ["-c", "gpg.format=ssh", "-c", $"gpg.ssh.allowedSignersFile={allowedSignersFile}", "verify-commit", commitSha],
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

    /// <summary>Reads a raw commit object exactly the way <c>git cat-file commit &lt;sha&gt;</c>
    /// prints it: the <c>gpgsig</c> header's own first line always carries the signature block's
    /// own opening marker immediately after the header name, with no other line able to produce
    /// that same prefix — a pure string check, so nothing about testing it needs a real
    /// repository, unlike <see cref="IsSignedByRegisteredKeyAsync"/> itself.</summary>
    internal static bool HasSshSignatureHeader(string rawCommitObject)
    {
        const string header = "gpgsig ";
        const string sshMarker = "-----BEGIN SSH SIGNATURE-----";
        foreach (string rawLine in rawCommitObject.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith(header, StringComparison.Ordinal))
            {
                return line[header.Length..].StartsWith(sshMarker, StringComparison.Ordinal);
            }
        }

        return false;
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
