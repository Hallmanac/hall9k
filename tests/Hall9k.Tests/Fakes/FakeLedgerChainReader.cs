using Hall9k.Connectors.Trust;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// An <see cref="ILedgerChainReader"/> that hands back a canned <see cref="TrustChain"/> a test
/// builds by hand, rather than walking real git — the seam every CLI-command test above the
/// chain reader's own <c>GitLedgerChainReaderTests</c> drives instead (Brian's 2026-09-13 testing
/// rule).
/// </summary>
internal sealed class FakeLedgerChainReader : ILedgerChainReader
{
    private readonly Func<string, TrustChain> chainFor;

    /// <summary>The identical chain is returned for every repository path a test calls with —
    /// enough for every scenario that only ever exercises a single project's ledger.</summary>
    public FakeLedgerChainReader(TrustChain chain) => chainFor = _ => chain;

    /// <summary>A different chain per repository path — for a scenario spanning several projects,
    /// each with its own independently-computed chain, the same way a real
    /// <see cref="GitLedgerChainReader"/> would see two genuinely separate repositories.</summary>
    public FakeLedgerChainReader(IReadOnlyDictionary<string, TrustChain> chainsByRepositoryPath) =>
        chainFor = path => chainsByRepositoryPath.TryGetValue(path, out TrustChain? chain) ? chain : TrustChain.Empty;

    public Task<TrustChain> ComputeAsync(string repositoryPath, CancellationToken cancellationToken) =>
        Task.FromResult(chainFor(repositoryPath));
}
