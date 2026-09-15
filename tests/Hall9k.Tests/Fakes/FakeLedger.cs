using Hall9k.Connectors.Ledger;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="ILedger"/> for every test above A1's own (Brian's 2026-09-13 testing
/// rule: no test outside <c>GitLedgerTests</c> and the message-transport chain reader's own tests
/// touches a real repository or remote). Keeps every write it accepted, keyed by repository, ref,
/// and path, exactly the identity <see cref="ILedger"/> itself reads and writes by — a caller
/// driving this fake sees the same conflict/blob-id behaviour a real <c>GitLedger</c> would give
/// it, without git, a signing key that actually verifies, or a network in the loop.
/// </summary>
internal sealed class FakeLedger : ILedger
{
    private sealed record StoredFile(string Content, string BlobId);

    private readonly Dictionary<(string Repository, string RefName, string Path), StoredFile> _files = [];

    /// <summary>Every write this fake actually accepted, in order — what a test asserts against
    /// (content, committer, signing key) rather than re-deriving from <see cref="ReadAsync"/>.</summary>
    public List<LedgerWriteRequest> Writes { get; } = [];

    /// <summary>Every delete this fake actually accepted, in order — the same reason <see cref="Writes"/> exists.</summary>
    public List<LedgerDeleteRequest> Deletes { get; } = [];

    public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken)
    {
        RequireRegistered(refName);
        return Task.FromResult(_files.TryGetValue((repositoryPath, refName, path), out StoredFile? stored)
            ? new LedgerFile(stored.Content, stored.BlobId)
            : LedgerFile.Absent);
    }

    public Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken)
    {
        RequireRegistered(request.RefName);
        RequireSigningKey(request.SigningKey);
        (string RepositoryPath, string RefName, string Path) key = (request.RepositoryPath, request.RefName, request.Path);
        string? currentBlobId = _files.TryGetValue(key, out StoredFile? existing) ? existing.BlobId : null;

        if (currentBlobId != request.ExpectedBlobId)
        {
            return Task.FromResult(LedgerWriteOutcome.Conflict(
                existing is null ? LedgerFile.Absent : new LedgerFile(existing.Content, existing.BlobId)));
        }

        Writes.Add(request);
        string blobId = Guid.NewGuid().ToString("N");
        _files[key] = new StoredFile(request.Content, blobId);
        return Task.FromResult(LedgerWriteOutcome.Written(blobId));
    }

    public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken)
    {
        RequireRegistered(request.RefName);
        RequireSigningKey(request.SigningKey);
        (string RepositoryPath, string RefName, string Path) key = (request.RepositoryPath, request.RefName, request.Path);
        string? currentBlobId = _files.TryGetValue(key, out StoredFile? existing) ? existing.BlobId : null;

        if (currentBlobId != request.ExpectedBlobId)
        {
            return Task.FromResult(LedgerWriteOutcome.Conflict(
                existing is null ? LedgerFile.Absent : new LedgerFile(existing.Content, existing.BlobId)));
        }

        Deletes.Add(request);
        _files.Remove(key);
        return Task.FromResult(LedgerWriteOutcome.Written(Guid.NewGuid().ToString("N")));
    }

    private static void RequireRegistered(string refName)
    {
        if (!LedgerRefRegistry.IsRegistered(refName))
        {
            throw new ArgumentException(
                $"{refName} is not registered in {nameof(LedgerRefRegistry)} — this fake enforces the "
                + "same gate the real GitLedger does.",
                nameof(refName));
        }
    }

    private static void RequireSigningKey(LedgerSigningKey? signingKey)
    {
        if (signingKey is null)
        {
            throw new DomainValidationException(
                "A ledger write needs the writing node's own signing key — this fake enforces the "
                + "same mandatory-signing gate the real GitLedger does.");
        }
    }
}
