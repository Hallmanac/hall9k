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
/// One commit reachable from a ledger ref's own tip that touched a given path, newest first — what
/// <see cref="ILedgerCommitReader.ReadCommitsTouchingPathAsync"/> reports for a caller
/// (<c>PromptAddendaSweepEngine.MaterializeAsync</c>) that needs to walk PAST the newest commit
/// rather than trust it unconditionally, the way <see cref="LedgerSignedCommit"/> alone lets a
/// caller do. <see cref="Content"/> is null exactly when this commit deleted the path — a real
/// state to materialize (the owner's own removal), never an absence of data about the commit
/// itself.</summary>
public sealed record LedgerPathCommit(string? Content, string CommitSha, string RawCommitBytes);

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
    /// <c>GitLedgerChainReader</c> runs on every later read. Throws <see cref="InvalidOperationException"/>
    /// when the injection step itself fails (disk, permissions, an unavailable git binary) — an
    /// infrastructure failure, never a genuine "not signed" verdict, so a caller must not treat the
    /// exception as false: <c>InviteSweepEngine</c> must not cache it against an unmoved ref tip, and
    /// <c>ProjectJoinCommand</c>'s own carry path must not let it crash the join, only skip the
    /// candidate under review (or fail the attempt when the candidate was named explicitly).
    /// </summary>
    Task<bool> IsSignedByAsync(
        string repositoryPath, string rawCommitBytes, string publicKeyLine, CancellationToken cancellationToken);

    /// <summary>
    /// Walks <paramref name="refName"/>'s own tip backward, first-parent order, newest first,
    /// over the ref's WHOLE real history — but reports only the commits that <see cref="GitLedger"/>
    /// itself stamped as an actual write or delete of <paramref name="path"/>
    /// (<c>LedgerCommitPathTrailer</c>), never merely a commit whose tree for that path happens to
    /// carry forward unchanged from its parent. That distinction is what lets a signed reissue of
    /// already-current content still count (its tree for <paramref name="path"/> is byte-identical
    /// to its parent's — TREESAME — but its own trailer still names <paramref name="path"/>,
    /// independent pre-PR review, cycle 3, both lenses, high) while an unrelated commit that wrote
    /// some OTHER path entirely never does, even though it too is TREESAME for <paramref name="path"/>
    /// (cycle 5, conformance and adversarial lenses, both high: no git tree diff can tell those two
    /// cases apart, only the trailer can). A commit with no trailer at all — real ledger history
    /// from before <see cref="GitLedger"/> started stamping one — falls back to the old tree-diff
    /// membership test instead of reading as untouched (cycle 6, conformance lens, high: the
    /// trailer alone cannot vouch for history written before it existed). An empty list when the
    /// ref does not exist yet.
    /// <para>
    /// Stops walking — and returns everything read up to and including that one commit, never
    /// anything older — the moment <paramref name="isAuthorizedAsync"/> accepts a commit's own raw
    /// bytes: <c>PromptAddendaSweepEngine.MaterializeAsync</c>'s own owner test (idea 6be68ee2,
    /// trust-ledger finding 6) only ever wants the FIRST (newest) commit an Owner-role member's
    /// chain authorizes, so a repository collaborator who pushes any number of unsigned commits
    /// ahead of the last authorized one costs this walk only as many git processes as stand
    /// between the tip and that authorized commit, never the ref's entire history (independent
    /// pre-PR review, cycle 3, adversarial lens, medium). A caller with no meaningful way to
    /// answer early — nothing in the returned list should ever short-circuit the walk — passes a
    /// predicate that never returns true, and reads every path-matching commit in the ref's own
    /// entire history back.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<LedgerPathCommit>> ReadCommitsTouchingPathAsync(
        string repositoryPath, string refName, string path,
        Func<string, CancellationToken, Task<bool>> isAuthorizedAsync, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="refName"/>'s own literal tip commit — regardless of which path it
    /// actually wrote — is accepted by <paramref name="isAuthorizedAsync"/>. Unlike
    /// <see cref="ReadCommitsTouchingPathAsync"/>, this never filters by path at all:
    /// <c>PromptAddendaSweepEngine</c>'s own write/delete no-op check
    /// (<c>NewestCommitIsOwnerAuthorizedAsync</c>) needs to know whether the ref's own current
    /// state, whatever it is, is safe to leave standing — never merely whether one path's own last
    /// write was authorized. <c>true</c> when the ref does not exist yet: there is nothing at the
    /// tip to override.
    /// </summary>
    Task<bool> IsRefTipAuthorizedAsync(
        string repositoryPath, string refName,
        Func<string, CancellationToken, Task<bool>> isAuthorizedAsync, CancellationToken cancellationToken);
}
