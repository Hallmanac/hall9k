using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.JiraWrites;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The acceptance criterion this daemon sweep is built around (Brian's design, 2026-08-28): an
/// expired or missing twg login is a handled, expected state, and the identical payload succeeds
/// on retry once <c>twg login</c> runs, rather than being lost. Nothing exercised
/// <see cref="JiraWriteRetryEngine.PollOnceAsync"/> before this (independent pre-PR review, cycle
/// 1) — <c>TwgJiraExecutorTests</c> covers only argument construction and failure classification
/// against a fake process, and <c>TaskJiraWriteTests</c> covers only the decider and projections
/// in isolation — so a regression in the sweep's own filter, its payload round-trip, or which
/// write id an outcome gets recorded against could ship green. Against a real Postgres store, the
/// same pattern <c>BacklogTrackingTests</c> and <c>CardPublicationEngineTests</c> already use for
/// this class of daemon sweep.
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class JiraWriteRetryEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_auth_stuck_write_succeeds_on_retry_once_the_sweep_reattempts_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-123"), cts.Token);

        RecordingProcessRunner refusingTwg = RecordingProcessRunner.TwgAuthExpired();
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult submitted = await JiraWriteCoordinator.SubmitAsync(
                session, taskId, JiraWriteOperation.Comment, issueKey: null,
                new JiraWritePayload(null, null, "The pull request merged."), JiraProjectKey.None,
                DomainId.New(), new TwgJiraExecutor(refusingTwg.Runner), "/repo", cts.Token);

            submitted.Outcome.Should().Be(JiraWriteOutcome.PendingAuthentication);
        }

        TaskAggregate? stuck = await LoadAsync(store, taskId, cts.Token);
        stuck!.PendingJiraWriteIsAuthFailure.Should().BeTrue("the payload has to survive to be retried, not be lost");

        // A second node's twg, freshly logged in — the identical comment goes through this time.
        RecordingProcessRunner reauthenticatedTwg = RecordingProcessRunner.RespondingTo(
            _ => new ProcessResult(0, """{"key":"PROJ-123"}""", string.Empty));
        NodeContext node = await NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, reauthenticatedTwg.Runner, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Should().Be(new JiraWriteRetrySweepResult(Retried: 1, Succeeded: 1));
        TaskAggregate? resolved = await LoadAsync(store, taskId, cts.Token);
        resolved!.PendingJiraWriteId.Should().BeNull("the retry finished the request it already made rather than losing it");
    }

    [Fact]
    public async Task A_failure_that_is_not_about_authentication_is_left_for_a_freshly_composed_write()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-456"), cts.Token);

        RecordingProcessRunner refusingTwg = RecordingProcessRunner.Failing("field 'customfield_10010' is required");
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult submitted = await JiraWriteCoordinator.SubmitAsync(
                session, taskId, JiraWriteOperation.Comment, issueKey: null,
                new JiraWritePayload(null, null, "The pull request merged."), JiraProjectKey.None,
                DomainId.New(), new TwgJiraExecutor(refusingTwg.Runner), "/repo", cts.Token);

            submitted.Outcome.Should().Be(JiraWriteOutcome.Failed);
        }

        RecordingProcessRunner mustNotRun = RecordingProcessRunner.RespondingTo(
            _ => throw new InvalidOperationException("a non-auth failure must not be retried by the sweep"));
        NodeContext node = await NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, mustNotRun.Runner, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Should().Be(new JiraWriteRetrySweepResult(Retried: 0, Succeeded: 0));
    }

    [Fact]
    public async Task A_queued_merge_notice_waits_behind_an_outstanding_write_then_drains_once_it_clears()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-789"), cts.Token);

        // Closeout tried to submit the merge comment while another write was still outstanding on
        // this task and queued it instead of losing it (CloseoutEngine.QueueJiraMergeNoticeAsync).
        RecordingProcessRunner refusingTwg = RecordingProcessRunner.TwgAuthExpired();
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult submitted = await JiraWriteCoordinator.SubmitAsync(
                session, taskId, JiraWriteOperation.Comment, issueKey: null,
                new JiraWritePayload(null, null, "An earlier comment."), JiraProjectKey.None,
                DomainId.New(), new TwgJiraExecutor(refusingTwg.Runner), "/repo", cts.Token);
            submitted.Outcome.Should().Be(JiraWriteOutcome.PendingAuthentication);

            TaskAggregate outstanding = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.QueueJiraMergeNotice(outstanding, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingProcessRunner reauthenticatedTwg = RecordingProcessRunner.RespondingTo(
            _ => new ProcessResult(0, """{"key":"PROJ-789"}""", string.Empty));
        NodeContext node = await NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, reauthenticatedTwg.Runner, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        // First sweep: the outstanding write clears, but the queued notice was read as still
        // blocked (PendingJiraWriteId was set at query time) and is not drained in the same pass.
        JiraWriteRetrySweepResult first = await engine.PollOnceAsync(cts.Token);
        first.Should().Be(new JiraWriteRetrySweepResult(Retried: 1, Succeeded: 1, MergeNoticesDrained: 0));

        TaskAggregate? afterFirst = await LoadAsync(store, taskId, cts.Token);
        afterFirst!.PendingJiraWriteId.Should().BeNull("the outstanding write finished");
        afterFirst.HasQueuedJiraMergeNotice.Should().BeTrue("nothing has attempted the notice yet");

        // Second sweep: nothing blocks the notice any more, so it drains.
        JiraWriteRetrySweepResult second = await engine.PollOnceAsync(cts.Token);
        second.Should().Be(new JiraWriteRetrySweepResult(Retried: 0, Succeeded: 0, MergeNoticesDrained: 1));

        TaskAggregate? afterSecond = await LoadAsync(store, taskId, cts.Token);
        afterSecond!.HasQueuedJiraMergeNotice.Should().BeFalse("the retry sweep drained it exactly once");
        afterSecond.PendingJiraWriteId.Should().BeNull("the merge comment itself went through");
    }

    /// <summary>
    /// The race independent pre-PR review, cycle 5 (conformance lens) found: a second Jira write —
    /// an operator's own <c>h9k task write-jira</c> — lands on the target task in the narrow window
    /// after <c>DrainMergeNoticeAsync</c>'s own guard has already read <c>PendingJiraWriteId</c> as
    /// null but before <c>SubmitAsync</c>'s own <c>FetchStreamStateAsync</c> fences the stream for
    /// its append; <see cref="JiraWriteRetryEngine.OnBeforeMergeNoticeSubmitAsync"/> is the seam
    /// this test uses to land the race exactly there, since nothing about this window is otherwise
    /// reachable without a second engine or process actually running concurrently.
    /// <c>TaskDecider.RequestJiraWrite</c>'s own "already has a write outstanding" guard then refuses
    /// the notice's own <c>SubmitAsync</c> call before its <c>JiraWriteRequested</c> ever reaches the
    /// stream, so nothing about this attempt was ever appended. A version-based discriminator used
    /// to stand in <c>DrainMergeNoticeAsync</c>'s own catch instead, and could not tell that apart
    /// from this attempt's own intent having committed — the racing write moves the stream version
    /// exactly as this attempt's own append would have, so the notice was marked attempted and
    /// dropped for a write it never got the chance to send; a later discriminator read
    /// <c>PendingJiraWriteId</c> after the failure instead, but a write merely being outstanding
    /// still does not say whose it is (independent pre-PR review, cycle 6) — this test's own
    /// twg process throws if it is ever called, since the fixed code must refuse before reaching it.
    /// </summary>
    [Fact]
    public async Task A_queued_merge_notice_survives_a_racing_write_that_beats_it_to_the_outstanding_write_guard()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        Guid targetTaskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-222"), cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate target = (await session.Events.AggregateStreamAsync<TaskAggregate>(targetTaskId, token: cts.Token))!;
            session.Events.Append(targetTaskId, TaskDecider.QueueJiraMergeNotice(target, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingProcessRunner mustNotRun = RecordingProcessRunner.RespondingTo(
            _ => throw new InvalidOperationException(
                "the notice's own submit must be refused before it ever reaches twg"));
        NodeContext node = await NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, mustNotRun.Runner, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance)
        {
            // Fires in the exact window DrainMergeNoticeAsync's own guard cannot see: it already
            // read PendingJiraWriteId as null, and SubmitAsync has not yet fetched the stream's own
            // fence, so this racing write — the same shape an operator's own write-jira landing in
            // that window would leave behind — commits between the two reads.
            OnBeforeMergeNoticeSubmitAsync = async ct =>
            {
                using IDocumentSession racingSession = store.LightweightSession();
                TaskAggregate target = (await racingSession.Events.AggregateStreamAsync<TaskAggregate>(targetTaskId, token: ct))!;
                racingSession.Events.Append(targetTaskId, TaskDecider.RequestJiraWrite(
                    target, JiraWriteOperation.Comment, issueKey: null, "{}", DomainId.New(), Now, DomainId.New()));
                await racingSession.SaveChangesAsync(ct);
            },
        };

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Should().Be(new JiraWriteRetrySweepResult(Retried: 0, Succeeded: 0, MergeNoticesDrained: 0));

        TaskAggregate? afterSweep = await LoadAsync(store, targetTaskId, cts.Token);
        afterSweep!.HasQueuedJiraMergeNotice.Should().BeTrue(
            "the notice was refused before it ever reached twg, so it must stay queued rather than be marked attempted and dropped");
        afterSweep.PendingJiraWriteId.Should().NotBeNull("the racing write is what is actually outstanding now, not the notice's own attempt");

        // This test's own racing write, and the notice it left genuinely queued, are deliberately
        // left unresolved above so the assertions could observe them — cleaned up here so neither
        // lingers for a later test's own sweep to pick up (JiraWriteRetryEngineTests shares one
        // Postgres fixture across every test in this class).
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate afterRace = (await session.Events.AggregateStreamAsync<TaskAggregate>(targetTaskId, token: cts.Token))!;
            session.Events.Append(targetTaskId, TaskDecider.RecordJiraWriteFailure(
                afterRace, afterRace.PendingJiraWriteId!.Value,
                "Test cleanup: ending the racing write so it does not leak into a later test.",
                isAuthFailure: false, Now));
            await session.SaveChangesAsync(cts.Token);

            TaskAggregate afterRaceCleared = (await session.Events.AggregateStreamAsync<TaskAggregate>(targetTaskId, token: cts.Token))!;
            session.Events.Append(targetTaskId, TaskDecider.RecordJiraMergeNoticeAttempted(afterRaceCleared, Now));
            await session.SaveChangesAsync(cts.Token);
        }
    }

    /// <summary>
    /// twg's own comment call can succeed while this write's own outcome still fails to record —
    /// JiraWriteCoordinator.AttemptAsync's own doc comment is why that failure is left to propagate
    /// rather than being swallowed into an ordinary JiraWriteFailed (a card that genuinely carries
    /// the comment must not be recorded as though the write never happened). But a queued merge
    /// notice retries itself automatically on the very next sweep once the pending marker clears,
    /// and a Comment write has no dedup gate the way a Create's own marker search does — so the
    /// drain has to mark the notice attempted itself rather than leaving it to be picked up again,
    /// or the identical comment goes out a second time with nobody watching (independent pre-PR
    /// review, cycle 4). The race is stood in for here by a second write ending the same pending
    /// write from underneath the first one's own outcome — the same "something committed before
    /// this failed" shape a genuine transient Postgres failure inside RecordSuccessAsync leaves
    /// behind, without needing to fault-inject Postgres itself.
    /// </summary>
    [Fact]
    public async Task A_merge_notice_drain_that_cannot_record_its_own_outcome_is_not_retried_automatically()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-654"), cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.QueueJiraMergeNotice(task, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments =>
        {
            if (arguments.Contains("get"))
            {
                // Twg's own comment call already succeeded by the time the mandatory read-back
                // runs; racing a second write outcome in here, before control returns to
                // AttemptAsync's own RecordSuccessAsync, reproduces "something committed after
                // twg's call but before this write's own success could be recorded" without
                // needing to fault-inject Postgres.
                using IDocumentSession racing = store.LightweightSession();
                TaskAggregate current = racing.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token)
                    .GetAwaiter().GetResult()!;
                racing.Events.Append(taskId, TaskDecider.RecordJiraWriteFailure(
                    current, current.PendingJiraWriteId!.Value, "Raced by another write.", isAuthFailure: false, Now));
                racing.SaveChangesAsync(cts.Token).GetAwaiter().GetResult();
                return new ProcessResult(0, """{"key":"PROJ-654"}""", string.Empty);
            }

            return new ProcessResult(0, "{}", string.Empty);
        });

        NodeContext node = await NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, twg.Runner, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.MergeNoticesDrained.Should().Be(1, "the notice is marked attempted even though its own outcome could not be recorded");
        TaskAggregate? after = await LoadAsync(store, taskId, cts.Token);
        after!.HasQueuedJiraMergeNotice.Should().BeFalse(
            "attempted exactly once — auto-retrying an unwatched comment risks posting it twice");

        RecordingProcessRunner mustNotRunAgain = RecordingProcessRunner.RespondingTo(
            _ => throw new InvalidOperationException("the notice must not be retried automatically once marked attempted"));
        JiraWriteRetryEngine secondEngine = new(
            store, node, mustNotRunAgain.Runner, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult second = await secondEngine.PollOnceAsync(cts.Token);

        second.MergeNoticesDrained.Should().Be(0, "there is nothing left queued for a later sweep to pick up");
    }

    /// <summary>
    /// Pins the discriminator <see cref="JiraWriteSubmissionException"/> exists to make, rather than
    /// the coincidental case above where the racing failure clears the marker back to null. Here
    /// the racing session, in the same transaction that fails this write's own pending marker,
    /// requests a fresh write of its own too — so <c>PendingJiraWriteId</c> reads non-null by the
    /// time <c>RecordSuccessAsync</c> tries to record this attempt's own outcome, exactly the shape
    /// a version-based or id-comparison discriminator would misread as "somebody else's write, stays
    /// queued" (independent pre-PR review, cycle 5's own committed fix). But twg's own call for
    /// <em>this</em> attempt already posted the comment by the time that race lands, so the notice
    /// must still be marked attempted or a later sweep re-posts the identical comment a second time.
    /// Reverting <c>JiraWriteRetryEngine.cs</c>'s own check back to
    /// <c>afterFailure?.PendingJiraWriteId is not null</c> fails this test while leaving every other
    /// test in this class green (independent pre-PR review, cycle 7).
    /// </summary>
    [Fact]
    public async Task A_merge_notice_drain_is_marked_attempted_even_when_a_fresh_write_races_in_behind_its_own_failed_outcome()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-987"), cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.QueueJiraMergeNotice(task, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingProcessRunner twg = RecordingProcessRunner.RespondingTo(arguments =>
        {
            if (arguments.Contains("get"))
            {
                // Twg's own comment call already succeeded by the time the mandatory read-back
                // runs; racing both a failure for this write and a fresh request in here, in the
                // same session, reproduces "our own outcome could not be recorded, and something
                // else is now outstanding too" without needing to fault-inject Postgres.
                using IDocumentSession racing = store.LightweightSession();
                TaskAggregate current = racing.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token)
                    .GetAwaiter().GetResult()!;
                racing.Events.Append(taskId, TaskDecider.RecordJiraWriteFailure(
                    current, current.PendingJiraWriteId!.Value, "Raced by another write.", isAuthFailure: false, Now));
                racing.SaveChangesAsync(cts.Token).GetAwaiter().GetResult();

                TaskAggregate cleared = racing.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token)
                    .GetAwaiter().GetResult()!;
                racing.Events.Append(taskId, TaskDecider.RequestJiraWrite(
                    cleared, JiraWriteOperation.Comment, issueKey: null, "{}", DomainId.New(), Now, DomainId.New()));
                racing.SaveChangesAsync(cts.Token).GetAwaiter().GetResult();

                return new ProcessResult(0, """{"key":"PROJ-987"}""", string.Empty);
            }

            return new ProcessResult(0, "{}", string.Empty);
        });

        NodeContext node = await NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, twg.Runner, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.MergeNoticesDrained.Should().Be(
            1, "twg's own call for this write succeeded, so the notice must not be left for a later sweep to re-post");
        TaskAggregate? after = await LoadAsync(store, taskId, cts.Token);
        after!.HasQueuedJiraMergeNotice.Should().BeFalse(
            "attempted exactly once even though a fresh write is now outstanding on the task");
        after.PendingJiraWriteId.Should().NotBeNull("the racing session's own fresh write is genuinely outstanding now");

        // The racing session's own fresh write is deliberately left outstanding above so the
        // assertions could observe it — cleaned up here so it does not leak into a later test's own
        // sweep (JiraWriteRetryEngineTests shares one Postgres fixture across every test).
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate afterRace = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.RecordJiraWriteFailure(
                afterRace, afterRace.PendingJiraWriteId!.Value,
                "Test cleanup: ending the racing write so it does not leak into a later test.",
                isAuthFailure: false, Now));
            await session.SaveChangesAsync(cts.Token);
        }
    }

    /// <summary>
    /// The physical dedup gate that protects a stuck <em>create</em> retry has no equivalent for
    /// the site-resolution guard covered above — this covers a different, previously untested
    /// branch instead: <c>JiraWriteCoordinator.RecordAlreadyLinkedAsync</c>, reached only from
    /// <c>RetryPendingAsync</c> when a create sits stuck on an expired login and the task acquires
    /// its external item some other way in the meantime (an operator's own <c>h9k task link-jira</c>,
    /// run because the login problem had not been noticed yet). The retry must not record that
    /// linked reference verbatim — it owes its own recorded outcome the identical read-back every
    /// other write's success gets, in case the card was since deleted or twg is not authenticated
    /// (independent pre-PR review, adversarial lens, cycle 3; verified fixed, cycle 4). Nothing
    /// exercised this path before (independent pre-PR review, cycle 4).
    /// </summary>
    [Fact]
    public async Task A_stuck_create_found_linked_by_another_route_is_confirmed_by_a_fresh_read_back()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        Guid taskId = await SeedTaskAsync(store, externalReference: null, cts.Token);

        RecordingProcessRunner stuckTwg = RecordingProcessRunner.TwgAuthExpired();
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult submitted = await JiraWriteCoordinator.SubmitAsync(
                session, taskId, JiraWriteOperation.Create, issueKey: null,
                new JiraWritePayload("Dev Task", new Dictionary<string, string> { ["summary"] = "Close me out" }, null),
                JiraProjectKey.Parse("PROJ"), DomainId.New(), new TwgJiraExecutor(stuckTwg.Runner), "/repo", cts.Token);
            submitted.Outcome.Should().Be(JiraWriteOutcome.PendingAuthentication);
        }

        // An operator's own h9k task link-jira, run because the auth problem had not been noticed
        // yet — the create is still pending, but the task now carries the reference some other way.
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.LinkWorkItem(
                task, new ExternalReference(WorkItemProvider.Jira, "PROJ-555"), "Found it", "To Do", Now, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingProcessRunner reauthenticatedTwg = RecordingProcessRunner.RespondingTo(
            _ => new ProcessResult(0, """{"key":"PROJ-555"}""", string.Empty));
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult? retried = await JiraWriteCoordinator.RetryPendingAsync(
                session, taskId, JiraProjectKey.Parse("PROJ"), new TwgJiraExecutor(reauthenticatedTwg.Runner), "/repo", cts.Token);

            retried.Should().NotBeNull();
            retried!.Outcome.Should().Be(JiraWriteOutcome.Succeeded);
            retried.IssueKey.Should().Be("PROJ-555", "the recorded outcome is what twg answered when read back, not the unverified link");
        }

        reauthenticatedTwg.Calls.Should().ContainSingle().Which.Arguments.Should().ContainInOrder("jira", "workitem", "get", "PROJ-555");
        TaskAggregate? resolved = await LoadAsync(store, taskId, cts.Token);
        resolved!.PendingJiraWriteId.Should().BeNull("the stuck create is resolved rather than left pending forever");
    }

    /// <summary>
    /// A write requested and never given an outcome — the shape a cancellation the coordinator's
    /// own recording grace could not outrun, or a harder process death, leaves behind (independent
    /// pre-PR review, cycle 1, both lenses). Appended directly through the decider rather than by
    /// letting a real attempt run and fail: the whole point of the fix under test is that nothing
    /// else in the platform ever gets the chance to record an outcome for this write, so a fake
    /// twg that fails or cancels here would only be testing the wrong path.
    /// </summary>
    [Fact]
    public async Task A_write_stuck_pending_past_the_ceiling_is_ended_on_the_clock_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-321"), cts.Token);
        Guid writeId;
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            writeId = DomainId.New();
            session.Events.Append(taskId, TaskDecider.RequestJiraWrite(
                task, JiraWriteOperation.Comment, "PROJ-321", "{}", writeId, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingProcessRunner mustNotRun = RecordingProcessRunner.RespondingTo(
            _ => throw new InvalidOperationException("the ceiling sweep must never call twg — it only ends a stale write"));
        NodeContext node = await NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(
            store, node, mustNotRun.Runner, Options.Create(new DaemonOptions { PendingJiraWriteCeiling = TimeSpan.Zero }),
            NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Should().Be(new JiraWriteRetrySweepResult(Retried: 0, Succeeded: 0, MergeNoticesDrained: 0, Expired: 1));
        TaskAggregate? ended = await LoadAsync(store, taskId, cts.Token);
        ended!.PendingJiraWriteId.Should().BeNull("the ceiling clears the wedge even though nothing ever attempted the write");
        ended.PendingJiraWriteIsAuthFailure.Should().BeFalse();
    }

    private static IOptions<DaemonOptions> DefaultOptions() => Options.Create(new DaemonOptions());

    private DocumentStore OpenStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static async Task<NodeContext> NewNodeAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        NodeContext node = new();
        await node.InitializeAsync(store, cancellationToken);
        return node;
    }

    private static async Task<TaskAggregate?> LoadAsync(IDocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        return await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
    }

    private static async Task<Guid> SeedTaskAsync(
        IDocumentStore store, ExternalReference? externalReference, CancellationToken cancellationToken)
    {
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        await using IDocumentSession ownerSession = store.LightweightSession();
        ownerSession.Events.StartStream<OwnerAggregate>(ownerId, OwnerDecider.Register(
            ownerId, "Brian Hall", "brian@hallmanac.com", Now));
        await ownerSession.SaveChangesAsync(cancellationToken);

        Guid connectionId = await EnsureJiraConnectionAsync(store, ownerId, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(projectId, ProjectDecider.Register(
            projectId, ownerId, connectionId, "hall9k", "/repos/hall9k.git",
            new Uri("https://github.com/Hallmanac/hall9k"), null, Now));

        session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId, projectId, "Comment on the linked Jira card", ["A comment is posted"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference, Now, ownerId));

        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    /// <summary>
    /// Once per database, not once per test: every test in this class shares the fixture's
    /// Postgres, the same reasoning <c>CloseoutEngineTests.SeedJiraConnectionAsync</c>'s own doc
    /// comment gives, and a second registered Jira connection is a state
    /// <see cref="WorkItemConnections.FindJiraConnectionAsync"/> refuses on purpose (nothing says
    /// which account a project uses) — which would make every test after the first assert a
    /// refusal instead of exercising what it actually means to test.
    /// </summary>
    private static async Task<Guid> EnsureJiraConnectionAsync(IDocumentStore store, Guid ownerId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        if (await WorkItemConnections.FindJiraConnectionAsync(session, cancellationToken) is { } existing)
        {
            return existing.Id;
        }

        Guid connectionId = DomainId.New();
        session.Events.StartStream<ConnectionAggregate>(connectionId, ConnectionDecider.Register(
            connectionId, ownerId, WorkItemProvider.Jira, "brian@hallmanac.com",
            CredentialReference.EnvironmentVariable("JIRA_TOKEN"), Now, new Uri("https://hall9k.atlassian.net")));
        await session.SaveChangesAsync(cancellationToken);
        return connectionId;
    }
}
