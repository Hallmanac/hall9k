namespace Hall9k.Connectors.Ledger;

/// <summary>
/// One path's current content, paired with the exact raw bytes of the commit that produced it — what
/// a carried-vouch bundle (<c>h9k project join</c>'s cross-project root-carry path, task f53fecfd)
/// embeds so a target ledger can verify the source's own SSH signature entirely offline, without a
/// remote pointing at the source project's own repository to fetch the commit from. <see cref="RawCommitBytes"/>
/// is exactly what <c>git cat-file commit &lt;sha&gt;</c> prints — the identical text
/// <c>GitLedgerChainReader.IsSignedByAsync</c> already reads and verifies against, so a reader on a
/// different repository can inject it as a loose object (<c>git hash-object -w -t commit</c>) and run
/// the same signature check locally: a git object is content-addressed, so the injected object's own
/// computed sha is identical to <see cref="CommitSha"/> regardless of which repository holds it.
/// </summary>
public sealed record LedgerSignedCommit(string Content, string CommitSha, string RawCommitBytes);

/// <summary>
/// Reads a path's current content together with the raw bytes of the exact commit that produced it.
/// The real implementation, <see cref="GitLedgerCommitReader"/>, is git plumbing over an
/// already-local, already-cloned repository — the carrying node reads its own local copy of the
/// source project's own ledger, the same repository <c>h9k project join</c> already writes through
/// for that project, never a live fetch of a third party's remote.
/// </summary>
public interface ILedgerCommitReader
{
    /// <summary>Null when the ref does not exist yet, or exists but the path is not in its tree — the identical absence <see cref="ILedger.ReadAsync"/> reports.</summary>
    Task<LedgerSignedCommit?> ReadSignedCommitAsync(
        string repositoryPath, string refName, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="rawCommitBytes"/> — the exact text <see cref="LedgerSignedCommit.RawCommitBytes"/>
    /// carries — is SSH-signed by <paramref name="publicKeyLine"/>, checked by injecting it as a
    /// loose object into <paramref name="repositoryPath"/> and running <c>git verify-commit</c>
    /// against the sha that injection computes (a git object is content-addressed, so the computed
    /// sha is identical regardless of which repository holds it). Lets a caller confirm a candidate
    /// bundle will actually verify before ever writing it — <c>h9k project join</c>'s own carry path
    /// uses this so it never commits a bundle doomed to fail the identical check
    /// <c>GitLedgerChainReader</c> runs on every later read.
    /// </summary>
    Task<bool> IsSignedByAsync(
        string repositoryPath, string rawCommitBytes, string publicKeyLine, CancellationToken cancellationToken);
}
