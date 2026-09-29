using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Daemon;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Learning.Queries;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using JasperFx.Events;
using Marten;
using Npgsql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The verified sender of a replicated fact, end to end against a real store (security review idea
/// 6be68ee2, prompt-builders findings 1 to 6): stamped the way <c>EventReplicationInbox</c> stamps an
/// applied event, projected by the Inline projections a prompt is built from, restored by the
/// backfills for rows written before the field existed, and readable off the stream for the
/// aggregates that are live-aggregated instead of projected.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ReplicatedSenderIntegrationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Sender = ForeignNoteFixtures.TeammateNode;

    private readonly ScopedTestHome _scopedHome = new();

    public void Dispose() => _scopedHome.Dispose();

    [Fact]
    public async Task A_retry_replicated_by_a_teammate_projects_its_sender_and_a_later_native_one_clears_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid taskId = await SeedTaskAsync(cts.Token);

        await AppendAsync(taskId, Retried(taskId, "theirs"), replicatedFrom: Sender, cts.Token);
        (await LoadTaskAsync(taskId, cts.Token)).RetryReceivedFromNodeId.Should().Be(Sender);

        await AppendAsync(taskId, Retried(taskId, "mine"), replicatedFrom: null, cts.Token);
        TaskDetails after = await LoadTaskAsync(taskId, cts.Token);
        after.RetryReason.Should().Be("mine");
        after.RetryReceivedFromNodeId.Should().BeNull();
    }

    /// <summary>
    /// A row written before the sender was projected has no key at all and reads as the local
    /// operator's own text, so the backfill has to catch it and restore the sender from the event.
    /// </summary>
    [Fact]
    public async Task A_task_row_written_before_the_sender_existed_is_rebuilt_with_the_sender_restored()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid taskId = await SeedTaskAsync(cts.Token);
        await AppendAsync(taskId, Retried(taskId, "theirs"), replicatedFrom: Sender, cts.Token);
        await AppendAsync(
            taskId, new TaskHandoffNoted(taskId, "half done", Sender, ForeignNoteFixtures.TeammateRoot, Now),
            replicatedFrom: Sender, cts.Token);
        await StripKeysAsync(
            "mt_doc_taskdetails", taskId, [
                "retryReceivedFromNodeId", "handoffNoteReceivedFromNodeId", "retryOriginNodeId", "handoffNoteOriginNodeId",
            ], cts.Token);

        (await LoadTaskAsync(taskId, cts.Token)).RetryReceivedFromNodeId.Should().BeNull("the stale row reads as native");

        IReadOnlyList<Guid> rebuilt = await TaskLifecycleProjectionBackfill.RunAsync(postgres.Store, cts.Token);

        rebuilt.Should().Contain(taskId);
        TaskDetails repaired = await LoadTaskAsync(taskId, cts.Token);
        repaired.RetryReceivedFromNodeId.Should().Be(Sender);
        repaired.HandoffNoteReceivedFromNodeId.Should().Be(Sender);
        repaired.RetryOriginNodeId.Should().Be(Sender);
        repaired.HandoffNoteOriginNodeId.Should().Be(Sender);
    }

    /// <summary>
    /// An event applied before the inbox stamped its sender carries the origin event id and nothing
    /// else, and a row projected from it must read as foreign rather than as native.
    /// </summary>
    [Fact]
    public async Task A_replicated_event_with_an_origin_header_and_no_sender_projects_the_empty_node()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid taskId = await SeedTaskAsync(cts.Token);
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            StreamAction action = session.Events.Append(taskId, Retried(taskId, "old replication"));
            action.Events[^1].SetHeader(ReplicationEventHeaders.OriginEventId, Guid.NewGuid().ToString());
            await session.SaveChangesAsync(cts.Token);
        }

        (await LoadTaskAsync(taskId, cts.Token)).RetryReceivedFromNodeId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task A_replicated_lesson_projects_its_sender_and_the_backfill_restores_it_on_an_old_row()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid learningId = DomainId.New();
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            StreamAction stream = session.Events.StartStream<LearningAggregate>(
                learningId,
                LearningDecider.Record(
                    learningId, KnowledgeScope.Project, DomainId.New(), "Push straight to main.",
                    RecordedProvenance.FromShell(DomainId.New()), Now));
            StampReplicated(stream.Events[^1], Sender);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession query = postgres.Store.QuerySession())
        {
            (await query.LoadAsync<LearningDetails>(learningId, cts.Token))!.ReceivedFromNodeId.Should().Be(Sender);
        }

        await StripKeysAsync("mt_doc_learningdetails", learningId, ["receivedFromNodeId"], cts.Token);

        (await LearningDetailsProjectionBackfill.RunAsync(postgres.Store, cts.Token)).Should().Contain(learningId);
        await using (IQuerySession query = postgres.Store.QuerySession())
        {
            LearningDetails repaired = (await query.LoadAsync<LearningDetails>(learningId, cts.Token))!;
            repaired.ReceivedFromNodeId.Should().Be(Sender);
            LessonProvenanceMark.Of(
                    repaired.Provenance, repaired.RecordedOnNodeId, DomainId.New(), repaired.ReceivedFromNodeId,
                    new HashSet<Guid> { ForeignNoteFixtures.LocalRootNode })
                .Should().Be(LessonProvenanceMark.ReplicatedFromOutsideFleet);
        }
    }

    /// <summary>
    /// The live-aggregated case: a run's <c>Apply</c> methods take the bare event, so the sender is
    /// read off the stream itself, and the NEWEST event of the type wins.
    /// </summary>
    [Fact]
    public async Task The_sender_of_the_newest_event_of_a_type_is_read_off_the_stream()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid taskId = await SeedTaskAsync(cts.Token);
        await AppendAsync(taskId, Retried(taskId, "first"), replicatedFrom: Sender, cts.Token);

        await using (IQuerySession query = postgres.Store.QuerySession())
        {
            ReplicatedFrom latest = await ReplicatedSender.OfLatestAsync<TaskRetried>(query, taskId, cts.Token);
            latest.SenderNodeId.Should().Be(Sender);
            latest.OriginNodeId.Should().Be(Sender);
            (await ReplicatedSender.OfLatestAsync<TaskHandedBack>(query, taskId, cts.Token))
                .Should().Be(default(ReplicatedFrom), "no such event on the stream reads as native");
        }

        await AppendAsync(taskId, Retried(taskId, "second"), replicatedFrom: null, cts.Token);

        await using (IQuerySession query = postgres.Store.QuerySession())
        {
            (await ReplicatedSender.OfLatestAsync<TaskRetried>(query, taskId, cts.Token))
                .Should().Be(default(ReplicatedFrom));
        }
    }

    /// <summary>
    /// A blocker's closeout summary carries the sender of the event that recorded it, so a dependent's
    /// prompt can fence a teammate's handoff and leave its own alone. Read off the stream, not a
    /// projected column, so a run row written before this change is judged the same way.
    /// </summary>
    [Fact]
    public async Task A_blockers_summary_carries_the_sender_of_the_event_that_recorded_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid ownerId = DomainId.New();
        Guid theirsId = await SeedClosedOutBlockerAsync(ownerId, "Their handoff.", replicatedFrom: Sender, cts.Token);
        Guid minesId = await SeedClosedOutBlockerAsync(ownerId, "My handoff.", replicatedFrom: null, cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        IReadOnlyList<BlockerHandoff> handoffs = await BlockerHandoffQuery.LoadAsync(query, [theirsId, minesId], cts.Token);

        handoffs[0].Summary.Should().Be("Their handoff.");
        handoffs[0].SummaryReceivedFromNodeId.Should().Be(Sender);
        handoffs[0].SummaryOriginNodeId.Should().Be(Sender);
        handoffs[1].Summary.Should().Be("My handoff.");
        handoffs[1].SummaryReceivedFromNodeId.Should().BeNull();
    }

    /// <summary>
    /// The feed reads the owner's fleet only when a lesson it loaded was replicated here, so a solo
    /// project never walks the ledger for a prompt; and when one was, a teammate's lesson is held
    /// while a lesson from another node of the same fleet reaches the prompt.
    /// </summary>
    [Fact]
    public async Task The_lesson_feed_reads_the_fleet_only_for_a_replicated_lesson_and_holds_a_teammates()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid thisNode = DomainId.New();
        RecordedProvenance fromARun(Guid _) => new(ownerId, DomainId.New(), DomainId.New(), HumanAttendance.Unattended);
        await RecordLessonAsync(projectId, "Recorded here.", fromARun(thisNode), replicatedFrom: null, thisNode, cts.Token);

        int reads = 0;
        LocalFleetReader reader = _ =>
        {
            reads++;
            return Task.FromResult<IReadOnlySet<Guid>?>(new HashSet<Guid>
            {
                ForeignNoteFixtures.LocalRootNode, ForeignNoteFixtures.LocalSecondNode,
            });
        };

        await using (IQuerySession query = postgres.Store.QuerySession())
        {
            InjectedLessons native = await LessonPromptFeed.ComposeAsync(
                query, projectId, ownerId, thisNode, LessonInjectionCaps.Default, reader, cts.Token);
            native.Lessons.Should().ContainSingle();
            reads.Should().Be(0, "a project whose lessons were all recorded here needs no fleet");
        }

        await RecordLessonAsync(
            projectId, "From my other machine.", fromARun(thisNode), ForeignNoteFixtures.LocalSecondNode, thisNode,
            cts.Token);
        await RecordLessonAsync(
            projectId, "From a teammate, naming no run.", RecordedProvenance.FromShell(ownerId),
            ForeignNoteFixtures.TeammateNode, thisNode, cts.Token);

        await using (IQuerySession query = postgres.Store.QuerySession())
        {
            InjectedLessons section = await LessonPromptFeed.ComposeAsync(
                query, projectId, ownerId, thisNode, LessonInjectionCaps.Default, reader, cts.Token);

            reads.Should().Be(1);
            section.Lessons.Select(lesson => lesson.Statement).Should()
                .BeEquivalentTo(["Recorded here.", "From my other machine."]);
            section.HeldForProvenanceByMark.Should().Equal(
                new HeldLessonCount(LessonProvenanceMark.ReplicatedFromOutsideFleet, 1));
        }
    }

    /// <summary>
    /// The daemon's fleet: the snapshot the message sweep already keeps when there is one (no ledger
    /// read at all), and a ledger read of its own on a miss, where an owner whose root fingerprint
    /// is not known yields no fleet, which fences.
    /// </summary>
    [Fact]
    public async Task The_daemons_fleet_comes_from_the_sweeps_snapshot_and_reads_the_ledger_only_on_a_miss()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(postgres.Store, cts.Token);
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Store(new ProjectDetails { Id = projectId, Name = "hall9k", RepositoryPath = "/repos/hall9k" });
            session.Store(new OwnerDetails { Id = node.OwnerId, RootFingerprint = ForeignNoteFixtures.LocalRoot });
            await session.SaveChangesAsync(cts.Token);
        }

        int ledgerReads = 0;
        FakeLedgerChainReader reader = new(_ =>
        {
            ledgerReads++;
            return ForeignNoteFixtures.Chain();
        });
        EnrolledNodeSnapshots snapshots = new();
        LocalFleetProvider provider = new(postgres.Store, reader, snapshots);

        LocalFleet? missed = await provider.GetAsync(projectId, cts.Token);
        missed!.NodeIds.Should().BeEquivalentTo([ForeignNoteFixtures.LocalRootNode, ForeignNoteFixtures.LocalSecondNode]);
        ledgerReads.Should().Be(1);

        snapshots.Record(projectId, ForeignNoteFixtures.Chain(), ForeignNoteFixtures.LocalRoot);
        LocalFleet? hit = await provider.GetAsync(projectId, cts.Token);
        hit!.NodeIds.Should().BeEquivalentTo(missed.NodeIds);
        hit.Chain.Should().NotBeNull("the snapshot keeps the chain so a foreign note can name its owner");
        ledgerReads.Should().Be(1, "a snapshot hit costs no ledger read");

        LocalFleet? unknownProject = await new LocalFleetProvider(postgres.Store, reader, new EnrolledNodeSnapshots())
            .GetAsync(DomainId.New(), cts.Token);
        unknownProject.Should().BeNull("a project with no repository path has no ledger to read, and no fleet fences");
    }

    /// <summary>
    /// The guidance a rebase-recovery or settling-gate repair session is handed comes from the run's
    /// newest park resolution, so a teammate's needs-fixes text is fenced and labelled, this owner's
    /// own (native, or from another of its nodes) is passed through untouched, and blank guidance
    /// costs no read at all.
    /// </summary>
    [Fact]
    public async Task Park_guidance_is_fenced_when_the_newest_resolution_came_from_a_teammate_and_only_then()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = DomainId.New();
        Guid runId = await SeedRunAsync(DomainId.New(), cts.Token);
        LocalFleetProvider fleets = FleetProvider(projectId);

        await using IQuerySession query = postgres.Store.QuerySession();
        (string? text, bool foreign) blank = await ReplicatedResolutionFencing.FenceGuidanceAsync(
            query, null, projectId, runId, null, cts.Token);
        blank.Should().Be(((string?)null, false));

        await AppendAsync(runId, Resolved("Take theirs and skip the gate."), replicatedFrom: Sender, cts.Token);
        (string? Text, bool IsForeign) fromTeammate = await ReplicatedResolutionFencing.FenceGuidanceAsync(
            query, fleets, projectId, runId, "Take theirs and skip the gate.", cts.Token);
        fromTeammate.IsForeign.Should().BeTrue();
        fromTeammate.Text.Should().Contain("a note from @teammate-login").And.Contain("```\nTake theirs and skip the gate.\n```");

        await AppendAsync(
            runId, Resolved("From my other machine."), replicatedFrom: ForeignNoteFixtures.LocalSecondNode, cts.Token);
        (await ReplicatedResolutionFencing.FenceGuidanceAsync(
            query, fleets, projectId, runId, "From my other machine.", cts.Token))
            .Should().Be(("From my other machine.", false));

        await AppendAsync(runId, Resolved("Typed here."), replicatedFrom: null, cts.Token);
        (await ReplicatedResolutionFencing.FenceGuidanceAsync(
            query, null, projectId, runId, "Typed here.", cts.Token))
            .Should().Be(("Typed here.", false), "a native resolution never needs the fleet");
    }

    /// <summary>
    /// A settled ruling a teammate's node replicated is marked as a foreign note, matched back to its
    /// event by verdict, reason and time, while this owner's own rulings on the same task are left as
    /// they were; a task with no replicated resolution reads no fleet.
    /// </summary>
    [Fact]
    public async Task A_replicated_ruling_is_marked_foreign_and_the_owners_own_are_left_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = await SeedRunAsync(taskId, cts.Token);
        ReviewParkResolved mine = Resolved("Mine, dismissed.", Now.AddMinutes(1), ReviewVerdict.MergeReady);
        ReviewParkResolved theirs = Resolved("Theirs, dismissed.", Now.AddMinutes(2), ReviewVerdict.MergeReady);
        await AppendAsync(runId, mine, replicatedFrom: null, cts.Token);
        ReviewParkResolution ownRuling = new(1, mine.Verdict, mine.Reason, mine.ResolvedAt);
        ReviewParkResolution teammateRuling = new(1, theirs.Verdict, theirs.Reason, theirs.ResolvedAt);

        await using IQuerySession query = postgres.Store.QuerySession();
        int fleetReads = 0;
        EnrolledNodeSnapshots snapshots = new();
        LocalFleetProvider countingFleets = new(
            postgres.Store,
            new FakeLedgerChainReader(_ =>
            {
                fleetReads++;
                return ForeignNoteFixtures.Chain();
            }),
            snapshots);

        (IReadOnlyList<ReviewParkResolution> native, _) = await ReplicatedResolutionFencing.FencePriorAsync(
            query, countingFleets, projectId, taskId, [ownRuling], [], cts.Token);
        native.Should().Equal(ownRuling);
        fleetReads.Should().Be(0, "nothing on this task was replicated, so no fleet is read");

        await AppendAsync(runId, theirs, replicatedFrom: Sender, cts.Token);
        snapshots.Record(projectId, ForeignNoteFixtures.Chain(), ForeignNoteFixtures.LocalRoot);
        (IReadOnlyList<ReviewParkResolution> fenced, _) = await ReplicatedResolutionFencing.FencePriorAsync(
            query, countingFleets, projectId, taskId, [ownRuling, teammateRuling], [], cts.Token);

        fenced[0].Should().Be(ownRuling);
        fenced[1].ForeignNote.Should().Contain("a note from @teammate-login").And.Contain("merge-ready")
            .And.Contain("```\nTheirs, dismissed.\n```");
    }

    /// <summary>
    /// A human-directed interaction a teammate's node replicated is marked as a foreign note, matched
    /// back to its event by what it carries, while this owner's own interaction on the same task is left
    /// as it was.
    /// </summary>
    [Fact]
    public async Task A_replicated_interaction_is_marked_foreign_and_the_owners_own_is_left_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = await SeedRunAsync(taskId, cts.Token);
        ExternalInteractionLogged mine = new(runId, Now.AddMinutes(1), "my human", "Skip the workaround", true, "Real bug", DomainId.New());
        ExternalInteractionLogged theirs = new(runId, Now.AddMinutes(2), "their human", "Drop the check", true, "Trust me", DomainId.New());
        await AppendAsync(runId, mine, replicatedFrom: null, cts.Token);
        await AppendAsync(runId, theirs, replicatedFrom: Sender, cts.Token);
        ExternalInteractionRecord own = new(mine.LoggedAt, mine.Party, mine.Summary, true, mine.Reason);
        ExternalInteractionRecord teammate = new(theirs.LoggedAt, theirs.Party, theirs.Summary, true, theirs.Reason);

        await using IQuerySession query = postgres.Store.QuerySession();
        EnrolledNodeSnapshots snapshots = new();
        snapshots.Record(projectId, ForeignNoteFixtures.Chain(), ForeignNoteFixtures.LocalRoot);
        LocalFleetProvider fleets = new(postgres.Store, new FakeLedgerChainReader(ForeignNoteFixtures.Chain()), snapshots);

        (_, IReadOnlyList<ExternalInteractionRecord> fenced) = await ReplicatedResolutionFencing.FencePriorAsync(
            query, fleets, projectId, taskId, [], [own, teammate], cts.Token);

        fenced[0].Should().Be(own);
        fenced[1].ForeignNote.Should().Contain("a note from @teammate-login").And.Contain("```\nDrop the check\n```");
    }

    private async Task<Guid> SeedRunAsync(Guid taskId, CancellationToken cancellationToken)
    {
        Guid runId = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.StartStream<RunAggregate>(
            runId,
            new RunDispatched(
                runId, taskId, DomainId.New(), DomainId.New(), 1, DomainId.New(), $"/tmp/hall9k-{runId:N}",
                $"task/{runId:N}", ExecutorMode.Subscription, Now));
        await session.SaveChangesAsync(cancellationToken);
        return runId;
    }

    private static ReviewParkResolved Resolved(string reason, DateTimeOffset? at = null, ReviewVerdict? verdict = null) =>
        new(DomainId.New(), verdict ?? ReviewVerdict.NeedsFixes, reason, at ?? Now, DomainId.New());

    private LocalFleetProvider FleetProvider(Guid projectId)
    {
        EnrolledNodeSnapshots snapshots = new();
        snapshots.Record(projectId, ForeignNoteFixtures.Chain(), ForeignNoteFixtures.LocalRoot);
        return new LocalFleetProvider(postgres.Store, new FakeLedgerChainReader(ForeignNoteFixtures.Chain()), snapshots);
    }

    private async Task RecordLessonAsync(
        Guid projectId, string statement, RecordedProvenance provenance, Guid? replicatedFrom, Guid thisNode,
        CancellationToken cancellationToken)
    {
        Guid learningId = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();
        StreamAction stream = session.Events.StartStream<LearningAggregate>(
            learningId, LearningDecider.Record(learningId, KnowledgeScope.Project, projectId, statement, provenance, Now));
        if (replicatedFrom is { } sender)
        {
            StampReplicated(stream.Events[^1], sender);
        }
        else
        {
            EventRecordingNode.StampAtAppend(stream, thisNode);
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid> SeedClosedOutBlockerAsync(
        Guid ownerId, string summary, Guid? replicatedFrom, CancellationToken cancellationToken)
    {
        Guid blockerId = DomainId.New();
        Guid runId = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(blockerId, TaskSeed.Dispatchable(
            TaskDecider.Add(
                blockerId, DomainId.New(), "Ship the schema", ["the migration applies"], TaskType.Chore,
                null, null, null, Now, ownerId),
            ownerId, Now));
        StreamAction run = session.Events.StartStream<RunAggregate>(
            runId,
            new RunDispatched(
                runId, blockerId, DomainId.New(), ownerId, 1, DomainId.New(), $"/tmp/hall9k-{runId:N}",
                $"task/{runId:N}", ExecutorMode.Subscription, Now),
            new PullRequestOpened(runId, "https://github.com/x/y/pull/42", 42, Now),
            new PullRequestMerged(runId, Now.AddHours(1), Now.AddHours(1)),
            new RunHandoffRecorded(runId, HandoffOutcome.Captured, summary, Now.AddHours(1)),
            new RunCompleted(runId, Now.AddHours(1)));
        if (replicatedFrom is { } sender)
        {
            StampReplicated(run.Events.Single(candidate => candidate.Data is RunHandoffRecorded), sender);
        }

        await session.SaveChangesAsync(cancellationToken);
        return blockerId;
    }

    private async Task<Guid> SeedTaskAsync(CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(
            taskId,
            TaskDecider.Add(
                taskId, DomainId.New(), "Do the thing", ["it is done"], TaskType.Chore, null, null, null, Now,
                DomainId.New()));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task AppendAsync(Guid streamId, object data, Guid? replicatedFrom, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        StreamAction action = session.Events.Append(streamId, data);
        if (replicatedFrom is { } sender)
        {
            StampReplicated(action.Events[^1], sender);
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Exactly the two headers <c>EventReplicationInbox</c> sets on an applied fact, before the save.</summary>
    private static void StampReplicated(IEvent @event, Guid sender)
    {
        @event.SetHeader(ReplicationEventHeaders.OriginEventId, Guid.NewGuid().ToString());
        @event.SetHeader(ReplicationEventHeaders.OriginNodeId, sender.ToString());
        @event.SetHeader(ReplicationEventHeaders.ReceivedFromNodeId, sender.ToString());
    }

    private static TaskRetried Retried(Guid taskId, string reason) =>
        new(taskId, null, "task/abc-do", reason, Now, DomainId.New());

    private async Task<TaskDetails> LoadTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = postgres.Store.QuerySession();
        return (await query.LoadAsync<TaskDetails>(taskId, cancellationToken))!;
    }

    private async Task StripKeysAsync(
        string table, Guid id, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (string key in keys)
        {
            await using NpgsqlCommand command = new($"update {table} set data = data - @key where id = @id", connection);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("id", id);
            (await command.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }
    }
}
