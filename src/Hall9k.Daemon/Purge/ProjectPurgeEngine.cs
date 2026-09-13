using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;
using Microsoft.Extensions.Logging;

namespace Hall9k.Daemon.Purge;

/// <summary>One sweep's tally, for the loop's log line — see <c>ProjectHomeRenderLoop</c> for the pattern.</summary>
public sealed record ProjectPurgeSweepResult(
    int ProjectsPurged, int TasksDestroyed, int RunsDestroyed, int IdeasDestroyed, int Failures);

/// <summary>
/// Carries out a due <c>ProjectPurgeScheduled</c> deadline (task: an archived project can be
/// purged — the second half of the two-tier project-removal design, PLAN.md §16 #182's purge
/// follow-up; the one explicit exception to the platform's nothing-is-deleted doctrine, Brian's
/// ruling 2026-08-29). Precedent: the operator hand-purged the smoke-test fossil on 2026-08-29 —
/// its events, its stream row, and five projection documents, deleted in one transaction against
/// <c>mt_events</c> and <c>mt_streams</c>. This automates exactly that, at project scope, for the
/// project and every task, run, and idea it owns.
/// <para>
/// Raw SQL, not Marten's own session APIs, because there is no session-level "hard-delete this
/// stream and every document keyed to it" operation to call — <c>QueueSqlCommand</c> queues the
/// deletes into the same transaction <see cref="IDocumentSession.SaveChangesAsync"/> commits, so a
/// purge is atomic: either every row for a project is gone, or (a failure mid-sweep) none of it
/// is, and the next sweep retries the same project from scratch. Events are deleted before streams
/// (rather than the reverse) so this never depends on whether <c>mt_events.stream_id</c> cascades
/// from <c>mt_streams.id</c> — deleting the child rows first is safe regardless.
/// </para>
/// <para>
/// Scope is exactly what the acceptance criteria name: the project's own stream, every task, run,
/// and idea stream it owns, and their projection documents — <c>ProjectDetails</c>,
/// <c>TaskDetails</c>/<c>TaskListItem</c>, <c>RunDetails</c>/<c>RunListItem</c>, and
/// <c>IdeaDetails</c>. Deliberately not <c>TaskLease</c> or <c>RunActivity</c>: both are mutable
/// telemetry documents, not projections of an event stream, and both are lazily-created Marten
/// document tables that may genuinely not exist yet on a node where no task has ever been claimed
/// — a <c>DELETE FROM</c> naming a table Postgres cannot find fails outright regardless of how
/// many rows would have matched, and <c>QueueSqlCommand</c> refuses more than one statement per
/// call (a guard clause and the delete can't share one round trip), so there is no safe way to
/// delete conditionally here. Either row would sit orphaned under an id nothing will ever look up
/// again — harmless, unlike a stream, event, or projection row, which is exactly what the
/// acceptance criteria scope this purge to. Epics are deliberately out of scope too: nothing in
/// the archive design (PLAN.md §16 #182) treats an epic as owned by a project the way a task, run,
/// or idea is, and the acceptance criteria for this purge name only those three — extending
/// deletion to epics would be reshaping a design this task was handed, not building on it.
/// </para>
/// </summary>
public sealed class ProjectPurgeEngine(IDocumentStore store, ILogger<ProjectPurgeEngine> logger)
{
    public async Task<ProjectPurgeSweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<ProjectDetails> due;
        await using (IQuerySession query = store.QuerySession())
        {
            due = await query.Query<ProjectDetails>()
                .Where(project => project.PurgeAt != null && project.PurgeAt <= now)
                .ToListAsync(cancellationToken);
        }

        int projectsPurged = 0, tasksDestroyed = 0, runsDestroyed = 0, ideasDestroyed = 0, failures = 0;
        foreach (ProjectDetails project in due)
        {
            try
            {
                (int tasks, int runs, int ideas) = await PurgeOneAsync(project, cancellationToken);
                projectsPurged++;
                tasksDestroyed += tasks;
                runsDestroyed += runs;
                ideasDestroyed += ideas;

                logger.LogWarning(
                    "Purged project '{Name}' ({Id}): destroyed {Tasks} task(s), {Runs} run(s), and "
                    + "{Ideas} idea(s) — every stream, event, and projection row for the project and "
                    + "everything it owned. This is this install's own database only; the repository, "
                    + "linked tracker items, and the home directory ({Home}) were never touched and "
                    + "remain exactly as they were.",
                    project.Name, project.Id, tasks, runs, ideas,
                    project.HomeDirectory.HasValue ? project.HomeDirectory.Value : "none recorded");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures++;
                logger.LogError(
                    exception, "Purge failed for project '{Name}' ({Id}); will retry next sweep",
                    project.Name, project.Id);
            }
        }

        return new ProjectPurgeSweepResult(projectsPurged, tasksDestroyed, runsDestroyed, ideasDestroyed, failures);
    }

    /// <summary>
    /// One project's own transaction: gather every id it owns, queue every delete, commit once.
    /// A fresh session per project so one project's failure never rolls back another's, already
    /// committed sweep in the same tick.
    /// </summary>
    private async Task<(int Tasks, int Runs, int Ideas)> PurgeOneAsync(
        ProjectDetails project, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        Guid[] taskIds = [.. (await session.Query<TaskListItem>()
            .Where(task => task.ProjectId == project.Id)
            .Select(task => task.Id)
            .ToListAsync(cancellationToken))];
        Guid[] runIds = taskIds.Length == 0
            ? []
            : [.. (await session.Query<RunListItem>()
                .Where(run => taskIds.Contains(run.TaskId))
                .Select(run => run.Id)
                .ToListAsync(cancellationToken))];
        Guid[] ideaIds = [.. (await session.Query<IdeaDetails>()
            .Where(idea => idea.ProjectId == project.Id)
            .Select(idea => idea.Id)
            .ToListAsync(cancellationToken))];

        Guid[] everyStreamId = [project.Id, .. taskIds, .. runIds, .. ideaIds];

        // Events before streams: safe regardless of whether mt_events.stream_id cascades from
        // mt_streams.id, since a child row never blocks on its own deletion.
        session.QueueSqlCommand("delete from mt_events where stream_id = ANY(?)", everyStreamId);
        session.QueueSqlCommand("delete from mt_streams where id = ANY(?)", everyStreamId);
        session.QueueSqlCommand("delete from mt_doc_projectdetails where id = ?", project.Id);

        if (taskIds.Length > 0)
        {
            session.QueueSqlCommand("delete from mt_doc_taskdetails where id = ANY(?)", taskIds);
            session.QueueSqlCommand("delete from mt_doc_tasklistitem where id = ANY(?)", taskIds);
        }

        if (runIds.Length > 0)
        {
            session.QueueSqlCommand("delete from mt_doc_rundetails where id = ANY(?)", runIds);
            session.QueueSqlCommand("delete from mt_doc_runlistitem where id = ANY(?)", runIds);
        }

        if (ideaIds.Length > 0)
        {
            session.QueueSqlCommand("delete from mt_doc_ideadetails where id = ANY(?)", ideaIds);
        }

        await session.SaveChangesAsync(cancellationToken);
        return (taskIds.Length, runIds.Length, ideaIds.Length);
    }
}
