using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;

namespace Hall9k.Tests.Connectors.Trust;

/// <summary>
/// An in-memory, hand-built commit graph standing in for a real git repository —
/// <see cref="ILedgerCommitAccess"/>'s own fake, so <see cref="GitLedgerChainReaderFastTests"/> can
/// exercise <see cref="GitLedgerChainReader"/>'s trust rules (vouch/revoke ordering, succession
/// rank, carried-record structural gating, membership authorization, the fixed-point loop) without
/// a real git process or a real SSH key anywhere. There is no "hub" or "clone" distinction here the
/// way <see cref="Hall9k.Tests.Connectors.Ledger.LedgerTestRepo"/> models for the real, git-backed
/// tests: every write lands in the one shared graph immediately visible to every read, since this
/// class never simulates network fetch delay or divergence — that is exactly the git plumbing the
/// small end-to-end set (<see cref="Hall9k.Tests.Connectors.Trust.GitLedgerChainReaderTests"/>)
/// still owns proving.
/// <para>
/// Every fake commit carries exactly one changed path — every real write this platform's own
/// commands ever produce touches exactly one ledger path per commit too, and the one scenario that
/// depends on a real multi-path merge commit
/// (<c>A_vouch_introduced_purely_via_a_merge_commits_second_parent_is_still_applied</c>) is kept as
/// a real end-to-end test for exactly that reason.
/// </para>
/// <para>
/// "Signed by" here is a direct equality check against whichever public key line
/// <see cref="Write"/>'s own caller named as the signer — no real SSH signature, no real
/// <c>git verify-commit</c>: proving that real signature verification actually rejects a forged
/// signer is the small end-to-end set's own job, not this fake's.
/// </para>
/// </summary>
internal sealed class FakeLedgerGraph : ILedgerCommitAccess
{
    private sealed record FakeCommit(
        string? Parent, string Path, string? Content, string SignerPublicKeyLine, string Message, DateTimeOffset CommitterTime);

    private readonly Dictionary<string, string> refTips = [];
    private readonly Dictionary<string, FakeCommit> commits = [];
    private int sequence;
    private DateTimeOffset clock = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Appends one commit to <paramref name="refName"/>'s own history, parented on
    /// whatever that ref's tip currently is (or root, for its first commit). Mirrors the real
    /// tests' own <c>WriteAsync</c>: always a fresh commit, read-then-write against whatever is
    /// currently there, never write-if-absent.</summary>
    public string Write(
        string refName, string path, string? content, string signerPublicKeyLine, string message = "test write",
        DateTimeOffset? committerTime = null)
    {
        string? parent = refTips.GetValueOrDefault(refName);
        string id = $"c{++sequence}";
        this.clock = committerTime ?? this.clock.AddSeconds(1);
        commits[id] = new FakeCommit(parent, path, content, signerPublicKeyLine, message, this.clock);
        refTips[refName] = id;
        return id;
    }

    public void DeleteRef(string refName) => refTips.Remove(refName);

    /// <summary>The fake counterpart of a carried bundle's own <c>root_commit_base64</c>/
    /// <c>vouch_commit_base64</c> field — an opaque token this graph's own
    /// <see cref="IsSignedByRawBytesAsync"/> can parse the signer back out of, and whose own text
    /// literally contains <paramref name="message"/> so the production vouch-marker binding check
    /// (a plain substring search over the raw bytes) exercises the identical logic a real commit's
    /// raw bytes would put it through.</summary>
    public static string EncodeRawCommit(string signerPublicKeyLine, string message) =>
        $"fake-commit\nsigner:{signerPublicKeyLine}\nmessage:{message}\n";

    public Task<IReadOnlyList<string>> DiscoverRefSuffixesAsync(string prefix, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(
            [.. refTips.Keys
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(name => name[prefix.Length..])]);

    public Task<LedgerAppendOnlyFetchResult> FetchRefAsync(string refName, CancellationToken cancellationToken)
    {
        string? tip = refTips.GetValueOrDefault(refName);
        return Task.FromResult(new LedgerAppendOnlyFetchResult(tip, false, tip, tip, null));
    }

    public async Task<IReadOnlyDictionary<string, LedgerAppendOnlyFetchResult>> FetchPrefixAsync(
        string prefix, IReadOnlyList<string> suffixes, CancellationToken cancellationToken)
    {
        Dictionary<string, LedgerAppendOnlyFetchResult> results = [];
        foreach (string suffix in suffixes)
        {
            results[suffix] = await FetchRefAsync($"{prefix}{suffix}", cancellationToken);
        }

        return results;
    }

    public Task<IReadOnlyList<string>> CommitsOldestFirstAsync(string tip, CancellationToken cancellationToken)
    {
        List<string> newestFirst = [];
        string? current = tip;
        while (current is not null)
        {
            newestFirst.Add(current);
            current = commits[current].Parent;
        }

        newestFirst.Reverse();
        return Task.FromResult<IReadOnlyList<string>>(newestFirst);
    }

    public Task<IReadOnlyList<string>> CommitsTouchingPathAsync(string tip, string path, CancellationToken cancellationToken)
    {
        List<string> touching = [];
        string? current = tip;
        while (current is not null)
        {
            FakeCommit commit = commits[current];
            if (commit.Path == path)
            {
                touching.Add(current);
            }

            current = commit.Parent;
        }

        return Task.FromResult<IReadOnlyList<string>>(touching);
    }

    public Task<IReadOnlyList<string>> ChangedPathsAsync(string commit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([commits[commit].Path]);

    public Task<string?> ReadAtCommitAsync(string commit, string path, CancellationToken cancellationToken)
    {
        string? current = commit;
        while (current is not null)
        {
            FakeCommit found = commits[current];
            if (found.Path == path)
            {
                return Task.FromResult(found.Content);
            }

            current = found.Parent;
        }

        return Task.FromResult<string?>(null);
    }

    public Task<DateTimeOffset> CommitTimeAsync(string commit, CancellationToken cancellationToken) =>
        Task.FromResult(commits[commit].CommitterTime);

    public Task<bool> IsSignedByAsync(string commit, string publicKeyLine, CancellationToken cancellationToken) =>
        Task.FromResult(commits[commit].SignerPublicKeyLine == publicKeyLine);

    public Task<bool> IsSignedByRawBytesAsync(string rawCommitBytes, string publicKeyLine, CancellationToken cancellationToken) =>
        Task.FromResult(ParseSigner(rawCommitBytes) == publicKeyLine);

    private static string? ParseSigner(string rawCommitBytes)
    {
        const string prefix = "signer:";
        foreach (string line in rawCommitBytes.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..];
            }
        }

        return null;
    }
}
