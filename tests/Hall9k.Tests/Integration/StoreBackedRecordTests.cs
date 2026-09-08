using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The records whose Marten round trip has to be exercised against a real store — the half of
/// each feature its DB-free unit tests structurally cannot reach, which is that the event actually
/// serializes, the inline projection actually fires, and the read model actually comes back — with
/// one container shared across the six seams below. Each was its own class and so its own
/// container for one to six tests; every assertion here loads what its own test seeded, by id, so
/// a sibling seam's rows are as invisible as a sibling test's already were
/// (<see cref="PostgresFixture"/> has always shared one database across a class).
/// <para>
/// Epic membership — an epic is its own stream, and a task's membership rides along on the task's
/// own stream: two independent records that both have to project correctly for
/// <c>h9k epic show</c> and <c>h9k task list --epic</c> to answer honestly (Decisions Log #100).
/// </para>
/// <para>
/// Review boundary approval — <see cref="ReviewBoundaryApproved"/> and
/// <see cref="ReviewParked.IsInteractiveGate"/> (task: interactive mode becomes a recorded
/// property of the task): the one thing the in-memory <c>RunAggregateTests</c> coverage cannot
/// confirm, that the event and fields round-trip through Marten's schema and JSON serialization
/// and that <c>RunDetailsProjection</c>'s inline Apply methods fire against a real database.
/// </para>
/// <para>
/// Project registration — a project's own record and the settings changes applied on top of it.
/// </para>
/// <para>
/// Idea promotion — promotion writes two streams in one transaction (Decisions Log #35): the
/// idea's, which records what it became, and the task's, which records where it came from. Either
/// half alone would be a broken provenance trail, so the append is atomic and this is what proves
/// both projections land.
/// </para>
/// <para>
/// Logged interactions — <see cref="TaskLogInteractionCommand.AppendInteractionAsync"/> against a
/// real store, the round trip <see cref="Hall9k.Tests.Cli.TaskLogInteractionCommandTests"/>' own
/// doc comment calls that command's integration-tier concern (independent pre-PR review, cycle 1,
/// low: the DB-free tests alone left the two guards and the append itself unexercised).
/// </para>
/// <para>
/// Work-item connections — building the importer out of whatever connections this install has
/// registered (PLAN.md §10), including the ones it cannot use. The question each of those tests
/// asks is how far a Jira misconfiguration is allowed to reach: GitHub piggybacks the machine's
/// own <c>gh</c> login, needs no connection record, and cannot be ambiguous, so a task whose
/// reference is a GitHub issue must keep working while Jira is unusable. Origin incident
/// (2026-08-22): the pre-PR review of the Jira branch found two registered Jira connections making
/// <c>h9k task show</c> and <c>h9k task add --from-issue</c> exit non-zero quoting a Jira refusal.
/// Those tests need the real projection because "which Jira connection" is a query over the
/// connection list rather than a comparison — and because the connection list is the one piece of
/// state here that is not scoped by id, each of them clears it and registers its own, which is
/// what makes them safe beside the seams above rather than only beside each other.
/// </para>
/// <para>
/// <c>TaskAndIdeaIdResolverEmptyFragmentTests</c> deliberately stayed out, small as it is: its
/// scenario is a database holding exactly one task and exactly one idea, so that a vacuous
/// empty-fragment match would have something to vacuously match. Beside these seams' rows it would
/// still pass with the bug back in place, because the resolver would then refuse for ambiguity
/// instead of for the empty fragment — a test that keeps its name and loses its teeth.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class StoreBackedRecordTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_task_joins_an_epic_at_add_and_the_epic_shows_it_as_a_member()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid epicId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<EpicAggregate>(
                epicId, EpicDecider.Add(epicId, projectId, "Interactive mode", Now, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Wire the prompt loop", acceptanceCriteria: [], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, Now, ownerId,
                epicId: epicId);
            session.Events.StartStream<TaskAggregate>(taskId, added);

            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            TaskDetails? details = await session.LoadAsync<TaskDetails>(taskId, cts.Token);
            TaskListItem? row = await session.LoadAsync<TaskListItem>(taskId, cts.Token);

            details!.EpicId.Should().Be(epicId, "membership is recorded on the task's own stream");
            row!.EpicId.Should().Be(epicId, "the lean row backing h9k task list --epic carries it too");
        }
    }

    [Fact]
    public async Task A_task_leaves_its_epic_through_revision_and_projections_agree()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid epicId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<EpicAggregate>(
                epicId, EpicDecider.Add(epicId, projectId, "Interactive mode", Now, ownerId));
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Wire the prompt loop", acceptanceCriteria: [], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, Now, ownerId,
                epicId: epicId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.EpicId.Should().Be(epicId);

            TaskRevised revised = TaskDecider.Revise(
                task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
                Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
                Now.AddHours(1), ownerId, Optional<Guid?>.Of(null));
            session.Events.Append(taskId, revised);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            TaskDetails? details = await session.LoadAsync<TaskDetails>(taskId, cts.Token);
            TaskListItem? row = await session.LoadAsync<TaskListItem>(taskId, cts.Token);

            details!.EpicId.Should().BeNull("leaving is the same revision gate, with the field cleared");
            row!.EpicId.Should().BeNull();
        }
    }

    [Fact]
    public async Task Closing_an_epic_never_happens_on_its_own_when_its_last_task_finishes()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid epicId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<EpicAggregate>(
                epicId, EpicDecider.Add(epicId, projectId, "Interactive mode", Now, ownerId));
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Wire the prompt loop", acceptanceCriteria: ["it works"], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, Now, ownerId,
                epicId: epicId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Assign(task, ownerId, [], Now, ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Claim(task, DomainId.New(), ownerId, DomainId.New(), Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Complete(task, task.CurrentRunId!.Value, "https://example/pr/1", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            EpicDetails? epic = await session.LoadAsync<EpicDetails>(epicId, cts.Token);
            epic!.State.Should().Be(
                EpicState.Open, "nothing closes an epic automatically, not even its last member task closing out");
        }
    }

    [Fact]
    public async Task Joining_refuses_an_epic_from_a_different_project()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid epicProjectId = DomainId.New();
        Guid otherProjectId = DomainId.New();
        Guid epicId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<EpicAggregate>(
                epicId, EpicDecider.Add(epicId, epicProjectId, "Interactive mode", Now, ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            Func<Task> resolve = () => EpicIdResolver.ResolveForMembershipAsync(
                session, epicId.ToString(), otherProjectId, cts.Token);

            await resolve.Should().ThrowAsync<DomainConflictException>()
                .WithMessage("*belongs to a different project*");
        }
    }

    [Fact]
    public async Task Joining_refuses_a_closed_epic()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid epicId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<EpicAggregate>(
                epicId, EpicDecider.Add(epicId, projectId, "Interactive mode", Now, ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            EpicAggregate epic = (await session.Events.AggregateStreamAsync<EpicAggregate>(epicId, token: cts.Token))!;
            session.Events.Append(epicId, EpicDecider.Close(epic, "no longer needed", Now.AddHours(1), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            Func<Task> resolve = () => EpicIdResolver.ResolveForMembershipAsync(
                session, epicId.ToString(), projectId, cts.Token);

            await resolve.Should().ThrowAsync<DomainConflictException>()
                .WithMessage("*Open is the only state a task can join*");
        }
    }

    [Fact]
    public async Task An_empty_fragment_never_vacuously_matches_the_only_epic()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid epicId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<EpicAggregate>(
                epicId, EpicDecider.Add(epicId, projectId, "Interactive mode", Now, ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            Func<Task> resolveEmpty = () => EpicIdResolver.ResolveForMembershipAsync(
                session, "", projectId, cts.Token);
            Func<Task> resolveDashesOnly = () => EpicIdResolver.ResolveForMembershipAsync(
                session, "-", projectId, cts.Token);

            await resolveEmpty.Should().ThrowAsync<DomainValidationException>()
                .WithMessage("*no characters to match*");
            await resolveDashesOnly.Should().ThrowAsync<DomainValidationException>()
                .WithMessage("*no characters to match*");
        }
    }

    // ── review boundary approval ──
    private static readonly DateTimeOffset BoundaryNow = new(2026, 9, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Review_boundary_approved_round_trips_and_restores_the_parked_phase_through_a_real_store()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid runId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<RunAggregate>(runId,
                new RunDispatched(
                    runId, taskId, DomainId.New(), DomainId.New(), 1, DomainId.New(),
                    "/wt/interactive", "task/interactive", ExecutorMode.Subscription, BoundaryNow),
                new ReviewDispatched(runId, DomainId.New(), 1, 5001, BoundaryNow, BoundaryNow),
                new ReviewCompleted(runId, 1, ReviewVerdict.NeedsFixes, BoundaryNow),
                new ReviewParked(
                    runId, "Interactive mode is on for this task: the review verdict calls for a fix session.",
                    BoundaryNow, IsInteractiveGate: true));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession query = store.QuerySession())
        {
            RunDetails? parkedDetails = await query.LoadAsync<RunDetails>(runId, cts.Token);
            parkedDetails.Should().NotBeNull();
            parkedDetails!.State.Should().Be(RunState.ReviewParked);
            parkedDetails.ParkedIsInteractiveGate.Should().BeTrue();
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewBoundaryApproved(runId, BoundaryNow, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession query = store.QuerySession())
        {
            RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
            run.ReviewPhase.Should().Be(ReviewPhase.FixNeeded, "restored exactly where the park interrupted the loop");
            run.State.Should().Be(RunState.UnderReview);
            run.InteractiveGateCleared.Should().BeTrue();
            run.ParkedIsInteractiveGate.Should().BeFalse();

            RunDetails? details = await query.LoadAsync<RunDetails>(runId, cts.Token);
            details.Should().NotBeNull();
            details!.State.Should().Be(RunState.UnderReview);
            details.ParkedReason.Should().BeNull();
            details.ParkedIsInteractiveGate.Should().BeFalse();
            details.BoundaryApprovals.Should().ContainSingle().Which.ApprovedAt.Should().Be(BoundaryNow);
        }
    }

    // ── project registration ──
    private static readonly DateTimeOffset RegistrationNow = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Registering_a_project_round_trips_through_events_aggregate_and_projection()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid connectionId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            OwnerRegistered owner = OwnerDecider.Register(ownerId, "Brian Hall", "brian@hallmanac.com", RegistrationNow);
            session.Events.StartStream<OwnerAggregate>(ownerId, owner);

            ConnectionRegistered connection = ConnectionDecider.Register(
                connectionId, ownerId, WorkItemProvider.GitHub, "Hallmanac", CredentialReference.GhCli, RegistrationNow);
            session.Events.StartStream<ConnectionAggregate>(connectionId, connection);

            ProjectRegistered project = ProjectDecider.Register(
                projectId, ownerId, connectionId, "hall9k", "/repos/hall9k.git",
                new Uri("https://github.com/Hallmanac/hall9k"), null, RegistrationNow);
            session.Events.StartStream<ProjectAggregate>(projectId, project);

            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectAggregate? aggregate = await session.Events.AggregateStreamAsync<ProjectAggregate>(
                projectId, token: cts.Token);
            aggregate.Should().NotBeNull();

            ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
                aggregate!,
                verifyCommands: new List<VerifyCommand> { new("build", "dotnet build"), new("test", "dotnet test") },
                skipPermissions: true,
                contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
                changedAt: RegistrationNow.AddMinutes(1), changedByOwnerId: ownerId);
            session.Events.Append(projectId, changed);

            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession query = store.QuerySession())
        {
            ProjectDetails? details = await query.LoadAsync<ProjectDetails>(
                projectId, cts.Token);

            details.Should().NotBeNull();
            details!.Name.Should().Be("hall9k");
            details.BaseBranch.Should().Be("main", "the decider defaults a blank base branch");
            details.VerifyCommands.Should().HaveCount(2);
            details.SkipPermissions.Should().BeTrue();
            details.MaxParallelAgents.Should().Be(3, "absent optionals leave settings unchanged");
            details.OwnerId.Should().Be(ownerId);
            details.ConnectionId.Should().Be(connectionId);
        }
    }

    /// <summary>
    /// The default home is derived from the project name through a lossy slug, so two different
    /// names reach one directory. Origin incident (2026-08-23): the pre-PR review of the
    /// project-home branch traced "My App" and "my-app" through h9k project add and found both
    /// registering happily, both resolving to <c>~/.hall9k/projects/my-app</c>, and the second
    /// overwriting the first's generated AGENTS.md while every step reported success. The second
    /// cycle found h9k project set --home walking straight past the guard the first one added,
    /// which is why the check is scoped to whatever a command is changing rather than to a
    /// registration.
    /// </summary>
    [Fact]
    public async Task Two_projects_cannot_claim_one_home_or_one_repository_path()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        string home = Path.Combine(Path.GetTempPath(), $"hall9k-claims-{Guid.NewGuid():N}", "my-app");
        string bare = ProjectHomePaths.BareRepository(home, "My App");
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(projectId, ProjectDecider.Register(
                projectId, DomainId.New(), DomainId.New(), "My App", bare, null, null, RegistrationNow,
                ProjectHome.Parse(home)));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            Func<Task> sameHome = () => ProjectHomeClaims.EnsureUnclaimedAsync(
                session, Guid.Empty, home, Path.Combine(home, "repo", "elsewhere.git"), cts.Token);
            await sameHome.Should().ThrowAsync<DomainConflictException>()
                .WithMessage("*My App*already lives*");

            Func<Task> sameRepository = () => ProjectHomeClaims.EnsureUnclaimedAsync(
                session, Guid.Empty, Path.Combine(Path.GetTempPath(), $"other-{Guid.NewGuid():N}"), bare, cts.Token);
            await sameRepository.Should().ThrowAsync<DomainConflictException>()
                .WithMessage("*My App*already cuts its worktrees*");

            // The project that holds the claim may always re-claim it, which is what makes
            // h9k project init idempotent against a home it created itself.
            Func<Task> itsOwn = () => ProjectHomeClaims.EnsureUnclaimedAsync(
                session, projectId, home, bare, cts.Token);
            await itsOwn.Should().NotThrowAsync();

            // Blank is an absence rather than a place, so it matches nothing. h9k project set
            // depends on that: it checks only the one of --home / --repo that the invocation is
            // actually changing, and a value nobody passed must not refuse a change to the other.
            Func<Task> onlyTheRepository = () => ProjectHomeClaims.EnsureUnclaimedAsync(
                session, DomainId.New(), string.Empty, Path.Combine(home, "repo", "elsewhere.git"), cts.Token);
            await onlyTheRepository.Should().NotThrowAsync();

            Func<Task> onlyTheHome = () => ProjectHomeClaims.EnsureUnclaimedAsync(
                session, DomainId.New(), home, string.Empty, cts.Token);
            await onlyTheHome.Should().ThrowAsync<DomainConflictException>()
                .WithMessage("*My App*already lives*");
        }
    }

    // ── idea promotion ──
    private static readonly DateTimeOffset PromotionNow = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Promoting_an_idea_records_provenance_in_both_directions()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid ideaId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<IdeaAggregate>(ideaId, IdeaDecider.Capture(
                ideaId, ownerId,
                "Ideas deserve their own discovery phase. The workspace is where research lands.",
                projectId: null, PromotionNow, ProjectHome.None));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(
                ideaId, token: cts.Token))!;
            idea.Should().NotBeNull();

            session.Events.Append(ideaId, IdeaDecider.AssignToProject(idea, projectId, PromotionNow.AddHours(1), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(
                ideaId, token: cts.Token))!;
            IdeaSeed seed = IdeaText.Seed(idea.Text);

            IdeaPromoted promoted = IdeaDecider.Promote(
                idea, taskId, projectId: null, seed.Objective, PromotionNow.AddDays(1), ownerId);
            TaskAdded added = TaskDecider.Add(
                taskId, promoted.ProjectId, promoted.Objective, acceptanceCriteria: [], TaskType.Feature,
                seed.Context, constraints: null, externalReference: null, promoted.PromotedAt, ownerId,
                model: null, blockedBy: null, sourceIdeaId: ideaId);

            session.Events.StartStream<TaskAggregate>(taskId, added);
            session.Events.Append(ideaId, promoted);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            IdeaDetails? idea = await session.LoadAsync<IdeaDetails>(ideaId, cts.Token);
            TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cts.Token);

            idea!.State.Should().Be(IdeaState.Promoted);
            idea.PromotedTaskId.Should().Be(taskId, "the idea's stream names the task it became");
            idea.ProjectId.Should().Be(projectId);

            task!.SourceIdeaId.Should().Be(ideaId, "the task's stream names the idea it came from");
            task.State.Should().Be(TaskState.Draft, "promotion produces an ordinary draft (log #34)");
            task.Objective.Should().Be("Ideas deserve their own discovery phase.");
            task.AgentContext.Should().Be("The workspace is where research lands.");
        }
    }

    /// <summary>
    /// Promotion is a one-time transition that mints a second stream, so two of them racing
    /// must not both land: the loser is refused at the database, and because the two appends
    /// share one transaction, the task it would have created never exists. Provenance stays
    /// two-way or it does not happen at all.
    /// </summary>
    [Fact]
    public async Task Two_racing_promotions_leave_exactly_one_task_behind()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid ideaId = DomainId.New();

        await using (IDocumentSession setup = store.LightweightSession())
        {
            setup.Events.StartStream<IdeaAggregate>(ideaId, IdeaDecider.Capture(
                ideaId, ownerId, "Two promotions race. Exactly one may win.", projectId, PromotionNow, ProjectHome.None));
            await setup.SaveChangesAsync(cts.Token);
        }

        // Two invocations read the same stream state, then both promote at the same expected
        // version — the database, not timing, decides which one becomes a task.
        await using IDocumentSession first = store.LightweightSession();
        await using IDocumentSession second = store.LightweightSession();

        StreamState fence1 = (await first.Events.FetchStreamStateAsync(ideaId, cts.Token))!;
        StreamState fence2 = (await second.Events.FetchStreamStateAsync(ideaId, cts.Token))!;
        IdeaAggregate view1 = (await first.Events.AggregateStreamAsync<IdeaAggregate>(
            ideaId, version: fence1.Version, token: cts.Token))!;
        IdeaAggregate view2 = (await second.Events.AggregateStreamAsync<IdeaAggregate>(
            ideaId, version: fence2.Version, token: cts.Token))!;

        Guid winningTaskId = DomainId.New();
        Guid losingTaskId = DomainId.New();
        Promote(first, view1, winningTaskId, fence1.Version, ownerId);
        Promote(second, view2, losingTaskId, fence2.Version, ownerId);

        await first.SaveChangesAsync(cts.Token);
        Func<Task> losing = () => second.SaveChangesAsync(cts.Token);
        await losing.Should().ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            "the second promotion must lose at the database, not by luck");

        await using IQuerySession verify = store.QuerySession();
        IdeaDetails? idea = await verify.LoadAsync<IdeaDetails>(ideaId, cts.Token);
        idea!.PromotedTaskId.Should().Be(winningTaskId, "the idea names the one task it became");

        TaskDetails? orphan = await verify.LoadAsync<TaskDetails>(losingTaskId, cts.Token);
        orphan.Should().BeNull("a task whose idea never recorded it would be provenance in one direction only");
    }

    /// <summary>What h9k idea promote writes: the task's first event and the idea's last, fenced together.</summary>
    private static void Promote(
        IDocumentSession session, IdeaAggregate idea, Guid taskId, long version, Guid ownerId)
    {
        IdeaSeed seed = IdeaText.Seed(idea.Text);
        IdeaPromoted promoted = IdeaDecider.Promote(
            idea, taskId, projectId: null, seed.Objective, PromotionNow.AddDays(1), ownerId);
        TaskAdded added = TaskDecider.Add(
            taskId, promoted.ProjectId, promoted.Objective, acceptanceCriteria: [], TaskType.Feature,
            seed.Context, constraints: null, externalReference: null, promoted.PromotedAt, ownerId,
            model: null, blockedBy: null, sourceIdeaId: idea.Id);

        session.Events.StartStream<TaskAggregate>(taskId, added);
        session.Events.Append(idea.Id, expectedVersion: version + 1, promoted);
    }

    // ── logged interactions ──
    private static readonly DateTimeOffset InteractionNow = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Refuses_to_log_against_a_task_with_no_active_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Log something", ["done"], TaskType.Chore,
                    null, null, null, InteractionNow, node.OwnerId),
                node.OwnerId, InteractionNow);
            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle]);
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        details.CurrentRunId.Should().BeNull("this task was seeded Queued, never claimed");

        Func<Task> act = () => TaskLogInteractionCommand.AppendInteractionAsync(
            session, details, Settings(), cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>().WithMessage("*no active run*");
    }

    [Fact]
    public async Task Refuses_to_log_against_a_stale_current_run_id_with_no_run_stream()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Log something", ["done"], TaskType.Chore,
                    null, null, null, InteractionNow, node.OwnerId),
                node.OwnerId, InteractionNow);
            TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, InteractionNow);
            task.Apply(claimed);
            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            // Deliberately no RunAggregate stream started for runId: CurrentRunId names a run
            // whose stream was never started, the stale-projection shape FetchStreamStateAsync's
            // own guard exists to catch.
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        details.CurrentRunId.Should().Be(runId);

        Func<Task> act = () => TaskLogInteractionCommand.AppendInteractionAsync(
            session, details, Settings(), cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>().WithMessage("*no run stream*");

        await using IQuerySession query = store.QuerySession();
        (await query.Events.FetchStreamStateAsync(runId, cts.Token)).Should().BeNull(
            "appending here must never implicitly create the run stream on a guard refusal");
    }

    [Fact]
    public async Task Appends_a_human_directed_interaction_to_the_runs_own_stream()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Log something", ["done"], TaskType.Chore,
                    null, null, null, InteractionNow, node.OwnerId),
                node.OwnerId, InteractionNow);
            TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, InteractionNow);
            task.Apply(claimed);
            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            seed.Events.StartStream<RunAggregate>(runId,
                new RunDispatched(runId, taskId, node.NodeId, node.OwnerId, task.LeaseGeneration,
                    DomainId.New(), "/tmp/log-interaction-worktree", "task/log-interaction-branch",
                    ExecutorMode.Subscription, InteractionNow));
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;

        ExternalInteractionLogged logged = await TaskLogInteractionCommand.AppendInteractionAsync(
            session, details,
            Settings(party: "the operator", summary: "Skip the workaround", humanDirected: true, reason: "Real bug"),
            cts.Token);
        await session.SaveChangesAsync(cts.Token);

        logged.RunId.Should().Be(runId);
        logged.LoggedByOwnerId.Should().Be(node.OwnerId);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ExternalInteractions.Should().ContainSingle();
        ExternalInteractionRecord record = run.ExternalInteractions[0];
        record.Party.Should().Be("the operator");
        record.Summary.Should().Be("Skip the workaround");
        record.HumanDirected.Should().BeTrue();
        record.Reason.Should().Be("Real bug");
    }

    private static TaskLogInteractionCommand.Settings Settings(
        string party = "another agent session", string summary = "Shared the worktree path", bool humanDirected = false,
        string? reason = null) => new()
    {
        Task = "unused-in-this-path",
        Party = party,
        Summary = summary,
        HumanDirected = humanDirected,
        Reason = reason,
    };

    // ── work-item connections ──
    private static readonly DateTimeOffset ConnectionsNow = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Site = new("https://hall9k.atlassian.net");
    private const string GitHubIssue = "github:Hallmanac/hall9k#42";

    [Fact]
    public async Task An_ambiguous_jira_connection_refuses_jira_and_leaves_github_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        await ClearConnectionsAsync(store, cts.Token);
        await RegisterAsync(store, "alice@corp.com", Site, cts.Token);
        await RegisterAsync(store, "bob@corp.com", Site, cts.Token);

        await using IQuerySession session = store.QuerySession();
        WorkItemImporter importer = await ImporterAsync(session, cts.Token);

        importer.WebUrl(GitHubIssue).Should().Be(
            new Uri("https://github.com/Hallmanac/hall9k/issues/42"),
            "GitHub needs no connection record and cannot be the ambiguous one");

        Func<Task> jira = () => ImportAsync(importer, WorkItemProvider.Jira, cts.Token);

        (await jira.Should().ThrowAsync<DomainConflictException>(
            "the ambiguity is still fatal to the commands that actually need Jira")).Which.Message
            .Should().Contain("2 Jira connections are registered")
            .And.Contain("alice@corp.com")
            .And.Contain("bob@corp.com")
            .And.NotContain("Known sources", "the sources that happen to be configured are not the answer");
    }

    /// <summary>
    /// The same degradation for the other way a Jira connection is unusable: one registered before
    /// the site was recorded on the event, which is a Jira connection with nowhere to send a
    /// request. The refusal keeps its own kind, because a connection that needs registering again
    /// is not the same failure as two nobody can choose between.
    /// </summary>
    [Fact]
    public async Task A_jira_connection_with_no_site_refuses_jira_and_leaves_github_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        await ClearConnectionsAsync(store, cts.Token);
        await RegisterAsync(store, "alice@corp.com", site: null, cts.Token);

        await using IQuerySession session = store.QuerySession();
        WorkItemImporter importer = await ImporterAsync(session, cts.Token);

        importer.WebUrl(GitHubIssue).Should().NotBeNull();

        Func<Task> jira = () => ImportAsync(importer, WorkItemProvider.Jira, cts.Token);

        (await jira.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("no site recorded")
            .And.Contain("h9k connection add jira --site");
    }

    [Fact]
    public async Task An_install_with_no_jira_connection_is_told_the_command_that_connects_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        await ClearConnectionsAsync(store, cts.Token);

        await using IQuerySession session = store.QuerySession();
        WorkItemImporter importer = await ImporterAsync(session, cts.Token);

        importer.WebUrl(GitHubIssue).Should().NotBeNull();

        Func<Task> jira = () => ImportAsync(importer, WorkItemProvider.Jira, cts.Token);

        (await jira.Should().ThrowAsync<DomainNotFoundException>()).Which.Message
            .Should().Contain("No Jira connection is registered")
            .And.Contain("h9k connection add jira --site");
    }

    /// <summary>
    /// The importer this install would build, with the Jira half's request seam pinned to a
    /// requester that throws if it is ever actually invoked. Every test here reaches Jira only
    /// down a path that refuses before a request is built, so nothing should ever call it, and a
    /// case added later (a single valid connection, imported for real) fails loudly here rather
    /// than issuing a live HTTPS request to Atlassian.
    /// <para>
    /// That last part holds only for a case routed through this helper, and nothing enforces that
    /// routing. <see cref="WorkItemConnections.ImporterAsync"/> is a public static; calling it
    /// directly is the spelling this file's own three call sites used until they were moved here,
    /// and the spelling every other caller in the tree still uses, so a case written that way
    /// falls straight through to <c>JiraHttp.Requester</c>'s real-HTTP default. This is a
    /// convention among the cases in this file, not a rule the build checks, and it is recorded
    /// as the blind spot it is rather than as coverage, the way each source-scanning guard on
    /// this branch records its own (PLAN.md §16 #110 exists because a guard doc claimed a reach
    /// its marker did not have). It is deliberately not given a guard of its own: the three
    /// sibling guards each enforce a rule across dozens of sites tree-wide, while this rule has
    /// one call site in one file, so a scan asserting "this call pins a requester" would be a
    /// fourth coverage claim to keep true for no additional reach.
    /// </para>
    /// <para>
    /// The GitHub half has no equivalent seam through this entry point — <c>ImporterAsync</c>
    /// constructs <c>GitHubWorkItemProvider</c> and <c>GitHubPullRequestProvider</c> on
    /// <c>ExternalProcess.Runner</c>'s real-process default with no runner parameter to override —
    /// which is the gap PLAN.md decision #109 records as still open.
    /// </para>
    /// </summary>
    private static Task<WorkItemImporter> ImporterAsync(
        IQuerySession session, CancellationToken cancellationToken) =>
        WorkItemConnections.ImporterAsync(session, cancellationToken, FakeJiraRequester.NeverInvoked());

    private static Task<ImportedWorkItem> ImportAsync(
        WorkItemImporter importer, WorkItemProvider provider, CancellationToken cancellationToken) =>
        importer.ImportAsync(
            new WorkItemImportRequest(provider, "PROJ-123", Path.GetTempPath()), cancellationToken);

    /// <summary>
    /// A registration, appended as the event rather than through the decider when the site is
    /// null: the decider requires one, and the connection this asks about is the one written
    /// before the field existed.
    /// </summary>
    private static async Task RegisterAsync(
        DocumentStore store, string email, Uri? site, CancellationToken cancellationToken)
    {
        Guid connectionId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<ConnectionAggregate>(connectionId, new ConnectionRegistered(
            connectionId,
            DomainId.New(),
            WorkItemProvider.Jira,
            email,
            CredentialReference.EnvironmentVariable("JIRA_API_TOKEN"),
            ConnectionsNow,
            site));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task ClearConnectionsAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.DeleteWhere<ConnectionDetails>(connection => true);
        await session.SaveChangesAsync(cancellationToken);
    }
}
