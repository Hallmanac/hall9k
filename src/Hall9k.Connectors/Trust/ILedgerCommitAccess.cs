using Hall9k.Connectors.Ledger;

namespace Hall9k.Connectors.Trust;

/// <summary>
/// The one seam between <see cref="GitLedgerChainReader"/>'s trust-rule algorithm (vouch/revoke
/// ordering, succession rank, carried-record structural gating, membership authorization, the
/// fixed-point loop) and however commits, refs, and signatures are actually read. Every method here
/// is a straight rename of a git-plumbing call <see cref="GitLedgerChainReader"/> used to make
/// directly against a real repository — <see cref="GitPlumbingLedgerCommitAccess"/> is that same
/// git plumbing, moved behind this interface with no behavior change, and is what every production
/// <see cref="GitLedgerChainReader"/> instance uses. The one other implementation
/// (<c>FakeLedgerGraph</c>, in <c>Hall9k.Tests</c>) answers from a hand-built in-memory commit
/// graph instead, so the trust rules above this seam can be exercised in milliseconds with no real
/// git process and no real SSH key ever touched — reserving real git and real signing keys for the
/// small end-to-end set that still proves this seam's own git plumbing is correct
/// (<c>GitLedgerChainReaderTests</c>: commit signature verification, carried bundles with embedded
/// commits, ref rewind refusal, and genesis selection by ref order).
/// </summary>
internal interface ILedgerCommitAccess
{
    /// <summary>Every suffix a ref under <paramref name="prefix"/> currently has — the owners or nodes
    /// prefix's own discovery walk (<see cref="GitLedgerChainReader.DiscoverOwnerRootsAsync"/>,
    /// <see cref="GitLedgerChainReader.DiscoverNodeIdsAsync"/>).</summary>
    Task<IReadOnlyList<string>> DiscoverRefSuffixesAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>One append-only ref's own fetch outcome — the tip to read or build a write on, and
    /// whether this read was refused (a rewind, a side merge, or a remote ref this node still holds a
    /// verified tip for that origin no longer lists).</summary>
    Task<LedgerAppendOnlyFetchResult> FetchRefAsync(string refName, CancellationToken cancellationToken);

    /// <summary>The identical fetch <see cref="FetchRefAsync"/> answers, for every one of
    /// <paramref name="suffixes"/> under <paramref name="prefix"/> in one round trip.</summary>
    Task<IReadOnlyDictionary<string, LedgerAppendOnlyFetchResult>> FetchPrefixAsync(
        string prefix, IReadOnlyList<string> suffixes, CancellationToken cancellationToken);

    /// <summary>Every commit reachable from <paramref name="tip"/>, oldest first, mainline only.</summary>
    Task<IReadOnlyList<string>> CommitsOldestFirstAsync(string tip, CancellationToken cancellationToken);

    /// <summary>Every commit reachable from <paramref name="tip"/> that touched <paramref name="path"/>,
    /// newest first — <c>[0]</c> is the commit that currently produces <paramref name="path"/>'s own
    /// content at <paramref name="tip"/>.</summary>
    Task<IReadOnlyList<string>> CommitsTouchingPathAsync(string tip, string path, CancellationToken cancellationToken);

    /// <summary>Every path <paramref name="commit"/> added, changed, or removed relative to its own
    /// mainline parent.</summary>
    Task<IReadOnlyList<string>> ChangedPathsAsync(string commit, CancellationToken cancellationToken);

    /// <summary><paramref name="path"/>'s own content at <paramref name="commit"/>, or null when the
    /// path does not exist there (never existed yet, or was deleted at or before this commit).</summary>
    Task<string?> ReadAtCommitAsync(string commit, string path, CancellationToken cancellationToken);

    /// <summary>The committer time of <paramref name="commit"/>, only ever used to order two
    /// declarations of the same field against each other, never as a trust anchor.</summary>
    Task<DateTimeOffset> CommitTimeAsync(string commit, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="commit"/> was actually signed by <paramref name="publicKeyLine"/>.</summary>
    Task<bool> IsSignedByAsync(string commit, string publicKeyLine, CancellationToken cancellationToken);

    /// <summary>The identical check <see cref="IsSignedByAsync"/> makes, over raw commit bytes an
    /// offline carried bundle embedded rather than a commit already present in this repository.</summary>
    Task<bool> IsSignedByRawBytesAsync(string rawCommitBytes, string publicKeyLine, CancellationToken cancellationToken);
}
