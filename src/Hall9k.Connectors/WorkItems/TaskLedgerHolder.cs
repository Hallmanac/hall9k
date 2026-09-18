using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Text;

namespace Hall9k.Connectors.WorkItems;

/// <summary>What a claim's holder write concluded (idea 202383dc, A3b).</summary>
public enum HolderClaimVerdict
{
    /// <summary>The write landed: the record's <see cref="TaskRecord.Holder"/> now names the candidate.</summary>
    Claimed,

    /// <summary>Another node already holds this task — named on <see cref="HolderClaimResult.Holder"/>.</summary>
    HeldByOther,

    /// <summary>
    /// This task has no ledger record to write a holder onto — not yet published, or not yet
    /// replicated to this node. Distinct from <see cref="Failed"/>: nothing was refused, there is
    /// simply nothing here to guard yet, so the caller proceeds exactly as it did before this
    /// feature existed rather than holding a task the ledger has never heard of.
    /// </summary>
    NoRecord,

    /// <summary>
    /// The write could not complete — a fetch, a push, a signing, or a read failure, or the
    /// record kept moving out from under every retry. Fails closed: the caller holds the claim
    /// rather than proceeding on an unconfirmed write (Brian, 2026-09-13, "fail closed, every
    /// project" — no per-project exemption exists for this).
    /// </summary>
    Failed,
}

/// <summary>One claim's whole answer — the verdict, and whichever of <see cref="Holder"/>/<see cref="FailureReason"/> it carries.</summary>
public sealed record HolderClaimResult(HolderClaimVerdict Verdict, TaskRecordHolder? Holder, string? FailureReason)
{
    public static HolderClaimResult Claimed(TaskRecordHolder holder) => new(HolderClaimVerdict.Claimed, holder, null);

    public static HolderClaimResult HeldByOther(TaskRecordHolder holder) => new(HolderClaimVerdict.HeldByOther, holder, null);

    public static readonly HolderClaimResult NoRecord = new(HolderClaimVerdict.NoRecord, null, null);

    public static HolderClaimResult Failed(string reason) => new(HolderClaimVerdict.Failed, null, reason);
}

/// <summary>What a release's holder write concluded.</summary>
public enum HolderReleaseVerdict
{
    /// <summary>The write landed: the record's <see cref="TaskRecord.Holder"/> is empty again.</summary>
    Released,

    /// <summary>
    /// Nothing to release — the record has no holder, the record is missing entirely, or the
    /// holder it names is not the node this release was asked on behalf of. Every one of those
    /// reads the same to a caller: this release has nothing left to do.
    /// </summary>
    NotHeld,

    /// <summary>The write could not complete — a fetch, a push, a signing, or a read failure, or the record kept moving under every retry.</summary>
    Failed,
}

public sealed record HolderReleaseResult(HolderReleaseVerdict Verdict, string? FailureReason)
{
    public static readonly HolderReleaseResult Released = new(HolderReleaseVerdict.Released, null);

    public static readonly HolderReleaseResult NotHeld = new(HolderReleaseVerdict.NotHeld, null);

    public static HolderReleaseResult Failed(string reason) => new(HolderReleaseVerdict.Failed, reason);
}

/// <summary>
/// The claim: a conditional write of a task's ledger record holder (idea 202383dc, A3b — "the
/// ledger record's holder is the truth about who has a task"). Rewrites only
/// <see cref="TaskRecord.Holder"/> on the record <c>TaskRecordPublication</c> already wrote,
/// through the identical optimistic-concurrency retry <c>TaskRecordPublication.WriteAsync</c> uses
/// against A1: read, decide, write with the blob just read as <see cref="LedgerWriteRequest.ExpectedBlobId"/>,
/// and on <see cref="LedgerWriteVerdict.Conflict"/> re-read and re-decide rather than retrying
/// blind — a holder that is now another node stands this claim down, and a record whose holder is
/// still empty (something unrelated moved under it — a concurrent revise, say) simply re-applies
/// against the fresh blob. Every other field survives byte-for-byte only within one build: the
/// round trip goes through <see cref="TaskRecord.TryParse"/> and <see cref="TaskRecord.ToYaml"/>
/// like any other ledger write, and that parser drops a field it does not recognize — so a claim
/// this build runs against a record a newer build published narrows it to the fields this build
/// understands, exactly as <c>TaskRecordPublication</c>'s own revise already does; nothing here
/// makes that exposure worse, it is simply the same one on a new path.
/// </summary>
public static class TaskLedgerHolder
{
    /// <summary>
    /// Bounded the same way <c>TaskRecordPublication.WriteAsync</c> is: two writers racing a
    /// record converge within a handful of rounds, and a write still losing after this many is
    /// either genuinely contended by more than one racer at once or fighting something a retry
    /// cannot fix.
    /// </summary>
    private const int MaxConflictRetries = 5;

