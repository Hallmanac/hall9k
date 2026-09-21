using Hall9k.Connectors.Ledger;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// An <see cref="ILedgerCommitReader"/> that hands back canned <see cref="LedgerSignedCommit"/>
/// values a test builds by hand, rather than reading real git plumbing — the seam
/// <c>ProjectJoinCommand</c>'s own cross-project root-carry path (task f53fecfd) is driven through
/// in <c>ProjectJoinCommandTests</c>, the same way <see cref="FakeLedgerChainReader"/> stands in for
/// the chain read right above it (Brian's 2026-09-13 testing rule: no test outside
/// <c>GitLedgerTests</c> and the message-transport chain reader's own tests touches a real
/// repository).
/// </summary>
internal sealed class FakeLedgerCommitReader : ILedgerCommitReader
{
    private readonly IReadOnlyDictionary<string, LedgerSignedCommit> commitsByPath;
    private readonly Func<string, string, bool> isSignedBy;

    /// <param name="commitsByPath">Keyed by the ledger path (never repository or ref), since every
    /// scenario this fake drives reads at most one repository's own ref at a time.</param>
    /// <param name="signed">What every <see cref="IsSignedByAsync"/> call answers — a single flag is
    /// enough for a scenario where every commit this fake hands back is equally (un)signed.</param>
    public FakeLedgerCommitReader(IReadOnlyDictionary<string, LedgerSignedCommit> commitsByPath, bool signed = true)
        : this(commitsByPath, (_, _) => signed)
    {
    }

    /// <param name="commitsByPath">Keyed by the ledger path (never repository or ref), since every
    /// scenario this fake drives reads at most one repository's own ref at a time.</param>
    /// <param name="isSignedBy">Answers <see cref="IsSignedByAsync"/> per (rawCommitBytes,
    /// publicKeyLine) pair — the seam a scenario that needs to distinguish which of several
    /// candidate commits actually verifies (the source's own root commit vs. its vouch commit) drives
    /// directly, rather than the single blanket flag the other constructor shares across every call.</param>
    public FakeLedgerCommitReader(
        IReadOnlyDictionary<string, LedgerSignedCommit> commitsByPath, Func<string, string, bool> isSignedBy)
    {
        this.commitsByPath = commitsByPath;
        this.isSignedBy = isSignedBy;
    }

    public Task<LedgerSignedCommit?> ReadSignedCommitAsync(
        string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
        Task.FromResult(commitsByPath.TryGetValue(path, out LedgerSignedCommit? commit) ? commit : null);

    public Task<bool> IsSignedByAsync(
        string repositoryPath, string rawCommitBytes, string publicKeyLine, CancellationToken cancellationToken) =>
        Task.FromResult(isSignedBy(rawCommitBytes, publicKeyLine));
}
