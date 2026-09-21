using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The ledger's own answer to "which task is this, and whose project is it" for a task this node
/// does not hold (task 9eb5b245). Every published task has a record at
/// <c>records/&lt;task-id&gt;.yaml</c> keyed by its full id, and every member of the project can
/// read it — so the ledger is the one local source that can turn a short id into a full one, and a
/// task id into the project to ask about it, for a task whose own stream is not here.
/// <para>
/// This is what makes <c>h9k task pull</c> typeable from what a human actually has in front of
/// them. The id they read off <c>h9k status</c>, a board row, a pull request title or a branch name
/// is the short id — the full one lived only on the node that held the task, which is precisely the
/// node they are not sitting at.
/// </para>
/// <para>
/// Reads through <see cref="ILedger.ReadAllAsync"/>, the same seam
/// <see cref="TaskRecordAdoption.LocateAsync"/> reads adoption's own records through, so no test
/// here needs a real repository (Brian's 2026-09-13 testing rule). Unlike every other read in
/// <c>h9k task pull</c>, this one does fetch a ref, so callers only reach for it when they
/// genuinely cannot answer locally.
/// </para>
/// </summary>
internal static class LedgerTaskLookup
{
    /// <summary>One project's ledger naming one task, and that task's full id.</summary>
    internal sealed record Match(Guid TaskId, ProjectDetails Project);

    /// <summary>
    /// Every (task, project) pair whose ledger record matches <paramref name="idOrFragment"/>,
    /// across <paramref name="projects"/> — a full id matches exactly, and a fragment matches either
    /// end of the id with the dashes stripped, the identical convention
    /// <see cref="TaskIdResolver"/> applies to a task this node already holds (a UUIDv7's own head
    /// is shared across ids minted in the same period, so the tail is what people quote).
    /// <para>
    /// One task id can appear under two projects only when two projects' ledgers genuinely both
    /// carry a record for it, which is a real ambiguity rather than a duplicate: the same task id
    /// pulled into the wrong project would ask the wrong members. So matches are deduplicated per
    /// (task, project) pair and the caller decides what more than one means.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<Match>> FindAsync(
        ILedger ledger, IReadOnlyList<ProjectDetails> projects, string idOrFragment, CancellationToken cancellationToken)
    {
        string fragment = idOrFragment.Replace("-", string.Empty);
        if (fragment.Length == 0)
        {
            throw new DomainValidationException(
                $"'{idOrFragment}' has no characters to match a task by — pass a full id or a "
                + "non-empty fragment of one.");
        }

        List<Match> matches = [];
        foreach (ProjectDetails project in projects)
        {
            IReadOnlyList<LedgerEntry> entries = await ledger.ReadAllAsync(
                project.RepositoryPath, LedgerRefRegistry.Records.RefspecSource,
                LedgerRefRegistry.RecordsPathPrefix, cancellationToken);
            foreach (LedgerEntry entry in entries)
            {
                if (TaskRecord.TryParse(entry.Content) is { } record
                    && Matches(record.TaskId, fragment)
                    && !matches.Any(found => found.TaskId == record.TaskId && found.Project.Id == project.Id))
                {
                    matches.Add(new Match(record.TaskId, project));
                }
            }
        }

        return matches;
    }

    private static bool Matches(Guid taskId, string fragment)
    {
        string id = taskId.ToString("N");
        return id.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
            || id.EndsWith(fragment, StringComparison.OrdinalIgnoreCase);
    }
}
