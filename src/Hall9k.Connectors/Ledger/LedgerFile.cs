namespace Hall9k.Connectors.Ledger;

/// <summary>
/// What <see cref="ILedger.ReadAsync"/> found at one path in one ref's tip — or the honest
/// absence of it, told apart from a real empty file by <see cref="BlobId"/> rather than by
/// <see cref="Content"/> alone. <see cref="BlobId"/> is what a caller carries into a later
/// <see cref="ILedger.WriteAsync"/> as <see cref="LedgerWriteRequest.ExpectedBlobId"/>, making
/// that write conditional on nothing having changed here since this read.
/// </summary>
public sealed record LedgerFile(string? Content, string? BlobId)
{
    /// <summary>The ref does not exist yet, or exists but the path is not in its tree — both read the same.</summary>
    public static readonly LedgerFile Absent = new(null, null);

    public bool Exists => BlobId is not null;
}

/// <summary>
/// One delete's whole ask: which ref and path to remove, and the blob the caller last read there —
/// a delete is conditional on nothing having changed since that read, the same optimistic
/// concurrency <see cref="LedgerWriteRequest"/> gives a write. Project membership removal (idea
/// 202383dc, T1) is the first caller: "a member removal deletes the file" rather than leaving a
/// tombstone, unlike node revocation, which is itself a file addition.
/// </summary>
public sealed record LedgerDeleteRequest(
    string RepositoryPath,
    string RefName,
    string Path,
    string? ExpectedBlobId,
    string CommitMessage,
    LedgerCommitter Committer,
    LedgerSigningKey? SigningKey = null);
