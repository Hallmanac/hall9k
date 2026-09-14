using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Logging;

namespace Hall9k.Daemon.Purge;

/// <summary>One sweep's tally, for the loop's log line — see <c>ProjectHomeRenderLoop</c> for the pattern.</summary>
public sealed record ProjectPurgeSweepResult(
    int ProjectsPurged, int TasksDestroyed, int RunsDestroyed, int IdeasDestroyed, int EpicsDestroyed, int Failures);

/// <summary>
/// One project's own turn. <see cref="LiveWithPurgeDeadlineSet"/> is distinct from an ordinary
/// skip: a cancel or reschedule clears <c>ProjectAggregate.PurgeAt</c>, which drops the project out
/// of the sweep's own due list entirely, so it never reaches <c>PurgeOneAsync</c> at all. This flag
/// names the one path that still can: a project that reads live (<c>IsArchived=false</c>) with
/// <c>PurgeAt</c> still set — the state <c>ProjectDecider.Reactivate</c>'s own purge-pending
/// refusal exists to prevent.
/// </summary>
internal sealed record PurgeOneResult(
    bool Purged, bool LiveWithPurgeDeadlineSet, int Tasks, int Runs, int Ideas, int Epics);

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
/// Scope is the project's own stream and every task, run, idea, and epic stream it owns, with
/// their projection documents — <c>ProjectDetails</c>, <c>TaskDetails</c>/<c>TaskListItem</c>,
/// <c>RunDetails</c>/<c>RunListItem</c>, <c>IdeaDetails</c>, and <c>EpicDetails</c>. The
/// acceptance criteria for this purge name only tasks, runs, and ideas, but an epic is owned by a
/// project the same way those three are — <c>EpicAdded.ProjectId</c> is a required field, and
/// <c>h9k epic add</c> refuses without one — so leaving an epic behind is not a scope decision,
/// it is an orphan: its stream, its events, and its <c>mt_doc_epicdetails</c> row would survive
/// under a project id nothing will ever look up again, and <c>h9k epic list --state all</c> would
/// render it forever under project "?" (independent pre-PR review, cycle 1, conformance lens).
/// Deliberately still not <c>TaskLease</c> or <c>RunActivity</c>: both are mutable telemetry
/// documents, not projections of an event stream, and both are lazily-created Marten document
/// tables that may genuinely not exist yet on a node where no task has ever been claimed — a
/// <c>DELETE FROM</c> naming a table Postgres cannot find fails outright regardless of how many
/// rows would have matched, and <c>QueueSqlCommand</c> refuses more than one statement per call (a
/// guard clause and the delete can't share one round trip), so there is no safe way to delete
/// conditionally here. Either row would sit orphaned under an id nothing will ever look up again —
/// harmless, unlike a stream, event, or projection row, which is exactly what this purge is scoped to.
/// </para>
/// <para>
/// The same reasoning leaves four more documents unmentioned in the deletes above and genuinely
/// orphaned rather than accounted for by them: <c>CleanBaseGateVerdict</c>, <c>ObservedReviewRequest</c>
/// and <c>ObservedReviewMention</c> (all keyed in part by <c>ProjectId</c>), and
/// <c>TrackerClaimHold</c> (keyed by <c>TaskId</c>) are each mutable telemetry rather than
/// projections of an event stream, so none is deleted by the nine queued statements above. Each
/// sits harmlessly under an id nothing will ever look up again once its owning project or task is
/// gone (independent pre-PR review, cycle 1, conformance lens, low) — this exclusion list is not
/// exhaustive by omission, it names every document this purge deliberately leaves behind.
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

        int projectsPurged = 0, tasksDestroyed = 0, runsDestroyed = 0, ideasDestroyed = 0, epicsDestroyed = 0, failures = 0;
        foreach (ProjectDetails project in due)
        {
            try
            {
                PurgeOneResult result = await PurgeOneAsync(project, cancellationToken);
                if (result.LiveWithPurgeDeadlineSet)
                {
                    // Not "cancelled or rescheduled" — those clear PurgeAt, so the project would
                    // have dropped out of this sweep's own due list. This project reads live
                    // (IsArchived=false) with PurgeAt still set, the exact state ProjectDecider
                    // .Reactivate's own fence exists to prevent; some writer reached
                    // ProjectReactivated without going through it. Warned, not merely logged,
                    // because nothing else names this until h9k project cancel-purge clears it, and
                    // this project stays in the due list forever otherwise (independent pre-PR
                    // review, cycle 3, adversarial lens, medium).
                    logger.LogWarning(
                        "Project '{Name}' ({Id}) reads live but still has a purge deadline set — "
                        + "not destroyed. This is not an ordinary cancellation: a writer reactivated "
                        + "it without going through ProjectDecider.Reactivate's own purge-pending "
                        + "refusal. Resolve with h9k project cancel-purge, naming this project.",
                        project.Name, project.Id);
                    continue;
                }

                if (!result.Purged)
                {
                    logger.LogInformation(
                        "Skipped project '{Name}' ({Id}): its purge was cancelled or rescheduled after "
                        + "this sweep's own due list was read; nothing was touched.",
                        project.Name, project.Id);
                    continue;
                }

                int tasks = result.Tasks, runs = result.Runs, ideas = result.Ideas, epics = result.Epics;

                projectsPurged++;
                tasksDestroyed += tasks;
                runsDestroyed += runs;
                ideasDestroyed += ideas;
                epicsDestroyed += epics;

                logger.LogWarning(
                    "Purged project '{Name}' ({Id}): destroyed {Tasks} task(s), {Runs} run(s), {Ideas} "
                    + "idea(s), and {Epics} epic(s) — every stream, event, and projection row for the "
                    + "project and everything it owned. This is this install's own database only; the "
                    + "repository, linked tracker items, and the home directory ({Home}) were never "
                    + "touched and remain exactly as they were.",
                    project.Name, project.Id, tasks, runs, ideas, epics,
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

        return new ProjectPurgeSweepResult(projectsPurged, tasksDestroyed, runsDestroyed, ideasDestroyed, epicsDestroyed, failures);
    }

    /// <summary>
    /// One project's own transaction: gather every id it owns, queue every delete, commit once.
    /// A fresh session per project so one project's failure never rolls back another's, already
    /// committed sweep in the same tick.
    /// </summary>
    private async Task<PurgeOneResult> PurgeOneAsync(
        ProjectDetails project, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        // Exclusive, not a plain re-read (independent pre-PR review, cycle 1: conformance and
        // adversarial lenses, both medium — the same race, reported by each lens on its own
        // pass). FetchForExclusiveWriting takes a `SELECT ... FOR UPDATE` lock on this project's
        // own stream row on this session's own transaction and holds it from this read straight
        // through SaveChangesAsync below, closing the window a plain re-read (Copilot review, PR
        // #338) only narrowed: "the whole sweep's own due-list query" down to "one project's own
        // turn in the loop", not to zero. h9k project cancel-purge's own fenced append
        // (ProjectCancelPurgeCommand) needs this exact row locked to bump its expected version, so
        // a concurrent cancel now either lands and commits entirely before this lock is taken —
        // this sweep's own read below then sees it live, the ordinary skip path just below — or it
        // blocks until this transaction commits or rolls back. If this purge commits first, the
        // blocked cancel's own version check then finds a stream that no longer exists and fails
        // loudly (DomainConflictException) rather than printing "nothing was destroyed" over a
        // project that is; if this purge instead rolls back (a failure elsewhere in this method),
        // the blocked cancel proceeds normally and the next sweep re-reads a live project.
        IEventStream<ProjectAggregate> stream = await session.Events.FetchForExclusiveWriting<ProjectAggregate>(
            project.Id, cancellationToken);
        ProjectAggregate? aggregate = stream.Aggregate;
        if (aggregate is null || aggregate.PurgeAt is null || aggregate.PurgeAt > DateTimeOffset.UtcNow)
        {
            return new PurgeOneResult(Purged: false, LiveWithPurgeDeadlineSet: false, 0, 0, 0, 0);
        }

        // Liveness, not only the deadline: the invariant this re-check leans on ("a project with
        // PurgeAt set is archived") is enforced by ProjectDecider.Reactivate, but only when every
        // writer that can append ProjectReactivated goes through it fenced — an unfenced writer
        // racing a schedule can still leave IsArchived=false with PurgeAt set (independent pre-PR
        // review, cycle 1, adversarial lens, high). Checked here too, defensively, so a project
        // that reads as live is never destroyed regardless of how it got that way — reported back
        // distinctly from an ordinary cancel or reschedule, since neither of those happened
        // (independent pre-PR review, cycle 3, adversarial lens, medium).
        if (!aggregate.IsArchived)
        {
            return new PurgeOneResult(Purged: false, LiveWithPurgeDeadlineSet: true, 0, 0, 0, 0);
        }

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
        Guid[] epicIds = [.. (await session.Query<EpicDetails>()
            .Where(epic => epic.ProjectId == project.Id)
            .Select(epic => epic.Id)
            .ToListAsync(cancellationToken))];

        Guid[] everyStreamId = [project.Id, .. taskIds, .. runIds, .. ideaIds, .. epicIds];

        // The spend governor's own undercount (independent pre-PR review, cycle 1, adversarial
        // lens, medium): PeriodSpend.ReadAsync sums TokensRecorded (on a run's own stream) and
        // PublicationTokensRecorded (on a task's own stream) live, across the whole install, with
        // no regard for whether the stream that recorded them still exists — so deleting them
        // below would silently hand the purged project's own in-period spend back to the node's
        // budget. Excluding just these two event types from the events delete does not save them:
        // mt_events.stream_id cascades from mt_streams.id (confirmed against a real database), so
        // deleting the stream row two statements down would erase them anyway regardless of any
        // type filter on the events delete itself. Carrying the total forward onto a plain
        // PurgedSpendRecord document — never a projection of an event stream, so untouched by
        // either delete — is the only way to keep both: every stream, event, and projection row
        // for this project gone, and its own already-spent tokens still counted against the
        // node's budget for the rest of the period they were recorded in.
        Guid[] spendBearingStreamIds = [.. runIds, .. taskIds];
        if (spendBearingStreamIds.Length > 0)
        {
            IReadOnlyList<IEvent> spendCandidates = await session.Events.QueryAllRawEvents()
                .Where(e => spendBearingStreamIds.Contains(e.StreamId))
                .ToListAsync(cancellationToken);
            foreach (IEvent candidate in spendCandidates)
            {
                (long TotalInputTokens, AgentModel Model, DateTimeOffset RecordedAt)? spend = candidate.Data switch
                {
                    TokensRecorded e => (e.InputTokens + e.CacheReadInputTokens + e.CacheCreationInputTokens,
                        e.Model ?? AgentModel.Unknown, e.RecordedAt),
                    PublicationTokensRecorded e => (e.InputTokens + e.CacheReadInputTokens + e.CacheCreationInputTokens,
                        e.Model ?? AgentModel.Unknown, e.RecordedAt),
                    _ => null,
                };
                if (spend is { } toCarryForward)
                {
                    session.Store(new PurgedSpendRecord(
                        DomainId.New(), toCarryForward.RecordedAt, toCarryForward.TotalInputTokens, toCarryForward.Model));
                }
            }
        }

        // Events before streams: safe regardless of whether mt_events.stream_id cascades from
        // mt_streams.id, since a child row never blocks on its own deletion.
        session.QueueSqlCommand("delete from mt_events where stream_id = ANY(?)", everyStreamId);
        session.QueueSqlCommand("delete from mt_streams where id = ANY(?)", everyStreamId);
        session.QueueSqlCommand("delete from mt_doc_projectdetails where id = ?", project.Id);
        // ProjectGitHubMembers is keyed by the project's own id (idea 202383dc, A2b) but, like
        // ProjectDetails, is never a stream in its own right, so the events/streams deletes above
        // never reach it — left behind otherwise, keeping every observed collaborator's GitHub
        // identity on file for a project id that no longer exists (independent pre-PR review,
        // cycle 3, adversarial lens, medium). Marten's own typed delete, not another raw
        // QueueSqlCommand: unlike mt_doc_projectdetails (guaranteed to exist — this very method
        // already loaded a row from it), mt_doc_projectgithubmembers is created lazily, on this
        // projection's first-ever write, the same way TaskLease and RunActivity are (this file's
        // own doc comment on both) — an install that has never observed GitHub repository access
        // for any project has no such table yet, and a raw DELETE naming it outright fails
        // regardless of whether this project ever had a row. Routing through Marten's own document
        // API instead of SQL lets its schema-on-demand handling create the table first rather than
        // finding it missing.
        session.Delete<ProjectGitHubMembers>(project.Id);

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

        if (epicIds.Length > 0)
        {
            session.QueueSqlCommand("delete from mt_doc_epicdetails where id = ANY(?)", epicIds);
        }

        await session.SaveChangesAsync(cancellationToken);
        return new PurgeOneResult(
            Purged: true, LiveWithPurgeDeadlineSet: false,
            taskIds.Length, runIds.Length, ideaIds.Length, epicIds.Length);
    }
}
