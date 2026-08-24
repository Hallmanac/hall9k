using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The daemon half of backlog 48: task.md and idea.md are a pure function of the store's current
/// state, rewritten by a sweep rather than a per-event handler (the same shape as
/// <c>CardPublicationEngineTests</c> proves for publication). What matters here is the seam
/// between the store and the filesystem — a revision renaming the directory, a home that has not
/// been materialised yet being skipped rather than half-written into, and a stray directory being
/// reconciled — since <c>TaskDocumentRendererTests</c> and <c>HomeEntryWriterTests</c> already
/// cover the rendering and the filesystem mechanics in isolation.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ProjectHomeRenderEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly string _home = Directory.CreateTempSubdirectory("hall9k-render-engine-home-").FullName;

    [Fact]
    public async Task A_sweep_renders_every_task_and_idea_in_a_materialised_home()
    {
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            AddTask(session, projectId, ownerId, "Tasks and ideas render as markdown files");
            CaptureIdea(session, projectId, ownerId, "Project directory and tracker mirroring");
            await session.SaveChangesAsync();
        }

        ProjectHomeRenderSweepResult sweep = await NewEngine(store).PollOnceAsync(CancellationToken.None);

        sweep.ProjectsInspected.Should().Be(1);
        sweep.TasksRendered.Should().Be(1);
        sweep.IdeasRendered.Should().Be(1);

        string tasksRoot = ProjectHomePaths.TasksDirectory(_home);
        string taskDirectory = Directory.EnumerateDirectories(tasksRoot).Should().ContainSingle().Subject;
        Path.GetFileName(taskDirectory).Should().Contain("tasks-and-ideas-render-as-markdown-files");
        File.ReadAllText(Path.Combine(taskDirectory, "task.md")).Should().Contain("state: Draft");
        Directory.Exists(Path.Combine(taskDirectory, "workspace")).Should().BeTrue();

        string ideasRoot = ProjectHomePaths.IdeasDirectory(_home);
        string ideaDirectory = Directory.EnumerateDirectories(ideasRoot).Should().ContainSingle().Subject;
        Path.GetFileName(ideaDirectory).Should().Contain("project-directory-and-tracker-mirroring");
        File.ReadAllText(Path.Combine(ideaDirectory, "idea.md")).Should().Contain("Project directory and tracker mirroring");
    }

    [Fact]
    public async Task Revising_the_objective_moves_the_directory_instead_of_leaving_a_stale_copy()
    {
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Original objective", ["criterion"], TaskType.Feature, null,
                null, null, Now, ownerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);
        string tasksRoot = ProjectHomePaths.TasksDirectory(_home);
        string originalDirectory = Directory.EnumerateDirectories(tasksRoot).Should().ContainSingle().Subject;

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId)
                ?? throw new InvalidOperationException("task not found");
            TaskRevised revised = TaskDecider.Revise(
                task, Optional<string>.Of("Renamed objective"), Optional<IReadOnlyList<string>>.None,
                Optional<string>.None, Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None,
                Optional<AgentModel>.None, Now, ownerId);
            session.Events.Append(taskId, revised);
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);

        Directory.Exists(originalDirectory).Should().BeFalse("the stale slug must not survive the rename");
        string renamedDirectory = Directory.EnumerateDirectories(tasksRoot).Should().ContainSingle().Subject;
        Path.GetFileName(renamedDirectory).Should().Contain("renamed-objective");
        File.ReadAllText(Path.Combine(renamedDirectory, "task.md")).Should().Contain("objective: Renamed objective");
    }

    [Fact]
    public async Task A_project_whose_home_is_not_materialised_on_this_machine_is_skipped_not_half_written()
    {
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        string unmaterialisedHome = Path.Combine(Path.GetTempPath(), $"hall9k-never-created-{Guid.NewGuid():N}");

        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, ownerId, DomainId.New(), "elsewhere", "/tmp/elsewhere", null, "main", Now,
                ProjectHome.Parse(unmaterialisedHome));
            session.Events.StartStream<ProjectAggregate>(projectId, registered);
            AddTask(session, projectId, ownerId, "Some task");
            await session.SaveChangesAsync();
        }

        ProjectHomeRenderSweepResult sweep = await NewEngine(store).PollOnceAsync(CancellationToken.None);

        sweep.ProjectsInspected.Should().Be(0);
        sweep.TasksRendered.Should().Be(0);
        Directory.Exists(unmaterialisedHome).Should().BeFalse(
            "a recorded home that was never materialised here must not be created by the render sweep");
    }

    [Fact]
    public async Task A_stray_directory_matching_no_task_or_idea_is_reconciled_away_on_the_next_sweep()
    {
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            await session.SaveChangesAsync();
        }

        string strayDirectory = Path.Combine(ProjectHomePaths.TasksDirectory(_home), "deadbeef-leftover-from-somewhere");
        Directory.CreateDirectory(strayDirectory);
        File.WriteAllText(Path.Combine(strayDirectory, "task.md"), "stale generated content");

        ProjectHomeRenderSweepResult sweep = await NewEngine(store).PollOnceAsync(CancellationToken.None);

        sweep.OrphansHandled.Should().Be(1);
        Directory.Exists(strayDirectory).Should().BeFalse("an empty shell orphan is removed, not left behind");
    }

    private ProjectHomeRenderEngine NewEngine(IDocumentStore store) =>
        new(store, NullLogger<ProjectHomeRenderEngine>.Instance);

    private void RegisterProject(IDocumentSession session, Guid projectId, Guid ownerId, string name)
    {
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, ownerId, DomainId.New(), name, "/tmp/repo", null, "main", Now,
            ProjectHome.Parse(_home));
        session.Events.StartStream<ProjectAggregate>(projectId, registered);
        Directory.CreateDirectory(_home);
    }

    private static void AddTask(IDocumentSession session, Guid projectId, Guid ownerId, string objective)
    {
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, objective, ["criterion"], TaskType.Feature, "Some agent context.",
            null, null, Now, ownerId);
        session.Events.StartStream<TaskAggregate>(taskId, added);
    }

    private void CaptureIdea(IDocumentSession session, Guid projectId, Guid ownerId, string text)
    {
        Guid ideaId = DomainId.New();
        // Mirrors what h9k idea add actually checks: the project's home already materialised
        // on this machine at capture time (RegisterProject creates _home before this runs).
        ProjectHome workspaceHome = Directory.Exists(_home) ? ProjectHome.Parse(_home) : ProjectHome.None;
        IdeaCaptured captured = IdeaDecider.Capture(ideaId, ownerId, text, projectId, Now, workspaceHome);
        session.Events.StartStream<IdeaAggregate>(ideaId, captured);
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }
}
