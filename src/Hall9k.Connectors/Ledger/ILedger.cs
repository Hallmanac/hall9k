namespace Hall9k.Connectors.Ledger;

/// <summary>
/// One write's whole ask: which ref and path, the new content, the blob the caller last read
/// there (null when the caller never read one — a write-only-if-absent request, the shape a
/// holder lock needs), who to attribute the commit to, and the signing key to sign it with, when
/// one exists (optional through A1; A2a makes it mandatory once every node has one).
/// </summary>
public sealed record LedgerWriteRequest(
    string RepositoryPath,
    string RefName,
    string Path,
    string Content,
    string? ExpectedBlobId,
    string CommitMessage,
    LedgerCommitter Committer,
    LedgerSigningKey? SigningKey = null);

public enum LedgerWriteVerdict
{
    /// <summary>The commit landed on the ref's tip, locally and on origin.</summary>
    Written,

    /// <summary>
    /// The path was not what <see cref="LedgerWriteRequest.ExpectedBlobId"/> said it was — either
    /// it changed since the caller read it, or (<c>ExpectedBlobId</c> null) it already existed
    /// when the caller asked to write only if absent. Never thrown: what a conflict means for a
    /// caller's own record is that caller's decision, not this component's.
    /// </summary>
    Conflict,
}

/// <summary>
/// What <see cref="ILedger.WriteAsync"/> actually did. <see cref="Current"/> is populated only on
/// <see cref="LedgerWriteVerdict.Conflict"/> — what is actually at the path now, so a caller can
/// decide whether to re-read, retry with a fresh <see cref="LedgerWriteRequest.ExpectedBlobId"/>,
/// or give up, without a second round trip to find out.
/// </summary>
public sealed record LedgerWriteOutcome(LedgerWriteVerdict Verdict, string? CommitId, LedgerFile? Current)
{
    public static LedgerWriteOutcome Written(string commitId) => new(LedgerWriteVerdict.Written, commitId, null);

    public static LedgerWriteOutcome Conflict(LedgerFile current) => new(LedgerWriteVerdict.Conflict, null, current);
}

/// <summary>
/// A push kept losing the race for <see cref="Attempts"/> tries in a row on a path that was never
/// itself in conflict (the retry loop's own re-fetch found <c>LedgerWriteRequest.Path</c> still
/// matched <c>LedgerWriteRequest.ExpectedBlobId</c> every time) — something is keeping the ref
/// moving faster than this node can land on it, or the push itself is failing for a reason
/// retrying cannot fix (a credential, a network partition). Thrown rather than returned: unlike
/// <see cref="LedgerWriteVerdict.Conflict"/>, there is no reasonable per-caller decision to hand
/// back here, only a failure to surface loudly rather than let the loop spin forever.
/// </summary>
public sealed class LedgerPushRejectedException(string refName, int attempts, string gitError)
    : Exception(
        $"Push to {refName} was rejected on every one of {attempts} attempt(s); Hall9k stopped " +
        $"retrying rather than spin forever. git's own last word: {gitError.Trim()}")
{
    public string RefName { get; } = refName;

    public int Attempts { get; } = attempts;
}

/// <summary>
/// Reads and writes small files at a path inside a named ref under <c>refs/hall9k/</c> in a
/// project's bare repository, by git plumbing alone — never a worktree, never a merge, never
/// anything visible in a hosting UI. Every ref a caller names here must already be registered in
/// <see cref="LedgerRefRegistry"/>: that registry is the one place a ref name is checked against
/// before this component ever touches it, so an ordinary <c>git fetch origin</c> — the project's
/// own <c>+refs/heads/*:refs/remotes/origin/*</c> — never brings a ledger ref down, and nothing
/// this component was not handed a registered name for is ever fetched or pushed. The fetch and
/// push refspec themselves are never read from the registry, though: each operation builds its
/// own, an exact single-ref refspec from the caller's own <c>refName</c> argument.
/// </summary>
public interface ILedger
{
    /// <summary>
    /// Fetches <paramref name="refName"/> fresh, then reads <paramref name="path"/> at its tip.
    /// <see cref="LedgerFile.Absent"/> when the ref does not exist yet, or exists but the path is
    /// not in its tree — both read the same to a caller deciding whether or how to write.
    /// </summary>
    Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <see cref="LedgerWriteRequest.Content"/> to <see cref="LedgerWriteRequest.Path"/> as
    /// a new commit on <see cref="LedgerWriteRequest.RefName"/>'s tip, fetching first and pushing
    /// after. A push git rejects — someone else's write landed first — fetches again and re-applies
    /// this same change fresh on the new tip, retrying up to a bounded number of times, as long as
    /// <see cref="LedgerWriteRequest.Path"/> is still what <see cref="LedgerWriteRequest.ExpectedBlobId"/>
    /// says it was; the moment it is not, retrying stops and <see cref="LedgerWriteVerdict.Conflict"/>
    /// comes back instead. Exhausting every retry on a push that keeps losing for any other reason
    /// throws <see cref="LedgerPushRejectedException"/>.
    /// </summary>
    Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken);
}
