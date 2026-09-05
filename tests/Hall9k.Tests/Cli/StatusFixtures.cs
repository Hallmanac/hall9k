using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Liveness as a test can state it. The real observer asks the operating system whether a
/// recorded process is still there, which is exactly what a unit test cannot arrange — so the
/// seam exists and this is what goes through it.
/// </summary>
internal sealed class StubSessionObserver(
    SessionLiveness liveness, IReadOnlyDictionary<int, SessionLiveness>? byProcess = null) : ISessionObserver
{
    /// <summary>
    /// Mirrors the real observer's one structural rule: a run that records no session gets no
    /// claim about one, whatever the test asked for. <paramref name="processId"/> is looked up in
    /// the per-process answers first, so a cycle whose two review passes are in different states
    /// can be stated — which is the only way to test a run with more than one session in flight.
    /// </summary>
    public SessionLiveness Observe(int? processId, DateTimeOffset? startedAt, bool onThisMachine) =>
        processId is { } pid
            ? byProcess?.GetValueOrDefault(pid, liveness) ?? liveness
            : SessionLiveness.NotApplicable;
}

/// <summary>
/// The rows every board test composes from. One place, so a change to what a row is built out of
/// lands in one file rather than in six.
/// </summary>
internal static class StatusFixtures
{
    public const string ThisMachine = "test-node";

    public static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A task in the given persisted state, with whatever a test needs to say about it.</summary>
    public static TaskListItem Task(
        TaskState state,
        Guid? runId = null,
        string? pullRequest = null,
        string objective = "x",
        Guid? projectId = null,
        DateTimeOffset? addedAt = null,
        DateTimeOffset? assignedAt = null,
        Guid? claimedByNodeId = null,
        TaskType? type = null,
        bool preApproved = false) => new()
        {
            Id = DomainId.New(),
            ProjectId = projectId ?? DomainId.New(),
            Objective = objective,
            Type = type ?? TaskType.Chore,
            State = state,
            CurrentRunId = runId,
            PullRequestUrl = pullRequest,
            AddedAt = addedAt ?? Now,
            AssignedAt = assignedAt,
            ClaimedByNodeId = claimedByNodeId,
            PreApproved = preApproved,
        };

    /// <summary>
    /// A run in the given state, with a recorded build session unless a test says otherwise. The
    /// branch defaults to a real-looking one because every run has one: RunDispatched carries it,
    /// so a blank branch is a case a test has to ask for rather than one it can fall into.
    /// DispatchedAt defaults recent for the same reason: every real run's RunDispatched carries
    /// one, and left at its zero default it would read as millennia old, spuriously triggering
    /// the interactive-claim staleness nudge on any fixture that does not ask for a specific one.
    /// </summary>
    public static RunDetails Run(
        Guid id,
        RunState state,
        int? sessionProcessId = 4711,
        AgentRole? sessionRole = null,
        int? pullRequestNumber = null,
        string branch = "task/28b19893-x",
        DateTimeOffset? dispatchedAt = null) => new()
        {
            Id = id,
            State = state,
            Branch = branch,
            ActiveSessions = sessionProcessId is { } pid ? [Session(sessionRole ?? AgentRole.Build, pid)] : [],
            PullRequestNumber = pullRequestNumber,
            DispatchedAt = dispatchedAt ?? Now.AddMinutes(-10),
        };

    /// <summary>
    /// One recorded session, with the full identity a reader can check (pid plus start time,
    /// log #2). A review pass names its track; everything else records no lens.
    /// </summary>
    public static ActiveSession Session(AgentRole role, int processId, ReviewLens? lens = null) =>
        new(role, lens ?? ReviewLens.Unknown, processId, Now.AddMinutes(-10));

    public static TaskStatusRow Compose(
        TaskListItem task,
        RunDetails? run = null,
        DateTimeOffset? silentSince = null,
        SessionLiveness liveness = SessionLiveness.Alive,
        IReadOnlyDictionary<Guid, string>? owners = null,
        IReadOnlyDictionary<Guid, string>? projects = null,
        DateTimeOffset? now = null,
        IReadOnlyDictionary<int, SessionLiveness>? livenessByProcess = null,
        DispatchPressure? pressure = null,
        int budgetParkedRuns = 0,
        IReadOnlyDictionary<Guid, int>? budgetParkedByProject = null,
        int? interactiveClaimStaleAfterDays = null) =>
        TaskStatusComposer.Compose(
            task,
            Context(
                run, silentSince, liveness, owners, projects, livenessByProcess, pressure,
                // The count is per project (backlog 40), and a test that only cares how many runs
                // this row's own project is holding says the number and lets the fixture key it.
                budgetParkedByProject ?? new Dictionary<Guid, int> { [task.ProjectId] = budgetParkedRuns },
                interactiveClaimStaleAfterDays),
            now ?? Now);

    public static TaskStatusContext Context(
        RunDetails? run = null,
        DateTimeOffset? silentSince = null,
        SessionLiveness liveness = SessionLiveness.Alive,
        IReadOnlyDictionary<Guid, string>? owners = null,
        IReadOnlyDictionary<Guid, string>? projects = null,
        IReadOnlyDictionary<int, SessionLiveness>? livenessByProcess = null,
        DispatchPressure? pressure = null,
        IReadOnlyDictionary<Guid, int>? budgetParkedRuns = null,
        int? interactiveClaimStaleAfterDays = null)
    {
        Dictionary<Guid, RunDetails> runs = run is null ? [] : new Dictionary<Guid, RunDetails> { [run.Id] = run };
        Dictionary<Guid, RunActivity> activity = run is null || silentSince is null
            ? []
            : new Dictionary<Guid, RunActivity> { [run.Id] = new() { Id = run.Id, LastActivityAt = silentSince.Value } };
        // Every fixture run belongs to this machine, so liveness is observable and the stub's
        // answer is the one the composer reads.
        Dictionary<Guid, string> nodes = run is null
            ? []
            : new Dictionary<Guid, string> { [run.NodeId] = ThisMachine };

        return new TaskStatusContext(
            runs,
            activity,
            projects ?? new Dictionary<Guid, string>(),
            owners ?? new Dictionary<Guid, string>(),
            nodes,
            new StubSessionObserver(liveness, livenessByProcess),
            ThisMachine,
            pressure,
            budgetParkedRuns,
            interactiveClaimStaleAfterDays
                ?? Hall9k.Domain.Infrastructure.Persistence.OperatingSettings.DefaultInteractiveClaimStaleAfterDays);
    }
}
