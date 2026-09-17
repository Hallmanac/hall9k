using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Adoption's own read of the ledger (idea 202383dc, A3a): whether some node's task record already
/// speaks for the item <c>h9k task add --from-issue</c> or <c>--from-jira</c> was just handed, for
/// the one case the ordinary local lookup by external reference
/// (<c>TaskAddCommand.RefuseSecondAdoptionAsync</c>) cannot answer on its own — this project's own
/// event replication has not caught this task's stream up to this node yet, so nothing in the local
/// store names the reference even though another node already published it.
/// <para>
/// Reconstructing a draft from a record's own fields — the whole of what this class used to do when
/// the record still lived in a collapsed section of the GitHub issue body — is retired along with
/// that section (Brian, 2026-09-13; the record's own doc comment carries the fuller argument): a
/// record found here never seeds a new task, because a task it names is either already replicated
/// (report it, create nothing), not yet (report that too, create nothing, and let catch-up bring
/// the stream in — idea 202383dc, task 9408d525), or Abandoned (the item is released; adopt fresh
/// as though the record were never found — independent pre-PR review, cycle 1, both lenses). An
/// item with no record and no local task adopts exactly as it always did: title to objective, body
/// to context, criteria typed by hand.
/// </para>
/// </summary>
internal static class TaskRecordAdoption
{
    /// <summary>Where a ledger scan for this reference landed.</summary>
    internal enum LocateOutcome
    {
        /// <summary>No record anywhere names this reference — adopt fresh, exactly as if hall9k had never heard of the item.</summary>
        NoRecord,

        /// <summary>A record names this reference, and this store already holds a live (non-Abandoned) task at its own task id.</summary>
        ExistingLocal,

        /// <summary>A record names this reference, but this store holds no task at its own task id yet — replication has not caught up.</summary>
        StreamAbsent,

        /// <summary>
        /// A record names this reference, and this store holds a task at its own task id, but that
        /// task is Abandoned — which releases the item exactly like
        /// <see cref="TaskAddCommand.RefuseSecondAdoptionAsync"/>'s own local guard does: a human who
        /// walked away from the task released the item, and nothing ever deletes or rewrites its
        /// record to say so. Adopt fresh, the same as <see cref="NoRecord"/>.
        /// </summary>
        LocalAbandoned,
    }

    /// <summary>What <see cref="LocateAsync"/> found, and the task id it found it under (<see cref="Guid.Empty"/> for <see cref="LocateOutcome.NoRecord"/>).</summary>
    internal sealed record Locate(LocateOutcome Outcome, Guid TaskId);

    /// <summary>
    /// Scans every record under the ledger's own records prefix for one naming
    /// <paramref name="reference"/>. A record is keyed by task id
    /// (<see cref="LedgerRefRegistry.RecordPath"/>), never by a tracker reference, so finding one
    /// from only the reference means reading every record rather than one already-known path
    /// (<see cref="ILedger.ReadAllAsync"/> is the seam this reads through, per Brian's 2026-09-13
    /// testing rule: never a real repository outside <c>GitLedgerTests</c>).
    /// </summary>
    public static async Task<Locate> LocateAsync(
        IQuerySession session,
        ILedger ledger,
        string repositoryPath,
        ExternalReference reference,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LedgerEntry> entries = await ledger.ReadAllAsync(
            repositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordsPathPrefix,
            cancellationToken);
        foreach (LedgerEntry entry in entries)
        {
            TaskRecord? record = TaskRecord.TryParse(entry.Content);
            if (record?.ExternalReference != reference)
            {
                continue;
            }

            TaskListItem? local = await session.LoadAsync<TaskListItem>(record.TaskId, cancellationToken);
            LocateOutcome outcome = local switch
            {
                null => LocateOutcome.StreamAbsent,
                { State: var state } when state == TaskState.Abandoned => LocateOutcome.LocalAbandoned,
                _ => LocateOutcome.ExistingLocal,
            };
            return new Locate(outcome, record.TaskId);
        }

        return new Locate(LocateOutcome.NoRecord, Guid.Empty);
    }
}
