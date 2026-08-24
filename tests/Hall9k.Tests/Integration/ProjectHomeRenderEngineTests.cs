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
    public async Task Reassigning_an_idea_to_a_project_with_its_own_home_does_not_create_a_decoy_workspace()
    {
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid originalProjectId = DomainId.New();
        Guid otherProjectId = DomainId.New();
        string otherHome = Directory.CreateTempSubdirectory("hall9k-render-engine-other-home-").FullName;
        Guid ideaId = DomainId.New();

        try
        {
            await using (IDocumentSession session = store.LightweightSession())
            {
                RegisterProject(session, originalProjectId, ownerId, "original");
                ProjectRegistered otherRegistered = ProjectDecider.Register(
                    otherProjectId, ownerId, DomainId.New(), "other", "/tmp/other", null, "main", Now,
                    ProjectHome.Parse(otherHome));
                session.Events.StartStream<ProjectAggregate>(otherProjectId, otherRegistered);

                // Captured while bound to the original project, whose home is already
                // materialised — the workspace decision (backlog 49) is made here and never
                // moves, even once the idea is reassigned below.
                ProjectHome workspaceHome = ProjectHome.Parse(_home);
                IdeaCaptured captured = IdeaDecider.Capture(
                    ideaId, ownerId, "Idea that moves projects", originalProjectId, Now, workspaceHome);
                session.Events.StartStream<IdeaAggregate>(ideaId, captured);
                await session.SaveChangesAsync();
            }

            await NewEngine(store).PollOnceAsync(CancellationToken.None);

            await using (IDocumentSession session = store.LightweightSession())
            {
                IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId)
                    ?? throw new InvalidOperationException("idea not found");
                IdeaAssignedToProject assigned = IdeaDecider.AssignToProject(idea, otherProjectId, Now, ownerId);
                session.Events.Append(ideaId, assigned);
                await session.SaveChangesAsync();
            }

            await NewEngine(store).PollOnceAsync(CancellationToken.None);

            string otherIdeasRoot = ProjectHomePaths.IdeasDirectory(otherHome);
            string ideaDirectory = Directory.EnumerateDirectories(otherIdeasRoot).Should().ContainSingle().Subject;
            Directory.Exists(Path.Combine(ideaDirectory, "workspace")).Should().BeFalse(
                "the idea's real workspace stays at its capture-time home; the project it moved to must not get a decoy");
        }
        finally
        {
            Directory.Delete(otherHome, recursive: true);
        }
    }

    [Fact]
    public async Task A_slug_changing_revise_before_the_first_sweep_still_finds_the_directory_capture_created()
    {
        // Mirrors what h9k idea add actually does (IdeaAddCommand): it creates the idea's
        // home-resident directory and workspace itself, synchronously, because no doorbell
        // ever wakes the render sweep for an idea. That directory has to carry the identity
        // marker from the moment it is created — if a slug-changing revise lands before the
        // first sweep ever runs, the sweep's HomeEntryLookup.FindExisting match requires the
        // marker to recognise this as the same directory rather than building a fresh, empty
        // decoy at the new name and stranding this one (adversarial review, backlog 49 cycle 3).
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid ideaId = DomainId.New();
        IdeaCaptured captured;

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            ProjectHome workspaceHome = ProjectHome.Parse(_home);
            captured = IdeaDecider.Capture(ideaId, ownerId, "Original idea text", projectId, Now, workspaceHome);
            session.Events.StartStream<IdeaAggregate>(ideaId, captured);
            await session.SaveChangesAsync();
        }

        string ideaDirectory = IdeaPaths.ResolveDirectory(
            ProjectHome.Parse(_home), ProjectHomePaths.EntryDirectoryName(ideaId, captured.Text), ideaId);
        string workspace = IdeaPaths.EnsureWorkspace(ideaDirectory);
        HomeEntryLookup.EnsureIdentityMarker(ideaDirectory, ideaId);
        File.WriteAllText(Path.Combine(workspace, "notes.md"), "keep me");

        await using (IDocumentSession session = store.LightweightSession())
        {
            IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId)
                ?? throw new InvalidOperationException("idea not found");
            IdeaRevised revised = IdeaDecider.Revise(idea, "Renamed idea text", Now, ownerId);
            session.Events.Append(ideaId, revised);
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);

        string ideasRoot = ProjectHomePaths.IdeasDirectory(_home);
        Directory.Exists(ideaDirectory).Should().BeFalse("the stale slug's directory must not survive the rename");
        string renamedDirectory = Directory.EnumerateDirectories(ideasRoot).Should().ContainSingle().Subject;
        Path.GetFileName(renamedDirectory).Should().Contain("renamed-idea-text");
        File.ReadAllText(Path.Combine(renamedDirectory, "workspace", "notes.md")).Should().Be("keep me",
            "capture's own workspace file must survive the move rather than being orphaned behind a decoy");
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
