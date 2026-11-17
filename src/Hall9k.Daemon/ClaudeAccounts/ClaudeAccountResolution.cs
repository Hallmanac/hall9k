using Hall9k.Connectors.ClaudeCode;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.PrReviewPreflight;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;

namespace Hall9k.Daemon.ClaudeAccounts;

/// <summary>
/// Where each daemon spawn site gets its account: a run's frozen account for anything spawned for a run, and
/// the project's current binding for everything else. Static over the store each engine already holds, so no
/// engine gains a dependency for it.
/// </summary>
public static class ClaudeAccountResolution
{
    /// <summary>The account <paramref name="run"/> froze at dispatch, or the default login for a run that froze none.</summary>
    public static Task<ClaudeCodeAccount> ForRunAsync(
        IQuerySession session, RunDetails run, CancellationToken cancellationToken) =>
        ClaudeCodeAccounts.LoadAsync(session, ClaudeCodeAccountBinding.ForRun(run), cancellationToken);

    public static async Task<ClaudeCodeAccount> ForRunAsync(
        IDocumentStore store, Guid runId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        return run is null
            ? ClaudeCodeAccount.Default
            : await ForRunAsync(session, run, cancellationToken);
    }

    /// <summary>The account a pre-flight dispatched by <paramref name="dispatchingRunId"/> runs under (<see cref="ClaudeCodeAccountBinding.ForPreflight"/>).</summary>
    public static async Task<ClaudeCodeAccount> ForPreflightAsync(
        IDocumentStore store, Guid dispatchingRunId, ProjectDetails project, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        RunDetails? dispatchingRun = await session.LoadAsync<RunDetails>(dispatchingRunId, cancellationToken);
        return await ClaudeCodeAccounts.LoadAsync(
            session, ClaudeCodeAccountBinding.ForPreflight(dispatchingRun, project), cancellationToken);
    }

    /// <summary>The account an already-dispatched pre-flight runs under, read from its own row.</summary>
    public static async Task<ClaudeCodeAccount> ForPreflightRowAsync(
        IDocumentStore store, Guid preflightRunId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        PrReviewPreflightDetails? preflight = await session.LoadAsync<PrReviewPreflightDetails>(preflightRunId, cancellationToken);
        TaskDetails? task = preflight is null
            ? null
            : await session.LoadAsync<TaskDetails>(preflight.TaskId, cancellationToken);
        ProjectDetails? project = task is null
            ? null
            : await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (preflight is null || project is null)
        {
            return ClaudeCodeAccount.Default;
        }

        RunDetails? dispatchingRun = await session.LoadAsync<RunDetails>(preflight.DispatchingRunId, cancellationToken);
        return await ClaudeCodeAccounts.LoadAsync(
            session, ClaudeCodeAccountBinding.ForPreflight(dispatchingRun, project), cancellationToken);
    }

    /// <summary>The account <paramref name="project"/> is bound to now.</summary>
    public static Task<ClaudeCodeAccount> ForProjectAsync(
        IQuerySession session, ProjectDetails project, CancellationToken cancellationToken) =>
        ClaudeCodeAccounts.LoadAsync(session, ClaudeCodeAccountBinding.ForProject(project), cancellationToken);

    public static async Task<ClaudeCodeAccount> ForProjectAsync(
        IDocumentStore store, Guid projectId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(projectId, cancellationToken);
        return project is null
            ? ClaudeCodeAccount.Default
            : await ForProjectAsync(session, project, cancellationToken);
    }

    /// <summary>The current binding of the project <paramref name="taskId"/> belongs to, for a session spawned for a task but not for a run.</summary>
    public static async Task<ClaudeCodeAccount> ForTaskAsync(
        IDocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
        ProjectDetails? project = task is null
            ? null
            : await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        return project is null
            ? ClaudeCodeAccount.Default
            : await ForProjectAsync(session, project, cancellationToken);
    }

    /// <summary>The account a courier runs under (<see cref="ClaudeCodeAccountBinding.ForCourier"/>).</summary>
    public static async Task<ClaudeCodeAccount> ForCourierAsync(
        IDocumentStore store, CourierRunDispatched dispatched, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(dispatched.ProjectId, cancellationToken);
        return await ClaudeCodeAccounts.LoadAsync(
            session, ClaudeCodeAccountBinding.ForCourier(dispatched, project), cancellationToken);
    }

    /// <summary>The current binding of the project a plan belongs to, for its discovery sessions.</summary>
    public static async Task<ClaudeCodeAccount> ForPlanAsync(
        IDocumentStore store, Guid planId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        PlanDetails? plan = await session.LoadAsync<PlanDetails>(planId, cancellationToken);
        ProjectDetails? project = plan is null
            ? null
            : await session.LoadAsync<ProjectDetails>(plan.ProjectId, cancellationToken);
        return project is null
            ? ClaudeCodeAccount.Default
            : await ForProjectAsync(session, project, cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="project"/>'s bound account is refused right now, which is when no new work starts
    /// for it: the dispatcher, the plan, feedback-step, and auto-pr-review engines all ask this beside the
    /// node-wide launch hold. Always false for an unbound project.
    /// </summary>
    public static async Task<bool> IsRefusedAsync(
        IQuerySession session, ProjectDetails project, CancellationToken cancellationToken) =>
        project.ClaudeCodeConnectionId is { } connectionId
        && await session.LoadAsync<ConnectionDetails>(connectionId, cancellationToken) is { ClaudeCodeRefused: true };

    public static async Task<bool> IsRefusedAsync(IDocumentStore store, Guid projectId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        return await session.LoadAsync<ProjectDetails>(projectId, cancellationToken) is { } project
            && await IsRefusedAsync(session, project, cancellationToken);
    }

    /// <summary>
    /// Which of <paramref name="projectIds"/> are bound to a refused account, read once per sweep so a gate over
    /// many candidates costs two document loads rather than two per candidate.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> RefusedProjectIdsAsync(
        IQuerySession session, IEnumerable<Guid> projectIds, CancellationToken cancellationToken)
    {
        Guid[] distinct = [.. projectIds.Distinct()];
        if (distinct.Length == 0)
        {
            return new HashSet<Guid>();
        }

        IReadOnlyList<ProjectDetails> bound = [.. (await session.LoadManyAsync<ProjectDetails>(cancellationToken, distinct))
            .Where(project => project.ClaudeCodeConnectionId is not null)];
        if (bound.Count == 0)
        {
            return new HashSet<Guid>();
        }

        Guid[] connectionIds = [.. bound.Select(project => project.ClaudeCodeConnectionId).OfType<Guid>().Distinct()];
        HashSet<Guid> refusedConnections = [.. (await session.LoadManyAsync<ConnectionDetails>(cancellationToken, connectionIds))
            .Where(connection => connection.ClaudeCodeRefused)
            .Select(connection => connection.Id)];
        return ClaudeAccountRefusals.RefusedProjects(
            bound.Select(project => (project.Id, project.ClaudeCodeConnectionId)), refusedConnections);
    }

    /// <summary>The freeze a dispatch appends beside <see cref="RunDispatched"/>: the project's binding as it stands now.</summary>
    public static RunClaudeCodeAccountRecorded Freeze(Guid runId, ProjectDetails project, DateTimeOffset recordedAt) =>
        new(runId, ClaudeCodeAccountBinding.ForProject(project), recordedAt);

    /// <summary>The freeze for an account already resolved, so the record and the spawn name the same one.</summary>
    public static RunClaudeCodeAccountRecorded Freeze(Guid runId, ClaudeCodeAccount account, DateTimeOffset recordedAt) =>
        new(runId, account.ConnectionId, recordedAt);
}
