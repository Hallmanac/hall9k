using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// <c>h9k run kill</c> takes whichever id is closer to hand: a task id (its current run is what
/// gets killed) or the run id itself. Full guid, or an unambiguous fragment matched against
/// either end (UUIDv7 tails differ) — a task match wins over a run match on the same fragment,
/// since a task id is what every other <c>h9k task ...</c> command already accepts here.
/// </summary>
internal static class RunIdResolver
{
    public static async Task<(Guid TaskId, Guid RunId)> ResolveTaskOrRunAsync(
        IQuerySession session, string idOrFragment, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrFragment, out Guid id))
        {
            TaskListItem? task = await session.LoadAsync<TaskListItem>(id, cancellationToken);
            if (task is not null)
            {
                return (task.Id, CurrentRunOrThrow(task));
            }

            RunListItem? run = await session.LoadAsync<RunListItem>(id, cancellationToken);
            return run is not null
                ? (run.TaskId, run.Id)
                : throw new DomainNotFoundException($"No task or run {id}.");
        }

        string fragment = idOrFragment.Replace("-", "");
        if (fragment.Length == 0)
        {
            throw new DomainValidationException(
                $"'{idOrFragment}' has no characters to match a task or run by — pass a full id or a "
                + "non-empty fragment of one.");
        }

        IReadOnlyList<TaskListItem> tasks = await session.Query<TaskListItem>().ToListAsync(cancellationToken);
        TaskListItem[] taskMatches = [.. tasks.Where(t => MatchesFragment(t.Id, fragment))];
        if (taskMatches.Length > 1)
        {
            throw new DomainConflictException(
                $"'{idOrFragment}' is ambiguous ({taskMatches.Length} task matches) — use more characters.");
        }

        if (taskMatches is [TaskListItem singleTask])
        {
            return (singleTask.Id, CurrentRunOrThrow(singleTask));
        }

        IReadOnlyList<RunListItem> runs = await session.Query<RunListItem>().ToListAsync(cancellationToken);
        RunListItem[] runMatches = [.. runs.Where(r => MatchesFragment(r.Id, fragment))];
        return runMatches switch
        {
            [RunListItem singleRun] => (singleRun.TaskId, singleRun.Id),
            [] => throw new DomainNotFoundException($"No task or run matches '{idOrFragment}'."),
            _ => throw new DomainConflictException(
                $"'{idOrFragment}' is ambiguous ({runMatches.Length} run matches) — use more characters."),
        };
    }

    private static bool MatchesFragment(Guid id, string fragment) =>
        id.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
            || id.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase);

    private static Guid CurrentRunOrThrow(TaskListItem task) =>
        task.CurrentRunId ?? throw new DomainConflictException(
            $"Task {task.Id} has no current run to kill — {task.State.Value} names nothing live.");
}
