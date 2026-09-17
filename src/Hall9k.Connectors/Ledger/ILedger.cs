namespace Hall9k.Connectors.Ledger;

/// <summary>
/// One write's whole ask: which ref and path, the new content, the blob the caller last read
/// there (null when the caller never read one — a write-only-if-absent request, the shape a
/// holder lock needs), who to attribute the commit to, and the signing key to sign it with, when
/// one exists (optional through A1; A2a makes it mandatory once every node has one).
/// <see cref="RequireEmptyPrefix"/> is the additional, ref-wide compare-and-swap a "genesis, only
/// once, ever" write needs: <see cref="ExpectedBlobId"/> alone only ever guards this one write's
/// own path, so two callers racing to establish a genesis file under two different paths (two
/// different fingerprints, say) would each see their own path absent and both land — this option
/// is checked against the ref's own freshly-fetched tip inside the same retry attempt that builds
/// and pushes the commit, so a rival's genesis write that lands in the gap is caught by this
/// write's own next retry, not merely by whichever caller happened to observe an empty prefix
/// first (independent review finding: the prior check-then-write across two separate calls left
/// exactly that gap open).
/// </summary>
public sealed record LedgerWriteRequest(
    string RepositoryPath,
    string RefName,
    string Path,
    string Content,
    string? ExpectedBlobId,
    string CommitMessage,
    LedgerCommitter Committer,
    LedgerSigningKey? SigningKey = null,
    string? RequireEmptyPrefix = null);

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
/// One ref <see cref="ILedger.ListRefsAsync"/> found on origin, paired with its current tip commit
/// SHA — <c>git ls-remote</c> already returns this alongside the ref name, so a caller scanning
/// many refs on a cadence (idea 202383dc, T2's own invite sweep) can tell an unmoved ref apart from
/// one worth a real <see cref="ILedger.ReadAsync"/> fetch without paying for that fetch first.
/// </summary>
public sealed record LedgerRef(string RefName, string Sha);

/// <summary>
/// One file <see cref="ILedger.ReadAllAsync"/> found under a prefix inside one ref's tree — the
/// same <see cref="LedgerFile.Content"/>/<see cref="LedgerFile.BlobId"/> shape <see cref="ILedger.ReadAsync"/>
/// answers for a single known path, paired with the path it actually sat at (idea 202383dc, A3a:
/// adoption has to find a task record by the external reference it carries, never the task id its
/// path is keyed by, so it has no single path to ask <see cref="ILedger.ReadAsync"/> for).
/// </summary>
public sealed record LedgerEntry(string Path, string Content, string BlobId);

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

    /// <summary>
    /// Removes <see cref="LedgerDeleteRequest.Path"/> from a new commit on
    /// <see cref="LedgerDeleteRequest.RefName"/>'s tip — a genuine tree deletion, not a
    /// content-only tombstone. Same optimistic concurrency and retry behavior as
    /// <see cref="WriteAsync"/>: <see cref="LedgerWriteVerdict.Conflict"/> when the path no longer
    /// matches <see cref="LedgerDeleteRequest.ExpectedBlobId"/>, and the identical push-rejected
    /// retry loop otherwise.
    /// </summary>
    Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches <paramref name="refName"/> fresh, then whether any path under
    /// <paramref name="pathPrefix"/> currently exists in its tree — <see langword="false"/> both
    /// when the ref does not exist yet and when it exists but nothing under the prefix does. What a
    /// genesis-style "only when the folder is empty" write checks before writing: <see cref="ReadAsync"/>'s
    /// own single-path, write-if-absent shape only ever answers about the one path a caller already
    /// knows the name of, never "has anything at all ever landed here" (independent pre-PR review,
    /// cycle 1, conformance and adversarial lenses, medium: a per-fingerprint absence check let a
    /// second, later joiner self-claim ownership in a project that already had one).
    /// </summary>
    Task<bool> HasAnyAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken);

    /// <summary>
    /// Every ref currently on origin that starts with <paramref name="refPrefix"/>, each paired
    /// with its own current tip SHA (<see cref="LedgerRef"/>) — the "list refs", not "list paths
    /// inside one ref", primitive: a caller enumerating a prefix registered as
    /// <see cref="LedgerRefKind.Prefix"/> (one ref per node id, one per owner root) has no other
    /// way to discover which concrete ref names exist at all, unlike <see cref="HasAnyAsync"/>,
    /// which only ever answers about paths inside a single, already-known ref (idea 202383dc, T2's
    /// invite sweep: which node refs carry a candidate proof to check, and — via the tip SHA — which
    /// of those actually changed since the sweep's own last look).
    /// Mirrors <c>GitLedgerChainReader.DiscoverOwnerRootsAsync</c>'s own private
    /// <c>git ls-remote</c> technique, duplicated onto this seam rather than shared across it: that
    /// class deliberately bypasses <see cref="ILedger"/> entirely for its own reasons (its own doc
    /// comment), so this is the first caller that actually needs the "list refs" shape through the
    /// ordinary seam every fake and every other caller already uses.
    /// </summary>
    Task<IReadOnlyList<LedgerRef>> ListRefsAsync(string repositoryPath, string refPrefix, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches <paramref name="refName"/> fresh, then every file that currently sits under
    /// <paramref name="pathPrefix"/> in its tree, each paired with its own path — empty both when
    /// the ref does not exist yet and when nothing sits under the prefix. Unlike <see cref="ReadAsync"/>,
    /// which only ever answers about one path a caller already knows the name of, this is the
    /// "read everything here" primitive a caller keyed on something other than the path itself
    /// needs: idea 202383dc's task records are keyed by task id (<c>records/&lt;task-id&gt;.yaml</c>),
    /// but adoption is handed an external reference and has to find the one record naming it, which
    /// means reading every record under the prefix rather than one already-known path.
    /// </summary>
    Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken);
}
