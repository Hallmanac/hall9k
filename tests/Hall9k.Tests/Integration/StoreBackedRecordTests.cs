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
/// Idea fan-out (backlog 31) — cutting a task writes two streams in one transaction: the idea's,
/// which records the cut, and the task's, which records where it came from. Either half alone
/// would be a broken provenance trail, so the append is atomic and this is what proves both
/// projections land — and, unlike the retired 1:1 promote, that the fan-out itself is repeatable.
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

    // ── idea fan-out ──
    private static readonly DateTimeOffset PromotionNow = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_idea_fans_out_into_two_draft_tasks_and_provenance_runs_both_ways()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid ideaId = DomainId.New();
        Guid firstTaskId = DomainId.New();
        Guid secondTaskId = DomainId.New();

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

        await Cut(store, ideaId, firstTaskId, projectId, "Give ideas a discovery workspace", ownerId, PromotionNow.AddDays(1), cts.Token);
        await Cut(store, ideaId, secondTaskId, projectId, "Render the workspace path on idea show", ownerId, PromotionNow.AddDays(2), cts.Token);

        await using (IQuerySession session = store.QuerySession())
        {
            IdeaDetails? idea = await session.LoadAsync<IdeaDetails>(ideaId, cts.Token);
            TaskDetails? first = await session.LoadAsync<TaskDetails>(firstTaskId, cts.Token);
            TaskDetails? second = await session.LoadAsync<TaskDetails>(secondTaskId, cts.Token);

            idea!.State.Should().Be(IdeaState.Captured, "cutting tasks never ends the idea on its own");
            idea.CutTaskIds.Should().Equal(firstTaskId, secondTaskId);
            idea.ProjectId.Should().Be(projectId);

            first!.SourceIdeaId.Should().Be(ideaId, "the task's stream names the idea it came from");
            first.State.Should().Be(TaskState.Draft, "cutting produces an ordinary draft (log #34)");
            first.Objective.Should().Be("Give ideas a discovery workspace");
            second!.SourceIdeaId.Should().Be(ideaId);
            second.Objective.Should().Be("Render the workspace path on idea show");
        }
    }

    /// <summary>
    /// A stream carrying the retired <see cref="IdeaPromoted"/> event — no decider method
    /// produces it any more, but a stream that recorded one before this branch shipped still
    /// carries it forever — replays correctly against a real store, both through the Inline
    /// <see cref="IdeaDetails"/> projection (today's <c>Apply</c> handler reconciles it into
    /// <see cref="IdeaState.Concluded"/> and adds the task to <c>CutTaskIds</c>, so a document
    /// materialized by <em>this</em> build reads right) and through a fresh
    /// <c>AggregateStreamAsync</c> read of <see cref="IdeaAggregate"/>.
    /// <para>
    /// <see cref="IdeaShowCommand.WriteFanOutAsync"/> deliberately reads the aggregate rather than
    /// the projection for this exact reason: <see cref="IdeaDetails"/> is Inline, so a document an
    /// <em>old</em> build's <c>IdeaPromoted</c> handler already materialized — before
    /// <c>CutTaskIds</c> existed on that projection's shape at all, the shape 34a618a6's own
    /// promotion from 3ba186b6 was written under — carries no such key and would read as an
    /// empty fan-out forever, since a terminal idea's stream never gets another event to trigger
    /// a re-materialization. That specific stale-document shape cannot be reproduced here without
    /// hand-writing raw JSON under the old build's schema, which this test does not attempt; what
    /// it does prove is that the mechanism <c>WriteFanOutAsync</c> actually relies on — event
    /// replay of <see cref="IdeaPromoted"/>, independent of whatever the projection currently
    /// holds — works end to end against a real store, including through Marten's own JSON
    /// round-trip of the event type itself.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_legacy_promoted_idea_replays_correctly_through_both_the_projection_and_a_fresh_aggregate_read()
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
                ideaId, ownerId, "An idea promoted before the fan-out redesign shipped.",
                projectId, PromotionNow, ProjectHome.None));
            session.Events.Append(ideaId, new IdeaPromoted(
                ideaId, taskId, projectId, "An idea promoted before the fan-out redesign shipped.",
                PromotionNow.AddDays(1), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            IdeaDetails? details = await session.LoadAsync<IdeaDetails>(ideaId, cts.Token);
            details!.State.Should().Be(IdeaState.Concluded, "a promotion always meant something came of the idea");
            details.CutTaskIds.Should().Equal(taskId);

            IdeaAggregate? live = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token);
            live!.CutTaskIds.Should().Equal([taskId], "h9k idea show reads this list, not the projection's own");
        }
    }

    /// <summary>
    /// What h9k task add --from-idea writes: the task's first event and the idea's own cut,
    /// unfenced — the same way every ordinary idea mutation is (h9k idea assign, h9k idea
    /// revise). Cutting has no "once" invariant to protect against racing itself, unlike
    /// promotion's atomic cut-then-conclude: fan-out is meant to be freely repeatable.
    /// </summary>
    private static async Task Cut(
        DocumentStore store, Guid ideaId, Guid taskId, Guid projectId, string objective, Guid ownerId,
        DateTimeOffset cutAt, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(
            ideaId, token: cancellationToken))!;

        IdeaTaskCut cut = IdeaDecider.CutTask(idea, taskId, objective, cutAt, ownerId);
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, cut.Objective, acceptanceCriteria: [], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null, cutAt, ownerId,
            model: null, blockedBy: null, sourceIdeaId: idea.Id);

        session.Events.StartStream<TaskAggregate>(taskId, added);
        session.Events.Append(idea.Id, cut);
        await session.SaveChangesAsync(cancellationToken);
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
