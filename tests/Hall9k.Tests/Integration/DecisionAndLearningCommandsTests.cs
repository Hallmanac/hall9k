using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Replication;
using Hall9k.Daemon;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Learning.Queries;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using JasperFx.Events;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k decide</c> and <c>h9k learn</c> through their own command seams, against real
/// Marten/Postgres (idea d805fd8b, piece 1): record, list, show, supersede, retire, the refused
/// agent decision, and the provenance nulls a statement typed at a shell carries. Nothing here
/// touches a repository or a remote (Brian's 2026-09-13 testing rule) — a project is registered
/// with a path nothing ever reads.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class DecisionAndLearningCommandsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/nothing/reads/this";

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public DecisionAndLearningCommandsTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_decision_typed_at_a_shell_is_recorded_listed_and_shown_with_explicit_provenance_nulls()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (DecisionRecorded recorded, ResolvedKnowledgeScope scope) = await DecideCommand.RunAsync(
            session,
            new DecideCommand.Settings
            {
                Statement = "Agents never push; the daemon pushes every branch with --force-with-lease",
                Project = project.Name,
                Origin = "2026-08-17, PR #6: a plain push stranded two rebased follow-up branches",
            },
            node.OwnerId, Now, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        scope.Scope.Should().Be(KnowledgeScope.Project);
        scope.Label.Should().Be(project.Name);

        DecisionDetails stored = (await session.LoadAsync<DecisionDetails>(recorded.Id, CancellationToken.None))!;
        stored.Statement.Should().StartWith("Agents never push");
        stored.Status.Should().Be(DecisionStatus.Recorded);
        stored.ScopeId.Should().Be(project.Id);
        stored.OriginIncident.Should().StartWith("2026-08-17");
        stored.Provenance!.RunId.Should().BeNull("nothing named a run, so nothing is claimed about one");
        stored.Provenance.TaskId.Should().BeNull();
        stored.Provenance.RecordedByOwnerId.Should().Be(node.OwnerId);
        stored.Provenance.Attendance.Should().Be(HumanAttendance.Unobserved);

        IReadOnlyList<DecisionDetails> listed = await DecisionListCommand.QueryAsync(
            session, new DecisionListCommand.Settings { Project = project.Name }, node.OwnerId, CancellationToken.None);
        listed.Select(row => row.Id).Should().Equal(recorded.Id);

        string shown = await ScopedAnsiConsoleCapture.CaptureAsync(
            () => DecisionShowCommand.WriteAsync(session, stored, CancellationToken.None));
        shown.Should().Contain("Agents never push");
        shown.Should().Contain("none recorded", "the absent run and task read as absences, not blanks");
        shown.Should().Contain("not observed either way");
    }

    [Fact]
    public async Task An_owner_scoped_lesson_lists_under_owner_and_not_under_the_project()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "Prefer a fake over a real process", Owner = true },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "Docker before dotnet test", Project = project.Name },
            node.OwnerId, node.NodeId, Now.AddMinutes(1), CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        IReadOnlyList<LearningDetails> mine = await LearningListCommand.QueryAsync(
            session, new LearningListCommand.Settings { Owner = true }, node.OwnerId, CancellationToken.None);
        mine.Select(row => row.Statement).Should().Equal("Prefer a fake over a real process");

        IReadOnlyList<LearningDetails> theirs = await LearningListCommand.QueryAsync(
            session, new LearningListCommand.Settings { Project = project.Name }, node.OwnerId, CancellationToken.None);
        theirs.Select(row => row.Statement).Should().Equal("Docker before dotnet test");

        IReadOnlyList<LearningDetails> everything = await LearningListCommand.QueryAsync(
            session, new LearningListCommand.Settings(), node.OwnerId, CancellationToken.None);
        everything.Should().HaveCount(2, "with neither flag, every scope is listed");
        everything[0].RecordedAt.Should().BeAfter(everything[1].RecordedAt, "newest first");
    }

    [Fact]
    public async Task Superseding_records_both_directions_and_drops_the_old_one_out_of_the_default_list()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (DecisionRecorded old, _) = await DecideCommand.RunAsync(
            session,
            new DecideCommand.Settings { Statement = "The old ruling", Project = project.Name },
            node.OwnerId, Now, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        (DecisionRecorded replacement, _) = await DecideCommand.RunAsync(
            session,
            new DecideCommand.Settings
            {
                Statement = "The ruling that holds now",
                Project = project.Name,
                Supersedes = [DomainId.Short(old.Id)],
            },
            node.OwnerId, Now.AddDays(1), CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        DecisionDetails supersededRow = (await session.LoadAsync<DecisionDetails>(old.Id, CancellationToken.None))!;
        supersededRow.Status.Should().Be(DecisionStatus.Superseded);
        supersededRow.SupersededByDecisionId.Should().Be(replacement.Id);
        supersededRow.SupersedeReason.Should().Contain(DomainId.Short(replacement.Id));

        DecisionDetails replacementRow =
            (await session.LoadAsync<DecisionDetails>(replacement.Id, CancellationToken.None))!;
        replacementRow.Supersedes.Should().Equal(old.Id);

        IReadOnlyList<DecisionDetails> binding = await DecisionListCommand.QueryAsync(
            session, new DecisionListCommand.Settings { Project = project.Name }, node.OwnerId, CancellationToken.None);
        binding.Select(row => row.Id).Should().Equal(replacement.Id);

        IReadOnlyList<DecisionDetails> everything = await DecisionListCommand.QueryAsync(
            session,
            new DecisionListCommand.Settings { Project = project.Name, All = true },
            node.OwnerId, CancellationToken.None);
        everything.Should().HaveCount(2, "nothing is deleted, only left out of the default view");

        string shown = await ScopedAnsiConsoleCapture.CaptureAsync(
            () => DecisionShowCommand.WriteAsync(session, supersededRow, CancellationToken.None));
        shown.Should().Contain("Replaced by");
        shown.Should().Contain("The ruling that holds now");
    }

    [Fact]
    public async Task The_standalone_supersede_verb_records_an_overruling_with_no_successor()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (DecisionRecorded old, _) = await DecideCommand.RunAsync(
            session,
            new DecideCommand.Settings { Statement = "The old ruling", Project = project.Name },
            node.OwnerId, Now, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        await DecisionSupersedeCommand.RunAsync(
            session,
            new DecisionSupersedeCommand.Settings
            {
                Decision = DomainId.Short(old.Id),
                Reason = "Overruled; the renumberer it served is gone",
            },
            node.OwnerId, Now.AddDays(2), CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        DecisionDetails row = (await session.LoadAsync<DecisionDetails>(old.Id, CancellationToken.None))!;
        row.Status.Should().Be(DecisionStatus.Superseded);
        row.SupersededByDecisionId.Should().BeNull();
        row.SupersedeReason.Should().Be("Overruled; the renumberer it served is gone");
        row.Statement.Should().Be("The old ruling");
    }

    [Fact]
    public async Task Retiring_a_lesson_keeps_it_readable_and_drops_it_out_of_the_default_list()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (LearningRecorded recorded, _) = await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "The old lesson", Project = project.Name },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        await LearningRetireCommand.RunAsync(
            session,
            new LearningRetireCommand.Settings
            {
                Learning = DomainId.Short(recorded.Id),
                Reason = "Graduated: the gate fails the build for it now",
            },
            node.OwnerId, Now.AddDays(5), CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        IReadOnlyList<LearningDetails> active = await LearningListCommand.QueryAsync(
            session, new LearningListCommand.Settings { Project = project.Name }, node.OwnerId, CancellationToken.None);
        active.Should().BeEmpty();

        IReadOnlyList<LearningDetails> everything = await LearningListCommand.QueryAsync(
            session,
            new LearningListCommand.Settings { Project = project.Name, All = true },
            node.OwnerId, CancellationToken.None);
        everything.Should().ContainSingle().Which.Status.Should().Be(LearningStatus.Retired);

        LearningDetails row = (await session.LoadAsync<LearningDetails>(recorded.Id, CancellationToken.None))!;
        string shown = await ScopedAnsiConsoleCapture.CaptureAsync(
            () => LearningShowCommand.WriteAsync(session, row, CancellationToken.None));
        shown.Should().Contain("The old lesson");
        shown.Should().Contain("retired");
        shown.Should().Contain("Graduated");
    }

    /// <summary>
    /// The half the whole attendance rule exists for: a dispatched agent naming its own task is
    /// refused a decision and pointed at the door that is open to it, while the identical call to
    /// <c>h9k learn</c> lands and carries that run and task as its provenance.
    /// </summary>
    [Fact]
    public async Task A_dispatched_run_is_refused_a_decision_and_records_a_lesson_instead()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);
        (Guid taskId, Guid runId) = await SeedDispatchedRunAsync(node, project, CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> decide = () => DecideCommand.RunAsync(
            session,
            new DecideCommand.Settings { Statement = "Agents rule now", Task = DomainId.Short(taskId) },
            node.OwnerId, Now, CancellationToken.None);

        (await decide.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*h9k learn*")
            .WithMessage("*unattended*");

        await using (IQuerySession fresh = _postgres.Store.QuerySession())
        {
            (await fresh.Query<DecisionDetails>().CountAsync(CancellationToken.None)).Should().Be(
                0, "the refusal lands before anything is queued, so the session that carries on is clean");
        }

        (LearningRecorded recorded, ResolvedKnowledgeScope scope) = await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "What this run learned", Task = DomainId.Short(taskId) },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        scope.ScopeId.Should().Be(project.Id, "the run's own project is the default scope");
        LearningDetails row = (await session.LoadAsync<LearningDetails>(recorded.Id, CancellationToken.None))!;
        row.Provenance!.RunId.Should().Be(runId);
        row.Provenance.TaskId.Should().Be(taskId);
        row.Provenance.Attendance.Should().Be(HumanAttendance.Unattended);
    }

    [Fact]
    public async Task An_operators_own_attended_claim_records_a_decision_carrying_that_run()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);
        (Guid taskId, Guid runId) = await SeedAttendedClaimAsync(node, project, CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (DecisionRecorded recorded, _) = await DecideCommand.RunAsync(
            session,
            new DecideCommand.Settings
            {
                Statement = "Recorded from an operator's own claim",
                Task = DomainId.Short(taskId),
            },
            node.OwnerId, Now, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        DecisionDetails row = (await session.LoadAsync<DecisionDetails>(recorded.Id, CancellationToken.None))!;
        row.Provenance!.RunId.Should().Be(runId);
        row.Provenance.TaskId.Should().Be(taskId);
        row.Provenance.Attendance.Should().Be(HumanAttendance.Attended);
        row.ScopeId.Should().Be(project.Id);
    }

    /// <summary>
    /// The travel rule, read where the outbound flush actually reads it: a project-scoped record
    /// resolves to its own project and rides that project's outbox, while an owner-scoped one
    /// resolves to no project at all — and a null never equals the project any outbox is
    /// flushing for, so it stays on the node that recorded it with no exclusion of its own.
    /// </summary>
    [Fact]
    public async Task A_project_scoped_record_resolves_to_its_project_and_an_owner_scoped_one_to_none()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (DecisionRecorded travels, _) = await DecideCommand.RunAsync(
            session,
            new DecideCommand.Settings { Statement = "A team rule", Project = project.Name },
            node.OwnerId, Now, CancellationToken.None);
        (LearningRecorded staysHome, _) = await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "A habit of mine", Owner = true },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        ReplicationProjectResolver resolver = new();
        (await resolver.ResolveAsync(session, travels.Id, CancellationToken.None)).ProjectId
            .Should().Be(project.Id);
        (await resolver.ResolveAsync(session, staysHome.Id, CancellationToken.None)).ProjectId
            .Should().BeNull();
    }

    [Fact]
    public async Task A_misspelled_superseded_id_refuses_before_anything_is_recorded()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => DecideCommand.RunAsync(
            session,
            new DecideCommand.Settings
            {
                Statement = "The ruling that holds now",
                Project = project.Name,
                Supersedes = ["deadbeef"],
            },
            node.OwnerId, Now, CancellationToken.None);

        await act.Should().ThrowAsync<DomainNotFoundException>();

        await using IQuerySession fresh = _postgres.Store.QuerySession();
        (await fresh.Query<DecisionDetails>().CountAsync(CancellationToken.None)).Should().Be(
            0, "a half-claimed replacement must never reach the store");
    }

    /// <summary>
    /// The same refusal on the other door (independent pre-PR review, cycle 1, both lenses): a
    /// full GUID parses without anything existing behind it, so a mistyped <c>--by</c> recorded a
    /// successor pointer at nothing, which <c>h9k decide show</c> then rendered as "not held on
    /// this node" — the honest label for a real decision this node has not replicated yet, not
    /// for a typo.
    /// </summary>
    [Fact]
    public async Task A_successor_id_naming_no_decision_refuses_before_the_supersession_is_recorded()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (DecisionRecorded recorded, _) = await DecideCommand.RunAsync(
            session,
            new DecideCommand.Settings { Statement = "The ruling that binds", Project = project.Name },
            node.OwnerId, Now, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        Func<Task> act = () => DecisionSupersedeCommand.RunAsync(
            session,
            new DecisionSupersedeCommand.Settings
            {
                Decision = recorded.Id.ToString(),
                Reason = "Something newer holds",
                By = DomainId.New().ToString(),
            },
            node.OwnerId, Now, CancellationToken.None);

        await act.Should().ThrowAsync<DomainNotFoundException>();

        await using IQuerySession fresh = _postgres.Store.QuerySession();
        DecisionDetails? untouched = await fresh.LoadAsync<DecisionDetails>(recorded.Id, CancellationToken.None);
        untouched!.Status.Should().Be(DecisionStatus.Recorded);
    }

    /// <summary>
    /// The render's own query seam (idea d805fd8b, piece 2), which the pure renderer tests cannot
    /// reach: what a project's <c>decisions.md</c> and <c>lessons.md</c> are allowed to carry.
    /// Only this project's own records, and specifically not an owner-scoped lesson, which is a
    /// cross-project habit belonging to the owner rather than to any one project's home — widening
    /// one into every project's file is backlog 55's prompt-injection work, and a wrong statement
    /// riding in every prompt everywhere is exactly the asymmetric failure scope exists to bound.
    /// </summary>
    [Fact]
    public async Task The_rendered_documents_carry_this_projects_own_records_and_nothing_elses()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);
        (_, ProjectDetails elsewhere) = await SeedProjectAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await DecideCommand.RunAsync(
            session,
            new DecideCommand.Settings { Statement = "This project decided this", Project = project.Name },
            node.OwnerId, Now, CancellationToken.None);
        await DecideCommand.RunAsync(
            session,
            new DecideCommand.Settings { Statement = "Another project decided that", Project = elsewhere.Name },
            node.OwnerId, Now, CancellationToken.None);
        await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "This project's runs learned this", Project = project.Name },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "A habit of mine everywhere", Owner = true },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        await using IQuerySession query = _postgres.Store.QuerySession();
        RenderedKnowledgeDocuments documents = await KnowledgeDocuments.RenderAsync(
            query, project.Id, CancellationToken.None);

        documents.Decisions.Should().Contain("This project decided this");
        documents.Decisions.Should().NotContain("Another project decided that");
        documents.Lessons.Should().Contain("This project's runs learned this");
        documents.Lessons.Should().NotContain("A habit of mine everywhere");
    }

    /// <summary>
    /// The prompt feed's own query seam (idea d805fd8b, piece 5), which the pure composer tests
    /// cannot reach: the section carries this project's active lessons PLUS this owner's, and
    /// neither another project's nor a retired one. This is where it deliberately differs from the
    /// rendered documents above, which are one project's file and leave an owner's cross-project
    /// habits out.
    /// </summary>
    [Fact]
    public async Task The_prompt_feed_carries_this_projects_active_lessons_and_this_owners()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);
        (_, ProjectDetails elsewhere) = await SeedProjectAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "This project's own lesson", Project = project.Name },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "Another project's lesson", Project = elsewhere.Name },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "A habit of mine everywhere", Owner = true },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        (LearningRecorded doomed, _) = await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "A lesson about to retire", Project = project.Name },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        await LearningRetireCommand.RunAsync(
            session,
            new LearningRetireCommand.Settings { Learning = doomed.Id.ToString(), Reason = "Wrong" },
            node.OwnerId, Now.AddMinutes(1), CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        await using IQuerySession query = _postgres.Store.QuerySession();
        InjectedLessons section = await LessonPromptFeed.ComposeAsync(
            query, project.Id, node.OwnerId, node.NodeId, LessonInjectionCaps.Default, CancellationToken.None);

        section.Lessons.Select(lesson => lesson.Statement).Should()
            .BeEquivalentTo(["This project's own lesson", "A habit of mine everywhere"]);
        section.ActiveInScope.Should().Be(2, "the retired one is not part of the active inventory");
    }

    /// <summary>
    /// The citation half of distillation through the command seam: <c>--distilled-from</c> resolves
    /// each source against the store, so a reference to nothing is refused by name here rather than
    /// recorded as a citation pointing nowhere, and the merge is readable back off the row.
    /// </summary>
    [Fact]
    public async Task A_distilled_lesson_cites_resolvable_sources_and_refuses_one_that_names_nothing()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (LearningRecorded first, _) = await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "Docker has to be up before the integration tier", Project = project.Name },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        (LearningRecorded second, _) = await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "The integration tier needs a container runtime", Project = project.Name },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        (LearningRecorded merged, _) = await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings
            {
                Statement = "The integration tier needs Docker running first",
                Project = project.Name,
                DistilledFrom = [DomainId.Short(first.Id), DomainId.Short(second.Id)],
            },
            node.OwnerId, node.NodeId, Now.AddMinutes(1), CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        await using IQuerySession query = _postgres.Store.QuerySession();
        LearningDetails row = (await query.LoadAsync<LearningDetails>(merged.Id, CancellationToken.None))!;
        row.DistilledFrom.Should().BeEquivalentTo([first.Id, second.Id]);

        Func<Task> namesNothing = async () => await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings
            {
                Statement = "A merge of something that is not there",
                Project = project.Name,
                DistilledFrom = ["zzzzzzzz"],
            },
            node.OwnerId, node.NodeId, Now.AddMinutes(2), CancellationToken.None);

        await namesNothing.Should().ThrowAsync<DomainNotFoundException>();

        // A well-formed id parses without ever touching the store, so the fragment case above
        // proves nothing about it: this is the one that ships a citation pointing at nothing if
        // the resolution skips the existence check (cycle-1 pre-PR review, adversarial lens).
        Func<Task> namesNoLesson = async () => await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings
            {
                Statement = "A merge citing an id nothing was ever recorded under",
                Project = project.Name,
                DistilledFrom = [DomainId.New().ToString()],
            },
            node.OwnerId, node.NodeId, Now.AddMinutes(3), CancellationToken.None);

        (await namesNoLesson.Should().ThrowAsync<DomainNotFoundException>())
            .WithMessage("*nothing to cite*");
    }

    /// <summary>
    /// The provenance mark's other half, through the real session rather than a fixture row: a
    /// lesson an agent run on this node records has to project THIS node, so it reaches a prompt.
    /// The inline projection reads the node off the event's own metadata, and Marten applies
    /// inline projections before it calls any session listener, so the origin stamping listener's
    /// pass lands too late for this row and the append has to carry the node itself
    /// (<see cref="EventRecordingNode.StampAtAppend"/>; cycle-1 pre-PR review, both lenses, where
    /// every agent-recorded lesson on this node projected a null node and was held out of every
    /// prompt).
    /// </summary>
    [Fact]
    public async Task A_lesson_a_run_on_this_node_records_projects_this_node_and_reaches_a_prompt()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);
        (Guid taskId, _) = await SeedDispatchedRunAsync(node, project, CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (LearningRecorded recorded, _) = await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings
            {
                Statement = "The gate's own filter excludes the docker tier",
                Task = DomainId.Short(taskId),
            },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        await using IQuerySession query = _postgres.Store.QuerySession();
        LearningDetails row = (await query.LoadAsync<LearningDetails>(recorded.Id, CancellationToken.None))!;
        row.RecordedOnNodeId.Should().Be(
            node.NodeId, "the row is what the prompt feed and h9k learn show both read the node off");

        LessonProvenanceMark mark = LessonProvenanceMark.Of(row.Provenance, row.RecordedOnNodeId, node.NodeId);
        mark.Should().Be(LessonProvenanceMark.AgentOnThisNode);

        InjectedLessons section = await LessonPromptFeed.ComposeAsync(
            query, project.Id, node.OwnerId, node.NodeId, LessonInjectionCaps.Default, CancellationToken.None);
        section.Lessons.Select(lesson => lesson.Statement).Should()
            .Contain("The gate's own filter excludes the docker tier");
        section.HeldForProvenance.Should().Be(0, "this node's own agent-recorded lesson is not held back");
    }

    /// <summary>
    /// The metadata the listener writes is unchanged by the append stamping above: the persisted
    /// event still carries this node's id, so everything that reads origin off <c>mt_events</c>
    /// (replication's outbox filter, the partial-stream repair planner) reads what it always did.
    /// </summary>
    [Fact]
    public async Task The_recorded_lesson_event_still_carries_this_nodes_origin_metadata()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (LearningRecorded recorded, _) = await LearnCommand.RunAsync(
            session,
            new LearnCommand.Settings { Statement = "A claim typed at a shell", Project = project.Name },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);

        await using IQuerySession query = _postgres.Store.QuerySession();
        IReadOnlyList<IEvent> events = await query.Events.FetchStreamAsync(
            recorded.Id, token: CancellationToken.None);
        events.Should().ContainSingle().Which
            .Headers![EventOriginStampingListener.NodeIdHeader].Should().Be(node.NodeId.ToString());
    }

    /// <summary>
    /// The import's own gate (idea d805fd8b, piece 3): a node with no replication switch-on point
    /// records nothing at all, because an event written before that point never rides an outbox
    /// and is never backfilled from elsewhere, so the whole rulebook would be stranded in one
    /// install's store with no id anybody else could cite.
    /// </summary>
    [Fact]
    public async Task The_import_is_refused_on_a_node_that_has_not_switched_replication_on()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> importing = () => DecisionImportCommand.RunAsync(
            session, new DecisionImportCommand.Settings { Project = project.Name },
            node.OwnerId, node.NodeId, Now, CancellationToken.None);

        await importing.Should().ThrowAsync<DomainValidationException>().WithMessage("*M2a*");

        await using IQuerySession query = _postgres.Store.QuerySession();
        (await query.Query<DecisionDetails>().CountAsync(CancellationToken.None)).Should().Be(0);
    }

    /// <summary>
    /// The import itself, end to end against a real store: every §16 entry and every AGENTS.md
    /// standing rule recorded once, each keeping the citation it already had, and the rendered
    /// document carrying that citation so the references already written across the repository
    /// land somewhere. Then the same call again, which must record nothing.
    /// </summary>
    [Fact]
    public async Task The_import_records_every_legacy_entry_once_and_a_second_run_records_nothing()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync(CancellationToken.None);
        await SwitchReplicationOnAsync(node, CancellationToken.None);

        int expected = LegacyDecisionsLogParser.Parse(LegacyKnowledgeSource.DecisionsLog()).Count
            + LegacyStandingRulesParser.Parse(LegacyKnowledgeSource.StandingRules()).Count;

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            LegacyImportOutcome first = await DecisionImportCommand.RunAsync(
                session, new DecisionImportCommand.Settings { Project = project.Name },
                node.OwnerId, node.NodeId, Now, CancellationToken.None);
            await session.SaveChangesAsync(CancellationToken.None);

            first.Recorded.Should().Be(expected);
            first.AlreadyImported.Should().Be(0);
            first.Retired.Should().Be(1, "§16 #162 is the one rule this change retired as it imported it");
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            LegacyImportOutcome second = await DecisionImportCommand.RunAsync(
                session, new DecisionImportCommand.Settings { Project = project.Name },
                node.OwnerId, node.NodeId, Now, CancellationToken.None);
            await session.SaveChangesAsync(CancellationToken.None);

            second.Recorded.Should().Be(0);
            second.AlreadyImported.Should().Be(expected);
            second.Retired.Should().Be(0, "a run that records nothing retires nothing either");
        }

        await using IQuerySession query = _postgres.Store.QuerySession();
        (await query.Query<DecisionDetails>().CountAsync(CancellationToken.None)).Should().Be(expected);

        DecisionDetails retired = await query.Query<DecisionDetails>()
            .SingleAsync(decision => decision.LegacyId == "Decisions Log #162", CancellationToken.None);
        retired.Status.Should().Be(DecisionStatus.Superseded);
        retired.SupersededByDecisionId.Should().BeNull();
        retired.SupersedeReason.Should().Contain("DecisionsLogNumberingGuardTests");

        // The citation's whole point, end to end: every spelling written across this repository
        // reaches the decision it names, the retired one included, through the resolver h9k decide
        // show and h9k decide supersede both use (independent pre-PR review, cycle 1, adversarial
        // lens — the store had no surface that answered a citation).
        (await DecisionIdResolver.ResolveAsync(query, "Decisions Log #162", CancellationToken.None))
            .Should().Be(retired.Id);
        (await DecisionIdResolver.ResolveAsync(query, "§16 #162", CancellationToken.None))
            .Should().Be(retired.Id);

        RenderedKnowledgeDocuments documents = await KnowledgeDocuments.RenderAsync(
            query, project.Id, CancellationToken.None);
        documents.Decisions.Should().Contain("(Decisions Log #62)");
        documents.Decisions.Should().Contain("(AGENTS.md Git rules #1)");
        documents.Decisions.Should().NotContain("## Decisions Log #162",
            "the retired rule is out of the rulebook the file carries");
        documents.Decisions.Should().Contain($"- Decisions Log #162 — now {DomainId.Short(retired.Id)}, superseded.",
            "and named at the foot of it, so a reader searching the file for that citation lands");
    }

    /// <summary>
    /// What the first outbound replication sweep would have recorded here, without running one:
    /// the import reads this node's own switch-on point and nothing else about replication.
    /// </summary>
    private async Task SwitchReplicationOnAsync(NodeContext node, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        NodeAggregate aggregate = (await session.Events.AggregateStreamAsync<NodeAggregate>(
            node.NodeId, token: cancellationToken))!;
        session.Events.Append(node.NodeId, NodeDecider.SwitchOnReplication(aggregate, 0, Now));
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<(NodeContext Node, ProjectDetails Project)> SeedProjectAsync(CancellationToken cancellationToken)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cancellationToken);

        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"knowledge-{projectId:N}", RepositoryPath, null, "main", Now);
        session.Events.StartStream<ProjectAggregate>(projectId, registered);
        await session.SaveChangesAsync(cancellationToken);

        return (node, (await session.LoadAsync<ProjectDetails>(projectId, cancellationToken))!);
    }

    /// <summary>An ordinary daemon dispatch: a real node holds the claim, and the session carries the build role.</summary>
    private Task<(Guid TaskId, Guid RunId)> SeedDispatchedRunAsync(
        NodeContext node, ProjectDetails project, CancellationToken cancellationToken) =>
        SeedClaimedRunAsync(node, project, node.NodeId, SessionRoleName.Build, cancellationToken);

    /// <summary>An operator's own h9k task work claim: the interactive sentinel, and the claim's own role name.</summary>
    private Task<(Guid TaskId, Guid RunId)> SeedAttendedClaimAsync(
        NodeContext node, ProjectDetails project, CancellationToken cancellationToken) =>
        SeedClaimedRunAsync(node, project, Guid.Empty, SessionRoleName.InteractiveClaim, cancellationToken);

    private async Task<(Guid TaskId, Guid RunId)> SeedClaimedRunAsync(
        NodeContext node,
        ProjectDetails project,
        Guid claimingNodeId,
        string role,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, project.Id, "Bound the limiter", ["the limiter resets per window"],
                TaskType.Feature, null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        TaskClaimed claimed = TaskDecider.Claim(task, claimingNodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);

        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, claimingNodeId, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
            Path.Combine(Path.GetTempPath(), $"hall9k-knowledge-wt-{runId:N}"), "task/limiter",
            ExecutorMode.Subscription, Now,
            SessionName: SessionRoleName.For(DomainId.Short(taskId), role)));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId);
    }
}
