using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Replication;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Task notes against a real store: what <c>h9k task note</c> records from a plain terminal, an attended
/// session and a dispatched one, what the task's projection and the prompt feed read back, how a
/// closing block is captured, and that a replicated note keeps its verified sender.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskNoteTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public TaskNoteTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_note_typed_at_a_plain_terminal_names_no_run_even_while_the_task_has_a_live_one()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync();
        (Guid taskId, _) = await SeedClaimedRunAsync(node, project, node.NodeId);

        TaskNoteRecorded recorded = await RecordAsync(node, taskId, "Force a rebuild first", _ => null);

        recorded.Provenance.RunId.Should().BeNull("a plain terminal names no run, whatever the task has live");
        recorded.Provenance.Attendance.Should().Be(HumanAttendance.Unobserved);
        recorded.SessionId.Should().BeNull();
        recorded.SessionName.Should().BeNull();
        recorded.Source.Should().Be(TaskNoteSource.Verb);
        TaskNoteView view = (await ViewsAsync(taskId)).Single();
        view.Mark.Should().Be(LessonProvenanceMark.NoRunNamed);
        view.Entry.RecordedOnNodeId.Should().Be(node.NodeId, "the append stamps the node, or every note would read as unobserved");
    }

    [Fact]
    public async Task A_dispatched_sessions_note_names_its_run_and_session_and_reads_as_this_nodes_own_agent()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync();
        (Guid taskId, Guid runId) = await SeedClaimedRunAsync(node, project, node.NodeId);
        Guid sessionId = DomainId.New();

        TaskNoteRecorded recorded = await RecordAsync(node, taskId, "The seam is in the loader", name => name switch
        {
            _ when name == DispatchedRunEnvironment.RunIdVariable => runId.ToString(),
            _ when name == DispatchedRunEnvironment.TaskIdVariable => taskId.ToString(),
            _ when name == DispatchedRunEnvironment.SessionIdVariable => sessionId.ToString(),
            _ when name == DispatchedRunEnvironment.SessionNameVariable => "abc12345-build",
            _ => null,
        });

        recorded.Provenance.RunId.Should().Be(runId);
        recorded.Provenance.TaskId.Should().Be(taskId);
        recorded.Provenance.Attendance.Should().Be(HumanAttendance.Unattended);
        recorded.SessionId.Should().Be(sessionId.ToString());
        recorded.SessionName.Should().Be("abc12345-build");
        (await ViewsAsync(taskId)).Single().Mark.Should().Be(LessonProvenanceMark.AgentOnThisNode);
    }

    [Fact]
    public async Task A_dispatched_session_is_refused_on_any_other_task_and_the_refusal_names_both()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync();
        (Guid ownTask, Guid runId) = await SeedClaimedRunAsync(node, project, node.NodeId);
        (Guid otherTask, _) = await SeedClaimedRunAsync(node, project, node.NodeId);

        Func<Task> act = () => RecordAsync(node, otherTask, "not mine", name => name switch
        {
            _ when name == DispatchedRunEnvironment.RunIdVariable => runId.ToString(),
            _ when name == DispatchedRunEnvironment.TaskIdVariable => ownTask.ToString(),
            _ => null,
        });

        string message = (await act.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message;
        message.Should().Contain(ownTask.ToString()).And.Contain(otherTask.ToString());
        (await ViewsAsync(otherTask)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_attended_sessions_note_names_the_attended_run()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync();
        (Guid taskId, Guid runId) = await SeedClaimedRunAsync(node, project, Guid.Empty);

        TaskNoteRecorded recorded = await RecordAsync(node, taskId, "Learned while driving", name =>
            name == InteractiveSessionLiveness.InteractiveRunEnvironmentVariable ? runId.ToString() : null);

        recorded.Provenance.RunId.Should().Be(runId, "an attended session names that session's run, never no run");
        recorded.Provenance.Attendance.Should().Be(HumanAttendance.Attended);
        recorded.SessionId.Should().BeNull("only a dispatched session records its session");
    }

    [Fact]
    public async Task A_session_that_registered_itself_is_recognised_by_its_process_with_no_run_variable_set()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync();
        (Guid taskId, Guid runId) = await SeedClaimedRunAsync(node, project, Guid.Empty);
        // The pasted-launch-line flow: the platform never spawned this session, so it carries no
        // HALL9K_INTERACTIVE_RUN_ID and is recognised by CLAUDE_PID matching the process it registered.
        using (System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess())
        {
            await using IDocumentSession session = _postgres.Store.LightweightSession();
            session.Events.Append(runId, new InteractiveSessionStarted(
                runId, DomainId.New(), self.StartTime.ToUniversalTime(), Environment.ProcessId,
                Environment.MachineName, "my-own-session"));
            await session.SaveChangesAsync();
        }

        TaskNoteRecorded fromThatSession = await RecordAsync(node, taskId, "Learned while driving", name =>
            name == InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable ? Environment.ProcessId.ToString() : null);
        TaskNoteRecorded fromAnotherProcess = await RecordAsync(node, taskId, "Typed elsewhere", name =>
            name == InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable ? (Environment.ProcessId + 1).ToString() : null);

        fromThatSession.Provenance.RunId.Should().Be(runId, "the registered session is attended and names its run");
        fromThatSession.Provenance.Attendance.Should().Be(HumanAttendance.Attended);
        fromAnotherProcess.Provenance.RunId.Should().BeNull("a process that is not the registered session names no run");
    }

    [Fact]
    public async Task A_note_can_be_left_on_a_task_in_any_state()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync();
        Guid draftId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(draftId, TaskDecider.Add(
                draftId, project.Id, "A draft", ["it exists"], TaskType.Feature, null, null, null, Now, node.OwnerId));
            await session.SaveChangesAsync();
        }

        await RecordAsync(node, draftId, "Even a draft takes a note", _ => null);

        (await ViewsAsync(draftId)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_task_reads_its_own_notes_then_its_epic_siblings_in_this_project_and_nobody_elses()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync();
        (_, ProjectDetails elsewhere) = await SeedProjectAsync();
        Guid epicId = DomainId.New();
        Guid own = await SeedTaskAsync(node, project, epicId);
        Guid sibling = await SeedTaskAsync(node, project, epicId);
        Guid inAnotherProject = await SeedTaskAsync(node, elsewhere, epicId);
        Guid outsideAnyEpic = await SeedTaskAsync(node, project, epicId: null);
        await RecordAsync(node, own, "own note", _ => null);
        await RecordAsync(node, sibling, "sibling note", _ => null);
        await RecordAsync(node, inAnotherProject, "other project note", _ => null);
        await RecordAsync(node, outsideAnyEpic, "loner note", _ => null);

        IReadOnlyList<TaskNoteView> views = await ViewsAsync(own);

        views.Select(view => view.Entry.Note.Text).Should().Equal("own note", "sibling note");
        views.Select(view => view.IsOwn).Should().Equal(true, false);
        views[1].TaskId.Should().Be(sibling);
        (await ViewsAsync(outsideAnyEpic)).Select(view => view.Entry.Note.Text).Should().Equal("loner note");

        await using IQuerySession query = _postgres.Store.QuerySession();
        InjectedTaskNotes? section = await TaskNoteFeed.LoadForPromptAsync(query, own, null, CancellationToken.None);
        section!.Notes[1].Line.Should().StartWith($"- task {DomainId.Short(sibling)}: sibling note");
    }

    [Fact]
    public async Task A_note_replicated_from_a_teammate_keeps_its_sender_and_origin_and_is_held_without_a_fleet()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync();
        Guid taskId = await SeedTaskAsync(node, project, epicId: null);
        Guid teammateNode = ForeignNoteFixtures.TeammateNode;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            StreamAction stream = session.Events.Append(taskId, new TaskNoteRecorded(
                taskId, DomainId.New(), "A teammate's note", RecordedProvenance.FromShell(DomainId.New()),
                TaskNoteSource.Verb, Now));
            IEvent @event = stream.Events[^1];
            @event.SetHeader(ReplicationEventHeaders.OriginEventId, Guid.NewGuid().ToString());
            @event.SetHeader(ReplicationEventHeaders.OriginNodeId, teammateNode.ToString());
            @event.SetHeader(ReplicationEventHeaders.ReceivedFromNodeId, teammateNode.ToString());
            await session.SaveChangesAsync();
        }

        TaskNoteView view = (await ViewsAsync(taskId)).Single();

        view.Entry.ReceivedFromNodeId.Should().Be(teammateNode);
        view.Entry.OriginNodeId.Should().Be(teammateNode);
        view.Entry.RecordedOnNodeId.Should().Be(teammateNode, "the origin the sender wrote, never this receiving node");
        view.Mark.Should().Be(LessonProvenanceMark.ReplicatedFromOutsideFleet);
        view.ReachesAPrompt.Should().BeFalse();
        await using IQuerySession query = _postgres.Store.QuerySession();
        InjectedTaskNotes? section = await TaskNoteFeed.LoadForPromptAsync(query, taskId, null, CancellationToken.None);
        section!.Any.Should().BeFalse();
        section.HeldForProvenance.Should().Be(1);
    }

    [Theory]
    [InlineData("Feature", true)]
    [InlineData("Spike", false)]
    public async Task A_spike_gets_no_notes_section_at_all_so_no_composing_site_asks_for_a_block_nothing_records(
        string type, bool carriesSection)
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync();
        Guid taskId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, project.Id, "A spike", ["an answer"], type == "Spike" ? TaskType.Spike : TaskType.Feature,
                null, null, null, Now, node.OwnerId, spikeKind: type == "Spike" ? SpikeKind.Research : null,
                exitCriterion: type == "Spike" ? "an answer" : null));
            await session.SaveChangesAsync();
        }

        await using IQuerySession query = _postgres.Store.QuerySession();
        InjectedTaskNotes? section = await TaskNoteFeed.LoadForPromptAsync(query, taskId, null, CancellationToken.None);

        (section is not null).Should().Be(carriesSection);
    }

    [Fact]
    public async Task A_closing_block_records_one_note_per_bullet_once_and_hands_back_the_message_without_it()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync();
        (Guid taskId, Guid runId) = await SeedClaimedRunAsync(node, project, node.NodeId);
        string message = $"Summary.\n\n{Hall9k.Connectors.Prompts.TaskNotesBlock.Marker}\n- first\n- second\n\nHANDOFF:\nDone.";

        string? clean = await TaskNoteCapture.ExtractAndRecordAsync(
            _postgres.Store, NullLogger.Instance, node.NodeId, runId, taskId, message, CancellationToken.None);
        string? again = await TaskNoteCapture.ExtractAndRecordAsync(
            _postgres.Store, NullLogger.Instance, node.NodeId, runId, taskId, message, CancellationToken.None);

        clean.Should().Be("Summary.\n\nHANDOFF:\nDone.");
        again.Should().Be(clean);
        IReadOnlyList<TaskNoteView> views = await ViewsAsync(taskId);
        views.Select(view => view.Entry.Note.Text).Should().BeEquivalentTo(["first", "second"], "reading it twice records each once");
        views.Should().OnlyContain(view => view.Entry.Note.Source == TaskNoteSource.ClosingBlock);
        views.Should().OnlyContain(view => view.Entry.Note.Provenance.RunId == runId);
        views.Should().OnlyContain(view => view.Mark == LessonProvenanceMark.AgentOnThisNode);
    }

    [Theory]
    [InlineData("PrReview")]
    [InlineData("Spike")]
    public async Task A_review_or_spike_sessions_final_message_records_nothing_and_loses_no_line(string type)
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, project.Id, "A review", ["it reviews"], type == "Spike" ? TaskType.Spike : TaskType.PrReview,
                null, null, null, Now, node.OwnerId, spikeKind: type == "Spike" ? SpikeKind.Research : null,
                exitCriterion: type == "Spike" ? "an answer" : null));
            await session.SaveChangesAsync();
        }

        string message = $"A report that quotes the marker.\n{Hall9k.Connectors.Prompts.TaskNotesBlock.Marker}\n- looks like a note";
        string? result = await TaskNoteCapture.ExtractAndRecordAsync(
            _postgres.Store, NullLogger.Instance, node.NodeId, runId, taskId, message, CancellationToken.None);

        result.Should().BeSameAs(message);
        (await ViewsAsync(taskId)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_task_leaves_the_message_alone_and_a_block_with_no_bullets_is_still_removed()
    {
        (NodeContext node, ProjectDetails project) = await SeedProjectAsync();
        (Guid taskId, Guid runId) = await SeedClaimedRunAsync(node, project, node.NodeId);
        string message = $"Summary.\n{Hall9k.Connectors.Prompts.TaskNotesBlock.Marker}\n- a note";

        // A task with no stream: nothing to look up, so nothing is recorded and the message comes back as it was.
        string? unknownTask = await TaskNoteCapture.ExtractAndRecordAsync(
            _postgres.Store, NullLogger.Instance, node.NodeId, runId, DomainId.New(), message, CancellationToken.None);
        unknownTask.Should().Be(message);

        // A closing block that is only a marker records nothing and still leaves the message clean.
        string? emptyBlock = await TaskNoteCapture.ExtractAndRecordAsync(
            _postgres.Store, NullLogger.Instance, node.NodeId, runId, taskId,
            $"Summary.\n{Hall9k.Connectors.Prompts.TaskNotesBlock.Marker}", CancellationToken.None);
        emptyBlock.Should().Be("Summary.");
        (await ViewsAsync(taskId)).Should().BeEmpty();
    }

    private async Task<TaskNoteRecorded> RecordAsync(
        NodeContext node, Guid taskId, string text, Func<string, string?> environment)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskNoteRecorded recorded = await TaskNoteCommand.RunAsync(
            session, new TaskNoteCommand.Settings { Task = taskId.ToString(), Text = text },
            node.OwnerId, node.NodeId, environment, Now, CancellationToken.None);
        await session.SaveChangesAsync();
        return recorded;
    }

    private async Task<IReadOnlyList<TaskNoteView>> ViewsAsync(Guid taskId)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        // The reading node is whatever NodeDetails row matches this machine, which the bootstrap seeds.
        return await TaskNoteFeed.LoadViewsAsync(query, taskId, null, CancellationToken.None);
    }

    private async Task<(NodeContext Node, ProjectDetails Project)> SeedProjectAsync()
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, CancellationToken.None);
        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"notes-{projectId:N}", "/does/not/matter", null, "main", Now);
        session.Events.StartStream<ProjectAggregate>(projectId, registered);
        await session.SaveChangesAsync();
        return (node, (await session.LoadAsync<ProjectDetails>(projectId))!);
    }

    private async Task<Guid> SeedTaskAsync(NodeContext node, ProjectDetails project, Guid? epicId)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId, project.Id, $"Task {taskId:N}", ["it works"], TaskType.Feature, null, null, null, Now, node.OwnerId,
            epicId: epicId));
        await session.SaveChangesAsync();
        return taskId;
    }

    private async Task<(Guid TaskId, Guid RunId)> SeedClaimedRunAsync(
        NodeContext node, ProjectDetails project, Guid claimingNodeId)
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
            Path.Combine(Path.GetTempPath(), $"hall9k-notes-wt-{runId:N}"), "task/limiter",
            ExecutorMode.Subscription, Now,
            SessionName: SessionRoleName.For(DomainId.Short(taskId), claimingNodeId == Guid.Empty
                ? SessionRoleName.InteractiveClaim
                : SessionRoleName.Build)));
        await session.SaveChangesAsync();
        return (taskId, runId);
    }
}
