using System.Text.Json;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The lesson picker session against a real store: what it is spawned with, how every way it can
/// end is recorded, and that a task never gets a second pick. Lessons are in-memory
/// <see cref="LessonCandidates"/>; only the events (the pick on the task's stream, the session's
/// tokens on the run's) need Postgres.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class LessonPickerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid ProjectId = DomainId.New();
    private static readonly Guid ThisNode = DomainId.New();

    private readonly string _runDirectory = Directory.CreateTempSubdirectory("hall9k-lesson-picker-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_runDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_pick_spawns_one_toolless_turn_on_the_couriers_model_and_records_what_it_chose()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid runId, TaskDetails task) = await SeedAsync(cts.Token);
        List<LearningDetails> lessons = Lessons(6);
        LessonCandidates candidates = Candidates(lessons, new LessonInjectionCaps(3, 10_000), LessonPickerMode.On);
        string answer = $"{Short(lessons[5])}, {Short(lessons[2])} 00000000 {Short(lessons[5])}";
        ScriptedExecutor executor = new(answer);
        DaemonOptions daemon = new();

        LessonPickRecorded? recorded = await NewPicker(executor, daemon).PickAsync(
            runId, _runDirectory, task, Project(), candidates, ExecutorMode.Subscription, cts.Token);

        recorded.Should().NotBeNull();
        recorded!.Outcome.Should().Be(LessonPickOutcome.Picked);
        recorded.Mode.Should().Be(LessonPickerMode.On);
        recorded.PickedLessonIds.Should().Equal(lessons[5].Id, lessons[2].Id);
        recorded.NewestFirstLessonIds.Should().Equal(lessons[0].Id, lessons[1].Id, lessons[2].Id);
        recorded.EligibleCount.Should().Be(6);
        recorded.DroppedIdCount.Should().Be(2, "one id named no lesson and one repeated another");
        recorded.Model.Should().Be(daemon.ResolveCourierModel(null));

        AgentSpawnRequest spawn = executor.Spawns.Should().ContainSingle().Subject;
        spawn.NoTools.Should().BeTrue();
        spawn.MaxTurns.Should().Be(1);
        spawn.UntrustedWorkingDirectory.Should().BeTrue();
        spawn.SkipPermissions.Should().BeFalse();
        spawn.WorktreePath.Should().Be(_runDirectory);
        spawn.SessionName.Should().EndWith("-lesson-picker");
        spawn.TaskId.Should().Be(task.Id);
        spawn.Model.Should().Be(daemon.ResolveCourierModel(null));
        spawn.Prompt.Should().Contain(task.Objective).And.Contain(task.AcceptanceCriteria[0]);

        await using IQuerySession query = postgres.Store.QuerySession();
        TaskDetails reread = (await query.LoadAsync<TaskDetails>(task.Id, cts.Token))!;
        reread.LessonPick.Should().NotBeNull();
        reread.LessonPick!.PickedLessonIds.Should().Equal(recorded.PickedLessonIds);

        IReadOnlyList<IEvent> runEvents = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        TokensRecorded tokens = runEvents.Select(@event => @event.Data).OfType<TokensRecorded>().Should().ContainSingle().Subject;
        tokens.Model.Should().Be(recorded.Model, "the session's tokens land in the run's totals under the model it ran on");
        tokens.InputTokens.Should().Be(10);
        tokens.OutputTokens.Should().Be(20);
    }

    [Fact]
    public async Task A_task_override_of_model_and_effort_does_not_move_the_picker()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid runId, TaskDetails task) = await SeedAsync(cts.Token);
        task.Model = AgentModel.Opus;
        task.Effort = AgentEffort.High;
        DaemonOptions daemon = new();
        ScriptedExecutor executor = new("aaaaaaaa");

        await NewPicker(executor, daemon).PickAsync(
            runId, _runDirectory, task, Project(), Candidates(Lessons(6), new LessonInjectionCaps(2, 10_000), LessonPickerMode.Shadow),
            ExecutorMode.Subscription, cts.Token);

        AgentSpawnRequest spawn = executor.Spawns.Should().ContainSingle().Subject;
        spawn.Model.Should().Be(daemon.ResolveCourierModel(null));
        spawn.Effort.Should().Be(daemon.ResolveEffort(AgentRole.Courier, null, null));
    }

    [Theory]
    [InlineData("session-error")]
    [InlineData("no-result")]
    [InlineData("timed-out")]
    [InlineData("unusable-answer")]
    [InlineData("empty-pick")]
    [InlineData("start-failed")]
    public async Task Every_way_a_pick_can_fail_is_recorded_and_the_prompt_falls_back_to_newest_first(string expected)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid runId, TaskDetails task) = await SeedAsync(cts.Token);
        List<LearningDetails> lessons = Lessons(6);
        LessonCandidates candidates = Candidates(lessons, new LessonInjectionCaps(3, 10_000), LessonPickerMode.On);
        FakeProcessManager processes = new();
        // Only the hung session needs the bound to be short; a session that dies with nothing written
        // is finalized after the waiter's own grace period, which a 300 ms bound would cut off.
        DaemonOptions daemon = new()
        {
            LessonPickerTimeout = expected == "timed-out" ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromMinutes(2),
        };
        IExecutor executor = expected switch
        {
            "session-error" => new ScriptedExecutor("an error", isError: true),
            "no-result" => new ScriptedExecutor(null),
            "timed-out" => new SilentExecutor(processes),
            "unusable-answer" => new ScriptedExecutor("I would pick the projection lessons because they matter."),
            "empty-pick" => new ScriptedExecutor("00000000 11111111"),
            _ => new ThrowingExecutor(),
        };

        LessonPickRecorded? recorded = await NewPicker(executor, daemon, processes).PickAsync(
            runId, _runDirectory, task, Project(), candidates, ExecutorMode.Subscription, cts.Token);

        recorded!.Outcome.Value.Should().Be(expected);
        recorded.PickedLessonIds.Should().BeEmpty();
        recorded.NewestFirstLessonIds.Should().HaveCount(3);
        candidates.For(recorded).Should().BeEquivalentTo(candidates.NewestFirst,
            "the build dispatches on today's selection and the record says why");
        if (expected == "timed-out")
        {
            processes.Terminations.Should().NotBeEmpty("a session past its bound is terminated");
        }

        await using IQuerySession query = postgres.Store.QuerySession();
        (await query.LoadAsync<TaskDetails>(task.Id, cts.Token))!.LessonPick!.Outcome.Value.Should().Be(expected);
    }

    [Fact]
    public async Task A_task_keeps_one_pick_however_many_times_the_picker_is_asked()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid runId, TaskDetails task) = await SeedAsync(cts.Token);
        LessonCandidates candidates = Candidates(Lessons(6), new LessonInjectionCaps(2, 10_000), LessonPickerMode.Shadow);
        ScriptedExecutor first = new("aaaaaaaa");
        ScriptedExecutor second = new("bbbbbbbb");

        LessonPickRecorded? one = await NewPicker(first).PickAsync(
            runId, _runDirectory, task, Project(), candidates, ExecutorMode.Subscription, cts.Token);

        // The second caller holds the task as it was before the first pick landed: the race the
        // writer's own re-read exists for.
        LessonPickRecorded? two = await NewPicker(second).PickAsync(
            runId, _runDirectory, task, Project(), candidates, ExecutorMode.Subscription, cts.Token);

        two.Should().BeEquivalentTo(one);
        await using IQuerySession query = postgres.Store.QuerySession();
        IReadOnlyList<IEvent> taskEvents = await query.Events.FetchStreamAsync(task.Id, token: cts.Token);
        taskEvents.Select(@event => @event.Data).OfType<LessonPickRecorded>().Should().ContainSingle();
    }

    [Fact]
    public async Task A_task_that_already_has_a_pick_spawns_no_picker_session()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid runId, TaskDetails task) = await SeedAsync(cts.Token);
        task.LessonPick = new LessonPickRecorded(
            task.Id, LessonPickerMode.Shadow, LessonPickOutcome.TimedOut, [], [], 6, 0, AgentModel.Sonnet, TimeSpan.Zero, Now);
        ScriptedExecutor executor = new("aaaaaaaa");

        LessonPickRecorded? recorded = await NewPicker(executor).PickAsync(
            runId, _runDirectory, task, Project(),
            Candidates(Lessons(6), new LessonInjectionCaps(2, 10_000), LessonPickerMode.On), ExecutorMode.Subscription, cts.Token);

        recorded.Should().BeNull();
        executor.Spawns.Should().BeEmpty();
    }

    [Fact]
    public async Task With_the_setting_off_or_nothing_for_the_caps_to_hold_back_no_session_runs_and_nothing_is_recorded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid runId, TaskDetails task) = await SeedAsync(cts.Token);
        ScriptedExecutor executor = new("aaaaaaaa");
        LessonPicker picker = NewPicker(executor);

        LessonPickRecorded? off = await picker.PickAsync(
            runId, _runDirectory, task, Project(),
            Candidates(Lessons(6), new LessonInjectionCaps(2, 10_000), LessonPickerMode.Off), ExecutorMode.Subscription, cts.Token);
        LessonPickRecorded? fits = await picker.PickAsync(
            runId, _runDirectory, task, Project(),
            Candidates(Lessons(2), new LessonInjectionCaps(15, 10_000), LessonPickerMode.On), ExecutorMode.Subscription, cts.Token);

        off.Should().BeNull();
        fits.Should().BeNull();
        executor.Spawns.Should().BeEmpty();
        await using IQuerySession query = postgres.Store.QuerySession();
        IReadOnlyList<IEvent> taskEvents = await query.Events.FetchStreamAsync(task.Id, token: cts.Token);
        taskEvents.Select(@event => @event.Data).OfType<LessonPickRecorded>().Should().BeEmpty();
    }

    [Theory]
    [InlineData(TaskTypeKind.Spike)]
    [InlineData(TaskTypeKind.PrReview)]
    [InlineData(TaskTypeKind.Chore)]
    public void Only_a_task_that_builds_is_ever_offered_a_pick(TaskTypeKind kind)
    {
        TaskType type = kind switch
        {
            TaskTypeKind.Spike => TaskType.Spike,
            TaskTypeKind.PrReview => TaskType.PrReview,
            _ => TaskType.Chore,
        };
        TaskDetails task = new() { Id = DomainId.New(), Type = type };
        LessonCandidates candidates = Candidates(Lessons(6), new LessonInjectionCaps(2, 10_000), LessonPickerMode.On);

        LessonPicker.ShouldPick(task, candidates).Should().Be(kind == TaskTypeKind.Chore);
    }

    /// <summary>The theory's own vocabulary, since a theory cannot carry a <see cref="TaskType"/> record instance.</summary>
    public enum TaskTypeKind
    {
        Spike,
        PrReview,
        Chore,
    }

    private LessonPicker NewPicker(IExecutor executor, DaemonOptions? daemon = null, FakeProcessManager? processes = null) =>
        new(postgres.Store, executor, processes ?? new FakeProcessManager(), Options.Create(daemon ?? new DaemonOptions()),
            NullLogger<LessonPicker>.Instance);

    private static string Short(LearningDetails lesson) => DomainId.Short(lesson.Id);

    private static ProjectDetails Project() => new() { Id = ProjectId, Name = "hall9k", BaseBranch = "main", Model = AgentModel.Unknown };

    private static List<LearningDetails> Lessons(int count) =>
        [.. Enumerable.Range(0, count).Select(index => new LearningDetails
        {
            Id = DomainId.New(),
            Scope = KnowledgeScope.Project,
            ScopeId = ProjectId,
            Statement = $"Claim number {index}",
            Provenance = RecordedProvenance.FromShell(Owner),
            RecordedAt = Now.AddMinutes(-index),
            Status = LearningStatus.Active,
        })];

    private static LessonCandidates Candidates(IReadOnlyList<LearningDetails> lessons, LessonInjectionCaps caps, LessonPickerMode mode) =>
        LessonInjection.Eligible(lessons, [], ThisNode, caps, pickerMode: mode);

    private async Task<(Guid RunId, TaskDetails Task)> SeedAsync(CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
                TaskDecider.Add(
                    taskId, ProjectId, "Change the Marten projection for task details", ["The projection keeps the first pick"],
                    TaskType.Chore, "Context for the picker.", null, null, Now, Owner),
                Owner, Now));
            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, ThisNode, Owner, 1, DomainId.New(), "/tmp/worktree", "task/picker",
                ExecutorMode.Subscription, Now));
            await session.SaveChangesAsync(cancellationToken);
        }

        await using IQuerySession query = postgres.Store.QuerySession();
        return (runId, (await query.LoadAsync<TaskDetails>(taskId, cancellationToken))!);
    }

    private sealed class ScriptedExecutor(string? summary, bool isError = false) : IExecutor
    {
        private int _nextPid = 9000;

        public List<AgentSpawnRequest> Spawns { get; } = [];

        public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            Spawns.Add(request);
            int pid = _nextPid++;
            if (summary is null)
            {
                return new SpawnedAgent(pid, Now);
            }

            string streamFile = RunPaths.SessionStreamFile(request.RunDirectory, request.SessionArtifactName!);
            Directory.CreateDirectory(request.RunDirectory);
            string line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "result",
                ["subtype"] = isError ? "error_during_execution" : "success",
                ["is_error"] = isError,
                ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 10, ["output_tokens"] = 20 },
                ["result"] = summary,
            });
            await File.WriteAllTextAsync(streamFile, line + "\n", cancellationToken);
            return new SpawnedAgent(pid, Now);
        }
    }

    private sealed class SilentExecutor(FakeProcessManager processes) : IExecutor
    {
        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            processes.MarkAlive(9100);
            return Task.FromResult(new SpawnedAgent(9100, Now));
        }
    }

    private sealed class ThrowingExecutor : IExecutor
    {
        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("claude could not be started");
    }
}