    public static async Task<HolderClaimResult> TryClaimAsync(
        ILedger ledger,
        string repositoryPath,
        Guid taskId,
        TaskRecordHolder candidate,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        string refName = LedgerRefRegistry.Records.RefspecSource;
        string path = LedgerRefRegistry.RecordPath(taskId);

        try
        {
            for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
            {
                LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
                // FetchFailed ahead of Exists, the identical reason the claim's own outer
                // existence guard checks it first (DispatchEngine.TryClaimLedgerHolderAsync): a
                // read this loop cannot confirm — the caller's own first read landed, but a
                // conflict retry's re-read hits a transient outage — must never be read as
                // NoRecord, which would let this claim proceed past a holder it never actually
                // saw (Brian's 2026-09-13 fail-closed ruling, every project, no exemption).
                if (current.FetchFailed)
                {
                    return HolderClaimResult.Failed(
                        $"the ledger record at {path} could not be confirmed — the fetch failed, so this "
                        + "claim cannot tell whether a holder already exists.");
                }

                if (!current.Exists)
                {
                    return HolderClaimResult.NoRecord;
                }

                TaskRecord? existing = TaskRecord.TryParse(current.Content);
                if (existing is null)
                {
                    return HolderClaimResult.Failed(
                        $"the ledger record at {path} could not be read back as a task record — its "
                        + "content changed shape underneath this claim.");
                }

                // Already this node's: a reclaim (the ordinary automatic-follow-up cycle, or a
                // retry after this node's own earlier claim wrote the holder but never reached
                // TaskClaimed) proceeds, refreshing Since. Anyone else's: stand the claim down.
                if (existing.Holder is { } holder && holder.NodeId != candidate.NodeId)
                {
                    return HolderClaimResult.HeldByOther(holder);
                }

                TaskRecord updated = existing with { Holder = candidate };
                string content = updated.ToYaml();
                if (current.Content == content)
                {
                    return HolderClaimResult.Claimed(candidate);
                }

                LedgerWriteOutcome outcome = await ledger.WriteAsync(
                    new LedgerWriteRequest(
                        repositoryPath, refName, path, content, current.BlobId,
                        $"Claim task {taskId}", committer, signingKey),
                    cancellationToken);
                if (outcome.Verdict == LedgerWriteVerdict.Written)
                {
                    return HolderClaimResult.Claimed(candidate);
                }

                // Conflict: something moved the record since this attempt's own read — re-read
                // and re-decide on the next loop iteration rather than retrying the same content
                // blind against a blob id that is no longer there.
            }

            return HolderClaimResult.Failed(
                $"the ledger record at {path} kept moving under this claim for {MaxConflictRetries} "
                + "attempt(s) without ever settling — holding rather than retrying forever this sweep.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HolderClaimResult.Failed(
                "writing the holder to the task's ledger record failed rather than answering — "
                + $"{exception.GetType().Name}: {RelayedText.OneLine(exception.Message)}");
        }
    }

    /// <summary>
    /// Release: the same conditional write, clearing <see cref="TaskRecord.Holder"/> back to null.
    /// Only ever clears a holder that names <paramref name="expectedNodeId"/> — a record already
    /// released, missing entirely, or held by someone else, is <see cref="HolderReleaseVerdict.NotHeld"/>
    /// rather than an error: this release has nothing left to do in every one of those shapes.
    /// </summary>
    public static async Task<HolderReleaseResult> TryReleaseAsync(
        ILedger ledger,
        string repositoryPath,
        Guid taskId,
        Guid expectedNodeId,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        string refName = LedgerRefRegistry.Records.RefspecSource;
        string path = LedgerRefRegistry.RecordPath(taskId);

        try
        {
            for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
            {
                LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
                // FetchFailed ahead of Exists, the identical reason TryClaimAsync's own loop
                // checks it first: NotHeld means "confirmed nothing left to release", and a read
                // this loop could not confirm must never be read that way — the caller
                // (ReleaseLedgerHolderBestEffortAsync) would otherwise delete this node's own
                // TaskHolderReleasePending row believing the release already landed, dropping a
                // release it still owes the moment the outage clears.
                if (current.FetchFailed)
                {
                    return HolderReleaseResult.Failed(
                        $"the ledger record at {path} could not be confirmed — the fetch failed, so this "
                        + "release cannot tell whether the holder still names this node.");
                }

                if (!current.Exists)
                {
                    return HolderReleaseResult.NotHeld;
                }

                TaskRecord? existing = TaskRecord.TryParse(current.Content);
                if (existing?.Holder is not { } holder || holder.NodeId != expectedNodeId)
                {
                    return HolderReleaseResult.NotHeld;
                }

                TaskRecord updated = existing with { Holder = null };
                string content = updated.ToYaml();
                LedgerWriteOutcome outcome = await ledger.WriteAsync(
                    new LedgerWriteRequest(
                        repositoryPath, refName, path, content, current.BlobId,
                        $"Release task {taskId}", committer, signingKey),
                    cancellationToken);
                if (outcome.Verdict == LedgerWriteVerdict.Written)
                {
                    return HolderReleaseResult.Released;
                }

                // Conflict: re-read and re-decide, same as the claim side above.
            }

            return HolderReleaseResult.Failed(
                $"the ledger record at {path} kept moving under this release for {MaxConflictRetries} "
                + "attempt(s) without ever settling.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HolderReleaseResult.Failed(
                "clearing the holder on the task's ledger record failed rather than answering — "
                + $"{exception.GetType().Name}: {RelayedText.OneLine(exception.Message)}");
        }
    }
}
