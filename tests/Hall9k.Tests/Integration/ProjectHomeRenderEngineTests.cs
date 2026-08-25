using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
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

            string originalIdeasRoot = ProjectHomePaths.IdeasDirectory(_home);
            string originalIdeaDirectory = Directory.EnumerateDirectories(originalIdeasRoot).Should().ContainSingle().Subject;
            string originalWorkspace = Path.Combine(originalIdeaDirectory, "workspace");
            File.WriteAllText(Path.Combine(originalWorkspace, "notes.md"), "real research, keep me");

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

            // The other half of the same invariant (adversarial review, backlog 48 cycle 4): the
            // idea's ORIGINAL project no longer owns it, so the sweep that just ran no longer
            // renders idea.md there — but the directory it already rendered, carrying the idea's
            // one true workspace, must survive the same sweep's orphan reconciliation rather than
            // being mistaken for a stray no task or idea claims any more.
            Directory.Exists(originalIdeaDirectory).Should().BeTrue(
                "reassignment must not orphan the idea's permanent, capture-time home directory");
            File.Exists(Path.Combine(originalWorkspace, "notes.md")).Should().BeTrue(
                "real research dropped in the idea's one true workspace must never be swept away by a reassignment");
            File.Exists(Path.Combine(originalIdeaDirectory, "ORPHANED.md")).Should().BeFalse(
                "the directory is still the idea's real home, not an orphan, so it must not be marked as one");
        }
        finally
        {
            Directory.Delete(otherHome, recursive: true);
        }
    }

    [Fact]
    public async Task Re_recording_a_projects_home_with_different_case_does_not_orphan_an_anchored_idea()
    {
        // Adversarial review, cycle 6: idea.WorkspaceHome == project.HomeDirectory used ProjectHome's
        // raw record equality (ordinal string comparison) instead of ProjectHomePaths.SameDirectory,
        // the one helper this codebase built for "do these two recorded paths name the same
        // directory". `h9k project init` lets a project's HomeDirectory be re-recorded at any time
        // (ProjectSettingsChanged), and nothing normalises case, so the same physical directory
        // retyped differently rewrites the recorded string. An idea anchored there under a project it
        // has since moved away from must still be recognised as "known" by the raw equality's
        // case-insensitive-filesystem replacement, or its real workspace gets deleted or marked
        // ORPHANED.md by the very next sweep.
        if (OperatingSystem.IsLinux())
        {
            // SameDirectory is deliberately ordinal on Linux, which does not fold case by default —
            // a recased path there names a genuinely different directory, so this scenario cannot
            // arise on that platform.
            return;
        }

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

                ProjectHome workspaceHome = ProjectHome.Parse(_home);
                IdeaCaptured captured = IdeaDecider.Capture(
                    ideaId, ownerId, "Idea anchored under a home later re-recorded with different case",
                    originalProjectId, Now, workspaceHome);
                session.Events.StartStream<IdeaAggregate>(ideaId, captured);
                await session.SaveChangesAsync();
            }

            await NewEngine(store).PollOnceAsync(CancellationToken.None);

            string originalIdeasRoot = ProjectHomePaths.IdeasDirectory(_home);
            string originalIdeaDirectory = Directory.EnumerateDirectories(originalIdeasRoot).Should().ContainSingle().Subject;
            string originalWorkspace = Path.Combine(originalIdeaDirectory, "workspace");
            File.WriteAllText(Path.Combine(originalWorkspace, "notes.md"), "real research, keep me");

            await using (IDocumentSession session = store.LightweightSession())
            {
                IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId)
                    ?? throw new InvalidOperationException("idea not found");
                IdeaAssignedToProject assigned = IdeaDecider.AssignToProject(idea, otherProjectId, Now, ownerId);
                session.Events.Append(ideaId, assigned);

                // The same physical directory as _home, retyped with different case — what
                // "h9k project init" re-run against the same path in a differently-cased shell
                // invocation would record. Nothing moves on disk.
                ProjectAggregate original = await session.Events.AggregateStreamAsync<ProjectAggregate>(originalProjectId)
                    ?? throw new InvalidOperationException("project not found");
                ProjectSettingsChanged recased = ProjectDecider.ChangeSettings(
                    original, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
                    Optional<int>.None, Optional<IReadOnlyList<ContextLink>>.None, Now, ownerId,
                    homeDirectory: ProjectHome.Parse(Recase(_home)));
                session.Events.Append(originalProjectId, recased);

                await session.SaveChangesAsync();
            }

            await NewEngine(store).PollOnceAsync(CancellationToken.None);

            Directory.Exists(originalIdeaDirectory).Should().BeTrue(
                "a home re-recorded with different case must not orphan the idea's permanent, capture-time home directory");
            File.Exists(Path.Combine(originalWorkspace, "notes.md")).Should().BeTrue(
                "real research in the idea's one true workspace must survive a case-only re-recording of its home");
            File.Exists(Path.Combine(originalIdeaDirectory, "ORPHANED.md")).Should().BeFalse(
                "the directory is still the idea's real home, not an orphan, so it must not be marked as one");
        }
        finally
        {
            Directory.Delete(otherHome, recursive: true);
        }
    }

    private static string Recase(string path) =>
        string.Concat(path.Select((c, i) => i % 2 == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)));

    [Fact]
    public async Task Revising_an_idea_after_reassignment_does_not_orphan_its_anchored_workspace()
    {
        // The other half of Reassigning_an_idea_to_a_project_with_its_own_home_does_not_create_a_decoy_workspace
        // (adversarial review, backlog 49 cycle 5): once reassigned, nothing under the ORIGINAL
        // project ever renders idea.md again, so nothing renames its directory there. A later
        // revise still changes the idea's text and therefore the slug ReconcileOrphans would
        // recompute for it — that recomputed name must not be trusted as "the" on-disk name, or
        // the sweep looks for a directory that was never created and reads the real one, still
        // sitting at its original slug, as an orphan.
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

                ProjectHome workspaceHome = ProjectHome.Parse(_home);
                IdeaCaptured captured = IdeaDecider.Capture(
                    ideaId, ownerId, "Idea that moves projects", originalProjectId, Now, workspaceHome);
                session.Events.StartStream<IdeaAggregate>(ideaId, captured);
                await session.SaveChangesAsync();
            }

            await NewEngine(store).PollOnceAsync(CancellationToken.None);

            string originalIdeasRoot = ProjectHomePaths.IdeasDirectory(_home);
            string originalIdeaDirectory = Directory.EnumerateDirectories(originalIdeasRoot).Should().ContainSingle().Subject;
            string originalWorkspace = Path.Combine(originalIdeaDirectory, "workspace");
            File.WriteAllText(Path.Combine(originalWorkspace, "notes.md"), "real research, keep me");

            await using (IDocumentSession session = store.LightweightSession())
            {
                IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId)
                    ?? throw new InvalidOperationException("idea not found");
                IdeaAssignedToProject assigned = IdeaDecider.AssignToProject(idea, otherProjectId, Now, ownerId);
                session.Events.Append(ideaId, assigned);
                await session.SaveChangesAsync();
            }

            await NewEngine(store).PollOnceAsync(CancellationToken.None);

            // Revise the idea's text after it has already moved projects — its slug changes, but
            // its anchored directory under the ORIGINAL home is never rendered (and so never
            // renamed) again.
            await using (IDocumentSession session = store.LightweightSession())
            {
                IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId)
                    ?? throw new InvalidOperationException("idea not found");
                IdeaRevised revised = IdeaDecider.Revise(idea, "Renamed after the move", Now, ownerId);
                session.Events.Append(ideaId, revised);
                await session.SaveChangesAsync();
            }

            await NewEngine(store).PollOnceAsync(CancellationToken.None);

            Directory.Exists(originalIdeaDirectory).Should().BeTrue(
                "a revise after reassignment must not orphan the idea's permanent, capture-time home directory");
            File.Exists(Path.Combine(originalWorkspace, "notes.md")).Should().BeTrue(
                "real research in the idea's one true workspace must survive a post-reassignment revise");
            File.Exists(Path.Combine(originalIdeaDirectory, "ORPHANED.md")).Should().BeFalse(
                "the directory is still the idea's real home, not an orphan, so it must not be marked as one");
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
    public async Task A_task_that_reaches_true_closeout_moves_into_the_archive_directory()
    {
        // True closeout, not raw Done (backlog 51): TaskCompleted fires the moment the pull
        // request opens, and only RunCompleted — appended once the closeout monitor observes
        // the merge — means the story is actually over. That is the same bar the dependency
        // rule (TaskDependencyQuery.IsClosedOut) already uses.
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();
        const string PullRequestUrl = "https://github.com/example/hall9k/pull/1";

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Task that closes out", ["criterion"], TaskType.Feature, null,
                    null, null, Now, ownerId),
                ownerId, Now);
            List<object> taskEvents = [.. lifecycle];

            TaskClaimed claimed = TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), Now);
            task.Apply(claimed);
            taskEvents.Add(claimed);

            TaskCompleted completed = TaskDecider.Complete(task, task.CurrentRunId!.Value, PullRequestUrl, Now);
            task.Apply(completed);
            taskEvents.Add(completed);

            Guid runId = task.CurrentRunId!.Value;
            session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
            session.Events.StartStream<RunAggregate>(runId,
                new RunDispatched(
                    runId, taskId, nodeId, ownerId, task.LeaseGeneration, DomainId.New(), "/tmp/worktree",
                    "task/closes-out", ExecutorMode.Subscription, Now),
                new RunCompleted(runId, Now));

            await session.SaveChangesAsync();
        }

        ProjectHomeRenderSweepResult sweep = await NewEngine(store).PollOnceAsync(CancellationToken.None);

        string tasksRoot = ProjectHomePaths.TasksDirectory(_home);
        string archiveRoot = ProjectHomePaths.ArchivedTasksDirectory(_home);
        LiveTaskDirectories(tasksRoot).Should().BeEmpty(
            "a task that has reached true closeout must not remain at the top level");
        string archivedDirectory = Directory.EnumerateDirectories(archiveRoot).Should().ContainSingle().Subject;
        Path.GetFileName(archivedDirectory).Should().Contain("task-that-closes-out");
        File.ReadAllText(Path.Combine(archivedDirectory, "task.md")).Should().Contain("state: Done");
        Directory.Exists(Path.Combine(archivedDirectory, "workspace")).Should().BeTrue();
        sweep.TasksRendered.Should().Be(1);
    }

    [Fact]
    public async Task A_done_task_whose_pull_request_is_still_open_stays_at_the_top_level()
    {
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Task with an open pull request", ["criterion"], TaskType.Feature, null,
                    null, null, Now, ownerId),
                ownerId, Now);
            List<object> taskEvents = [.. lifecycle];

            TaskClaimed claimed = TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), Now);
            task.Apply(claimed);
            taskEvents.Add(claimed);

            // No RunCompleted at all here: the run that carried this task is still out there,
            // under review — Done alone (TaskCompleted) is not true closeout.
            TaskCompleted completed = TaskDecider.Complete(
                task, task.CurrentRunId!.Value, "https://github.com/example/hall9k/pull/2", Now);
            task.Apply(completed);
            taskEvents.Add(completed);

            session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);

        string tasksRoot = ProjectHomePaths.TasksDirectory(_home);
        string archiveRoot = ProjectHomePaths.ArchivedTasksDirectory(_home);
        string taskDirectory = LiveTaskDirectories(tasksRoot).Should()
            .ContainSingle("a Done task whose pull request is still open has not reached true closeout")
            .Subject;
        File.ReadAllText(Path.Combine(taskDirectory, "task.md")).Should().Contain("state: Done");
        Directory.Exists(archiveRoot).Should().BeFalse(
            "nothing has ever archived here, so the render sweep must never have created the archive root");
    }

    [Fact]
    public async Task A_hand_resolved_task_archives_even_though_its_current_run_ended_failed()
    {
        // Adversarial review, backlog 51 cycle 2: h9k task resolve is the attestation exit from
        // Failed (Decisions Log #27) — it ends the task Done without ever touching CurrentRunId
        // (TaskAggregate.Apply(TaskResolved), unlike TaskRetried, leaves it exactly as it was), so
        // the current run stays Failed forever. This exercises the ResolvedReason attestation
        // branch specifically — no run of this task ever reaches RunCompleted, so archiving here
        // depends entirely on the attestation, not on the "any run" broadening below.
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Task resolved by hand after its run died", ["criterion"], TaskType.Feature,
                    null, null, null, Now, ownerId),
                ownerId, Now);
            List<object> taskEvents = [.. lifecycle];

            TaskClaimed claimed = TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), Now);
            task.Apply(claimed);
            taskEvents.Add(claimed);
            Guid runId = task.CurrentRunId!.Value;

            TaskFailed failed = TaskDecider.Fail(task, runId, "agent crashed", Now);
            task.Apply(failed);
            taskEvents.Add(failed);

            TaskResolved resolved = TaskDecider.Resolve(
                task, "merged by hand", "https://github.com/example/hall9k/pull/9", Now, ownerId);
            task.Apply(resolved);
            taskEvents.Add(resolved);

            session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
            session.Events.StartStream<RunAggregate>(runId,
                new RunDispatched(
                    runId, taskId, nodeId, ownerId, task.LeaseGeneration, DomainId.New(), "/tmp/worktree",
                    "task/resolved-by-hand", ExecutorMode.Subscription, Now),
                new RunFailed(runId, "agent crashed", Now));
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);

        string tasksRoot = ProjectHomePaths.TasksDirectory(_home);
        string archiveRoot = ProjectHomePaths.ArchivedTasksDirectory(_home);
        LiveTaskDirectories(tasksRoot).Should().BeEmpty(
            "a hand-resolved task is terminal by attestation and must not remain at the top level");
        string archivedDirectory = Directory.EnumerateDirectories(archiveRoot).Should().ContainSingle().Subject;
        File.ReadAllText(Path.Combine(archivedDirectory, "task.md")).Should().Contain("state: Done");
    }

    [Fact]
    public async Task A_task_closed_out_again_archives_on_an_earlier_runs_completion_even_though_its_current_run_has_no_projection_yet()
    {
        // Adversarial review, backlog 51 cycle 3: the hand-resolved test above passes through
        // TaskDetails.ResolvedReason alone and never actually exercises the "any of the task's
        // runs, not only the current one" broadening the Done branch also relies on — narrowing
        // IsArchived's Done check back to a current-run-only test leaves that test green while
        // silently breaking this rule. A follow-up run whose own RunDispatched has not landed
        // yet (a crash between the claim and the dispatch, or the render sweep simply polling in
        // that window) leaves CurrentRunId naming a run with no projection at all; only the
        // first run's own RunCompleted proves true closeout here, and ResolvedReason is never
        // set on this task at all.
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();
        const string PullRequestUrl = "https://github.com/example/hall9k/pull/11";

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Task closed out again while its follow-up run is still undispatched",
                    ["criterion"], TaskType.Feature, null, null, null, Now, ownerId),
                ownerId, Now);
            List<object> taskEvents = [.. lifecycle];

            TaskClaimed firstClaim = TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), Now);
            task.Apply(firstClaim);
            taskEvents.Add(firstClaim);
            Guid firstRunId = task.CurrentRunId!.Value;
            int firstGeneration = task.LeaseGeneration;

            TaskCompleted firstCompleted = TaskDecider.Complete(task, firstRunId, PullRequestUrl, Now);
            task.Apply(firstCompleted);
            taskEvents.Add(firstCompleted);

            TaskReopened reopened = TaskDecider.Reopen(
                task, firstRunId, "task/closed-out-twice", "one more look", FollowUpKind.ReviewFeedback,
                automatic: false, Now, ownerId);
            task.Apply(reopened);
            taskEvents.Add(reopened);

            TaskClaimed secondClaim = TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), Now);
            task.Apply(secondClaim);
            taskEvents.Add(secondClaim);
            Guid secondRunId = task.CurrentRunId!.Value;

            TaskCompleted secondCompleted = TaskDecider.Complete(task, secondRunId, PullRequestUrl, Now);
            task.Apply(secondCompleted);
            taskEvents.Add(secondCompleted);

            session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
            // Only the FIRST run ever gets a projection; the second (current) run's own
            // RunDispatched never lands — the exact gap the "any run" rule exists to cover.
            session.Events.StartStream<RunAggregate>(firstRunId,
                new RunDispatched(
                    firstRunId, taskId, nodeId, ownerId, firstGeneration, DomainId.New(), "/tmp/worktree",
                    "task/closed-out-twice", ExecutorMode.Subscription, Now),
                new RunCompleted(firstRunId, Now));
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);

        string tasksRoot = ProjectHomePaths.TasksDirectory(_home);
        string archiveRoot = ProjectHomePaths.ArchivedTasksDirectory(_home);
        LiveTaskDirectories(tasksRoot).Should().BeEmpty(
            "an earlier run of this task already reached true closeout, so the task archives even " +
            "though its current run has no projection to check yet");
        string archivedDirectory = Directory.EnumerateDirectories(archiveRoot).Should().ContainSingle().Subject;
        File.ReadAllText(Path.Combine(archivedDirectory, "task.md")).Should().Contain("state: Done");
    }

    [Fact]
    public async Task A_task_still_dispatched_into_the_archive_directory_by_a_reopen_is_not_moved_out_from_under_it()
    {
        // Adversarial review, backlog 51 cycle 2: RunLauncher dispatches a reopened task's
        // follow-up run straight into tasks/_archive/ when the render sweep has not yet moved
        // the directory back out (its own alternate-root search finds the task still archived).
        // The task's own state already reads non-terminal at that point, so unless the sweep
        // recognises the current run as still live, it moves the directory back to tasks/ out
        // from under the run that is writing into it.
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();
        const string PullRequestUrl = "https://github.com/example/hall9k/pull/10";

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Task reopened straight into the archive directory", ["criterion"],
                    TaskType.Feature, null, null, null, Now, ownerId),
                ownerId, Now);
            List<object> taskEvents = [.. lifecycle];

            TaskClaimed claimed = TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), Now);
            task.Apply(claimed);
            taskEvents.Add(claimed);

            TaskCompleted completed = TaskDecider.Complete(task, task.CurrentRunId!.Value, PullRequestUrl, Now);
            task.Apply(completed);
            taskEvents.Add(completed);

            Guid firstRunId = task.CurrentRunId!.Value;
            session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
            session.Events.StartStream<RunAggregate>(firstRunId,
                new RunDispatched(
                    firstRunId, taskId, nodeId, ownerId, task.LeaseGeneration, DomainId.New(), "/tmp/worktree",
                    "task/gets-reopened-into-archive", ExecutorMode.Subscription, Now),
                new RunCompleted(firstRunId, Now));
            await session.SaveChangesAsync();
        }

        // The task archives on the first sweep.
        await NewEngine(store).PollOnceAsync(CancellationToken.None);
        string tasksRoot = ProjectHomePaths.TasksDirectory(_home);
        string archiveRoot = ProjectHomePaths.ArchivedTasksDirectory(_home);
        Directory.EnumerateDirectories(archiveRoot).Should().ContainSingle(
            "the closed-out task must have archived on the first sweep");

        Guid followUpRunId;
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId)
                ?? throw new InvalidOperationException("task not found");
            TaskReopened reopened = TaskDecider.Reopen(
                task, task.CurrentRunId!.Value, "task/gets-reopened-into-archive", "one more look",
                FollowUpKind.ReviewFeedback, automatic: false, Now, ownerId);
            task.Apply(reopened);
            session.Events.Append(taskId, reopened);

            // Mirrors RunLauncher: a follow-up run dispatched straight into the directory as it
            // is found on disk right now — still under tasks/_archive/, since the render sweep
            // has not run again yet — and still live (no RunCompleted/RunFailed appended).
            followUpRunId = DomainId.New();
            TaskClaimed reclaimed = TaskDecider.Claim(task, nodeId, ownerId, followUpRunId, Now);
            session.Events.Append(taskId, reclaimed);
            session.Events.StartStream<RunAggregate>(followUpRunId,
                new RunDispatched(
                    followUpRunId, taskId, nodeId, ownerId, task.LeaseGeneration + 1, DomainId.New(),
                    "/tmp/worktree-followup", "task/gets-reopened-into-archive", ExecutorMode.Subscription, Now));
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);

        LiveTaskDirectories(tasksRoot).Should().BeEmpty(
            "the follow-up run is still live inside tasks/_archive/; moving it out now would race that run");
        Directory.EnumerateDirectories(archiveRoot).Should().ContainSingle(
            "the task's directory must stay put, runs/ and all, until the follow-up run stops being live");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(followUpRunId, new RunFailed(followUpRunId, "agent process died", Now));
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);

        Directory.EnumerateDirectories(archiveRoot).Should().BeEmpty(
            "once the run stops being live, the reopened task is free to move back to the top level");
        LiveTaskDirectories(tasksRoot).Should().ContainSingle(
            "the reopened task must move back out now that nothing is still writing to its directory");
    }

    [Fact]
    public async Task A_task_rendered_live_before_it_closes_out_still_archives_once_it_does()
    {
        // Regression, adversarial review cycle 1: every real task renders live under tasks/
        // (tasks/_archive/ does not exist yet) well before it ever reaches true closeout, unlike
        // the fixture above, which seeds the terminal state before the very first sweep and so
        // never exercises HomeEntryWriter.Write moving a directory INTO an archive root that does
        // not exist on disk yet.
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();
        const string PullRequestUrl = "https://github.com/example/hall9k/pull/4";

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Task that was live before it closes out", ["criterion"], TaskType.Feature,
                    null, null, null, Now, ownerId),
                ownerId, Now);
            List<object> taskEvents = [.. lifecycle];

            TaskClaimed claimed = TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), Now);
            task.Apply(claimed);
            taskEvents.Add(claimed);

            session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);

        string tasksRoot = ProjectHomePaths.TasksDirectory(_home);
        string archiveRoot = ProjectHomePaths.ArchivedTasksDirectory(_home);
        LiveTaskDirectories(tasksRoot).Should().ContainSingle("the task is still Working on the first sweep");
        Directory.Exists(archiveRoot).Should().BeFalse(
            "nothing has archived yet, so the render sweep must never have created the archive root");

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId)
                ?? throw new InvalidOperationException("task not found");
            Guid runId = task.CurrentRunId!.Value;
            TaskCompleted completed = TaskDecider.Complete(task, runId, PullRequestUrl, Now);
            session.Events.Append(taskId, completed);
            session.Events.StartStream<RunAggregate>(runId,
                new RunDispatched(
                    runId, taskId, nodeId, ownerId, task.LeaseGeneration, DomainId.New(), "/tmp/worktree",
                    "task/was-live", ExecutorMode.Subscription, Now),
                new RunCompleted(runId, Now));
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);

        LiveTaskDirectories(tasksRoot).Should().BeEmpty(
            "a task that has reached true closeout must not remain at the top level");
        string archivedDirectory = Directory.EnumerateDirectories(archiveRoot).Should().ContainSingle().Subject;
        File.ReadAllText(Path.Combine(archivedDirectory, "task.md")).Should().Contain("state: Done");
    }

    [Fact]
    public async Task An_abandoned_task_moves_into_the_archive_directory()
    {
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Task nobody wants any more", ["criterion"], TaskType.Feature, null,
                null, null, Now, ownerId);
            TaskAggregate task = new();
            task.Apply(added);
            TaskAbandoned abandoned = TaskDecider.Abandon(task, "no longer needed", Now, ownerId);
            session.Events.StartStream<TaskAggregate>(taskId, added, abandoned);
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);

        string tasksRoot = ProjectHomePaths.TasksDirectory(_home);
        string archiveRoot = ProjectHomePaths.ArchivedTasksDirectory(_home);
        LiveTaskDirectories(tasksRoot).Should().BeEmpty("abandoned is a terminal state; nothing here is still live");
        string archivedDirectory = Directory.EnumerateDirectories(archiveRoot).Should().ContainSingle().Subject;
        File.ReadAllText(Path.Combine(archivedDirectory, "task.md")).Should().Contain("state: Abandoned");
    }

    [Fact]
    public async Task An_abandoned_task_with_a_live_run_stays_at_the_top_level_until_the_run_stops()
    {
        // Regression, adversarial review cycle 1: abandoning a task does not kill whatever agent
        // is currently running for it — no daemon-side handler reacts to TaskAbandoned — so
        // archiving unconditionally would move runs/<run-id>/ out from under a process still
        // writing to it, exactly the hazard the true-closeout rule above already guards against.
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();
        Guid runId;

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Task abandoned mid-run", ["criterion"], TaskType.Feature, null, null, null,
                    Now, ownerId),
                ownerId, Now);
            List<object> taskEvents = [.. lifecycle];

            TaskClaimed claimed = TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), Now);
            task.Apply(claimed);
            taskEvents.Add(claimed);
            runId = task.CurrentRunId!.Value;

            TaskAbandoned abandoned = TaskDecider.Abandon(task, "changed my mind", Now, ownerId);
            taskEvents.Add(abandoned);

            session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
            session.Events.StartStream<RunAggregate>(runId,
                new RunDispatched(
                    runId, taskId, nodeId, ownerId, task.LeaseGeneration, DomainId.New(), "/tmp/worktree",
                    "task/abandoned-mid-run", ExecutorMode.Subscription, Now));
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);

        string tasksRoot = ProjectHomePaths.TasksDirectory(_home);
        string archiveRoot = ProjectHomePaths.ArchivedTasksDirectory(_home);
        LiveTaskDirectories(tasksRoot).Should().ContainSingle(
            "the run is still live, so archiving now would move it out from under itself");
        Directory.Exists(archiveRoot).Should().BeFalse("nothing has archived yet");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new RunFailed(runId, "agent process died", Now));
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);

        LiveTaskDirectories(tasksRoot).Should().BeEmpty("the run has stopped, so the abandoned task can archive now");
        Directory.EnumerateDirectories(archiveRoot).Should().ContainSingle();
    }

    [Fact]
    public async Task A_reopened_archived_task_moves_back_to_the_top_level()
    {
        // The other half of the archive rule (backlog 51): the folder must never lie about
        // liveness, so a task that leaves its terminal state has to come back out on the very
        // next sweep, carrying task.md, workspace/ and runs/ with it exactly as it moved in.
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();
        const string PullRequestUrl = "https://github.com/example/hall9k/pull/3";
        Guid runId;

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Task that gets reopened", ["criterion"], TaskType.Feature, null,
                    null, null, Now, ownerId),
                ownerId, Now);
            List<object> taskEvents = [.. lifecycle];

            TaskClaimed claimed = TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), Now);
            task.Apply(claimed);
            taskEvents.Add(claimed);

            TaskCompleted completed = TaskDecider.Complete(task, task.CurrentRunId!.Value, PullRequestUrl, Now);
            task.Apply(completed);
            taskEvents.Add(completed);

            runId = task.CurrentRunId!.Value;
            session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
            session.Events.StartStream<RunAggregate>(runId,
                new RunDispatched(
                    runId, taskId, nodeId, ownerId, task.LeaseGeneration, DomainId.New(), "/tmp/worktree",
                    "task/gets-reopened", ExecutorMode.Subscription, Now),
                new RunCompleted(runId, Now));

            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);
        string tasksRoot = ProjectHomePaths.TasksDirectory(_home);
        string archiveRoot = ProjectHomePaths.ArchivedTasksDirectory(_home);
        Directory.EnumerateDirectories(archiveRoot).Should().ContainSingle(
            "the closed-out task must have archived on the first sweep");
        File.WriteAllText(
            Path.Combine(Directory.EnumerateDirectories(archiveRoot).Single(), "workspace", "notes.md"), "keep me");

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId)
                ?? throw new InvalidOperationException("task not found");
            TaskReopened reopened = TaskDecider.Reopen(
                task, runId, "task/gets-reopened", "one more look", FollowUpKind.ReviewFeedback, automatic: false,
                Now, ownerId);
            session.Events.Append(taskId, reopened);
            await session.SaveChangesAsync();
        }

        await NewEngine(store).PollOnceAsync(CancellationToken.None);

        Directory.EnumerateDirectories(archiveRoot).Should().BeEmpty(
            "a reopened task is live again and must not stay archived");
        string liveDirectory = LiveTaskDirectories(tasksRoot).Should()
            .ContainSingle("the reopened task must move back to the top level")
            .Subject;
        File.ReadAllText(Path.Combine(liveDirectory, "task.md")).Should().Contain("state: Queued");
        File.ReadAllText(Path.Combine(liveDirectory, "workspace", "notes.md")).Should().Be("keep me",
            "the move back out must carry the same directory, workspace and all, not a fresh empty one");
    }

    [Fact]
    public async Task A_stray_directory_inside_the_archive_root_is_reconciled_like_a_top_level_stray()
    {
        // The orphan reconciler treats tasks/_archive/ as platform-owned exactly like tasks/
        // itself (backlog 51): a stray directory there is caught by the same rule, and the
        // archive root itself must never be mistaken for a stray inside tasks/'s own pass.
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            RegisterProject(session, projectId, ownerId, "hall9k");
            await session.SaveChangesAsync();
        }

        string strayDirectory = Path.Combine(
            ProjectHomePaths.ArchivedTasksDirectory(_home), "deadbeef-leftover-from-somewhere");
        Directory.CreateDirectory(strayDirectory);
        File.WriteAllText(Path.Combine(strayDirectory, "task.md"), "stale generated content");

        ProjectHomeRenderSweepResult sweep = await NewEngine(store).PollOnceAsync(CancellationToken.None);

        sweep.OrphansHandled.Should().Be(1);
        Directory.Exists(strayDirectory).Should().BeFalse("an empty shell orphan is removed, not left behind");
        Directory.Exists(ProjectHomePaths.ArchivedTasksDirectory(_home)).Should().BeTrue(
            "the archive root itself must never be treated as an orphan by its own reconciliation pass");
        File.Exists(Path.Combine(ProjectHomePaths.ArchivedTasksDirectory(_home), "ORPHANED.md")).Should().BeFalse(
            "the archive root is platform-owned, not a stray a human dropped beside the tasks it holds");
    }

    private static IEnumerable<string> LiveTaskDirectories(string tasksRoot) =>
        Directory.EnumerateDirectories(tasksRoot)
            .Where(directory => Path.GetFileName(directory) != ProjectHomePaths.ArchiveDirectoryName);

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
