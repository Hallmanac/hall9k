using System.Diagnostics;
using System.Globalization;
using System.Text;
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
    /// <summary>Same bound as <c>GitLedger.MaxPushAttempts</c>, for the identical reason: two
    /// writers racing converge within a round or two, and this ref has exactly one writer besides
    /// (this node), so a push still losing after this many is fighting something else entirely.</summary>
    private const int MaxPushAttempts = 5;

    /// <summary>The only principal <see cref="IsSignedByRegisteredKeyAsync"/> ever writes into a
    /// temporary <c>allowed_signers</c> file — a fixed literal, never the commit's own committer
    /// email. That field previously carried the (attacker-controlled) committer email of the very
    /// commit under verification: a crafted email containing a space let a malicious pusher smuggle
    /// their own key into the allowed-signers line's key-type/key-data fields, displacing the
    /// sender's real registered key into a trailing, ignored comment, so <c>git verify-commit</c>
    /// verified the forged commit against the attacker's own key instead (independent pre-PR review,
    /// cycle 1, adversarial lens). <c>git verify-commit</c> never requires this principal to match
    /// the commit's own committer identity — it accepts any line in the file whose key verifies the
    /// signature — so a fixed principal costs nothing: the one candidate key already came from the
    /// sender's own node file, never from anything this commit's author controls.</summary>
    private const string AllowedSignersPrincipal = "hall9k-sender";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

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

    /// <summary>
    /// Builds one commit carrying every envelope in <paramref name="envelopes"/> on top of the
    /// outbox ref's current tip (never an orphan the way <see cref="SquashAsync"/> is — a flush
    /// only ever adds), signs it, and pushes it by commit id with the same fetch-rebuild-retry loop
    /// <c>GitLedger.WriteAsync</c> uses for a single path. A rejected push after
    /// <see cref="MaxPushAttempts"/> throws <see cref="LedgerPushRejectedException"/>, matching
    /// <see cref="SendAsync"/>'s own failure shape so <c>MessageOutbox.FlushAsync</c> catches both
    /// identically.
    /// </summary>
    public async Task FlushAsync(
        string repositoryPath,
        Guid fromNodeId,
        IReadOnlyList<TransportEnvelope> envelopes,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        string refName = OutboxRef(fromNodeId);
        if (!await FetchRefAsync(repositoryPath, refName, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Could not fetch {refName} from origin — refusing to flush against a local tip "
                + "that might be stale; the queued envelopes stay pending for the next sweep.");
        }

        string lastError = string.Empty;
        for (int attempt = 1; attempt <= MaxPushAttempts; attempt++)
        {
            string? tip = await ResolveTipAsync(repositoryPath, refName, cancellationToken);
            string commitId = await BuildEnvelopeCommitAsync(
                repositoryPath, envelopes, seedFromTip: tip, parentTip: tip, "Flush messages", committer, signingKey,
                cancellationToken);

            (int pushExit, _, string pushError) = await RunGitRawAsync(
                repositoryPath, ["push", "origin", $"{commitId}:{refName}"], environment: null, standardInput: null,
                cancellationToken);
            if (pushExit == 0)
            {
                await SetLocalRefAsync(repositoryPath, refName, commitId, cancellationToken);
                return;
            }

            lastError = pushError;
            if (!await FetchRefAsync(repositoryPath, refName, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Could not fetch {refName} from origin after a rejected push — refusing to "
                    + "retry against a local tip that might be stale; the queued envelopes stay "
                    + "pending for the next sweep.");
            }
        }

        throw new LedgerPushRejectedException(refName, MaxPushAttempts, lastError);
    }

    public Task<IReadOnlyList<MessageOutboxTip>> ProbeAsync(string repositoryPath, CancellationToken cancellationToken) =>
        ProbeCoreAsync(repositoryPath, cancellationToken);

    private async Task<IReadOnlyList<MessageOutboxTip>> ProbeCoreAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "git", ["ls-remote", "origin", "refs/hall9k/messages/*"], repositoryPath, cancellationToken);
        if (result.ExitCode != 0)
        {
            // Thrown, never swallowed into an empty tip list: a genuine network, credential, or
            // repository failure must never look identical to "no message refs yet", or the sweep
            // could silently miss messages while reading the idle cadence as if nothing were
            // pending. MessageSweepEngine.ProbeAndReadAsync already catches and logs exactly this
            // ("Message probe failed... will retry next sweep"), the same retry-next-tick shape a
            // probe failure always had — this only routes the failure through that existing path
            // instead of a second, silent one.
            throw new InvalidOperationException(
                $"git ls-remote against {repositoryPath} for the message probe failed "
                + $"(exit {result.ExitCode}): {result.StandardError.Trim()}");
        }

        List<MessageOutboxTip> tips = [];
        foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            string sha = parts[0].Trim();
            string refName = parts[1].Trim();
            const string prefix = "refs/hall9k/messages/";
            if (!refName.StartsWith(prefix, StringComparison.Ordinal)
                || !Guid.TryParse(refName[prefix.Length..], out Guid senderNodeId))
            {
                continue;
            }

            tips.Add(new MessageOutboxTip(senderNodeId, sha));
        }

        return tips;
    }

    /// <summary>
    /// Rewrites this node's own outbox ref to an ORPHAN commit — no parent at all, unlike
    /// <see cref="FlushAsync"/> — holding exactly <paramref name="survivors"/>: the whole point of
    /// a squash is that everything dropped becomes genuinely unreachable, not merely absent from
    /// the tip's own tree while still hanging off a parent chain a fast-forward push would keep
    /// alive. Force-pushed (<c>+commit:ref</c>) since an orphan commit is never a fast-forward of
    /// whatever the ref held before.
    /// </summary>
    public async Task SquashAsync(
        string repositoryPath,
        Guid fromNodeId,
        IReadOnlyList<TransportEnvelope> survivors,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        string refName = OutboxRef(fromNodeId);
        if (!await FetchRefAsync(repositoryPath, refName, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Could not fetch {refName} from origin — refusing to squash against a local tip "
                + "that might be stale, which could overwrite newer remote envelopes.");
        }

        string commitId = await BuildEnvelopeCommitAsync(
            repositoryPath, survivors, seedFromTip: null, parentTip: null, "Squash outbox", committer, signingKey,
            cancellationToken);

        (int pushExit, _, string pushError) = await RunGitRawAsync(
            repositoryPath, ["push", "origin", $"+{commitId}:{refName}"], environment: null, standardInput: null,
            cancellationToken);
        if (pushExit != 0)
        {
            throw new LedgerPushRejectedException(refName, 1, pushError);
        }

        await SetLocalRefAsync(repositoryPath, refName, commitId, cancellationToken);
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
            await File.WriteAllTextAsync(allowedSignersFile, $"{AllowedSignersPrincipal} {publicKeyLine}\n", cancellationToken);
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

    private async Task<string?> ResolveTipAsync(string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        string? tip = (await RunGitCaptureAsync(
            repositoryPath, ["rev-parse", "--verify", "--quiet", $"{refName}^{{commit}}"], cancellationToken))?.Trim();
        return tip.IsBlank() ? null : tip;
    }

    private static async Task SetLocalRefAsync(
        string repositoryPath, string refName, string newCommit, CancellationToken cancellationToken)
    {
        (int exitCode, _, string error) = await RunGitRawAsync(
            repositoryPath, ["update-ref", refName, newCommit], environment: null, standardInput: null, cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git update-ref {refName} failed in {repositoryPath}: {error.Trim()}");
        }
    }

    /// <summary>
    /// Builds one signed commit holding every envelope in <paramref name="envelopes"/>, through a
    /// private <c>GIT_INDEX_FILE</c> this call owns start to finish — the identical isolation
    /// <c>GitLedger.BuildTreeAsync</c> uses for a single path, here looped over N. Seeding from
    /// <paramref name="seedFromTip"/> (<c>git read-tree</c>) is what makes <see cref="FlushAsync"/>
    /// an ordinary append: every path outside <c>messages/</c> — there is none today, but nothing
    /// here assumes that — and every envelope not in this batch survives into the new tree
    /// unchanged. <see cref="SquashAsync"/> passes <see langword="null"/> for both
    /// <paramref name="seedFromTip"/> and <paramref name="parentTip"/>, building a tree from
    /// nothing but <paramref name="envelopes"/> and a commit with no parent at all.
    /// </summary>
    private static async Task<string> BuildEnvelopeCommitAsync(
        string repositoryPath,
        IReadOnlyList<TransportEnvelope> envelopes,
        string? seedFromTip,
        string? parentTip,
        string commitMessage,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        string tempIndex = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"h9k-message-index-{Guid.NewGuid():N}");
        Dictionary<string, string> indexEnvironment = new() { ["GIT_INDEX_FILE"] = tempIndex };
        try
        {
            (int readExit, _, string readError) = seedFromTip is not null
                ? await RunGitRawAsync(
                    repositoryPath, ["read-tree", seedFromTip], indexEnvironment, standardInput: null, cancellationToken)
                : await RunGitRawAsync(
                    repositoryPath, ["read-tree", "--empty"], indexEnvironment, standardInput: null, cancellationToken);
            if (readExit != 0)
            {
                throw new InvalidOperationException(
                    $"git read-tree {seedFromTip ?? "--empty"} failed in {repositoryPath}: {readError.Trim()}");
            }

            foreach (TransportEnvelope envelope in envelopes)
            {
                (int hashExit, string blobOutput, string hashError) = await RunGitRawAsync(
                    repositoryPath, ["hash-object", "-w", "--stdin"], environment: null, envelope.Content, cancellationToken);
                if (hashExit != 0)
                {
                    throw new InvalidOperationException($"git hash-object failed in {repositoryPath}: {hashError.Trim()}");
                }

                string blobId = blobOutput.Trim();
                string path = PathFor(envelope.Seq);
                (int addExit, _, string addError) = await RunGitRawAsync(
                    repositoryPath, ["update-index", "--add", "--cacheinfo", $"100644,{blobId},{path}"],
                    indexEnvironment, standardInput: null, cancellationToken);
                if (addExit != 0)
                {
                    throw new InvalidOperationException($"git update-index failed in {repositoryPath}: {addError.Trim()}");
                }
            }

            (int writeExit, string treeOutput, string writeError) = await RunGitRawAsync(
                repositoryPath, ["write-tree"], indexEnvironment, standardInput: null, cancellationToken);
            if (writeExit != 0)
            {
                throw new InvalidOperationException($"git write-tree failed in {repositoryPath}: {writeError.Trim()}");
            }

            string treeId = treeOutput.Trim();

            List<string> commitArguments =
            [
                "-c", $"user.name={committer.Name}",
                "-c", $"user.email={committer.Email}",
                "-c", "gpg.format=ssh",
                "-c", $"user.signingkey={signingKey.PrivateKeyPath}",
                "commit-tree", treeId,
            ];
            if (parentTip is not null)
            {
                commitArguments.Add("-p");
                commitArguments.Add(parentTip);
            }

            commitArguments.Add("-S");
            commitArguments.Add("-m");
            commitArguments.Add(commitMessage);

            (int commitExit, string commitOutput, string commitError) = await RunGitRawAsync(
                repositoryPath, commitArguments, environment: null, standardInput: null, cancellationToken);
            if (commitExit != 0)
            {
                throw new InvalidOperationException($"git commit-tree failed in {repositoryPath}: {commitError.Trim()}");
            }

            return commitOutput.Trim();
        }
        finally
        {
            try
            {
                File.Delete(tempIndex);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a temp file; nothing downstream reads it again.
            }
        }
    }

    /// <summary>
    /// The one place this class spawns git with an environment override or stdin — everything
    /// else goes through the injected <see cref="runner"/> field, which supports neither and is
    /// what every read-side call above (and every test double) actually exercises. Mirrors
    /// <c>GitLedger</c>'s own private process runner rather than sharing it: that type is a
    /// different class in a different concern (the ledger's single-path write), and duplicating
    /// this narrow, already-proven shape is cheaper than widening the shared
    /// <see cref="ProcessRunner"/> delegate — used by connectors that need neither — just for this
    /// one caller.
    /// </summary>
    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunGitRawAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            StandardInputEncoding = standardInput is not null ? Utf8NoBom : null,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(repositoryPath);
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        NonInteractiveGit.Apply(process.StartInfo);
        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        process.Start();
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        try
        {
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return (process.ExitCode, await standardOutput, await standardError);
        }
        catch
        {
            // Cancelling the wait above only stops Hall9k waiting: git is a real operating-system
            // process and keeps running past it, so a cancelled push or tree build could otherwise
            // still land on origin or hold the temp index file open after this method has already
            // returned to a caller that believes it was cancelled.
            await TerminateAsync(process);
            throw;
        }
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            using CancellationTokenSource grace = new(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(grace.Token);
        }
        catch (Exception)
        {
            // Nothing here is recoverable and nothing here is the caller's problem.
        }
    }
}
