using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// A Linear issue behind the tracker-assignee claim gate: the read every claim door shares, the
/// take that writes this install's viewer id into an unassigned issue, the release that clears it,
/// and the dispatcher's mirror. Every Linear call here is answered by a
/// <see cref="RecordingLinearTracker"/>, so no test reaches a workspace, and the requests it
/// records are what pin the read-write-read sequence and the exact JSON a write sends.
/// <para>
/// The class's one Linear connection records <see cref="RecordingLinearTracker.ViewerId"/>, which is
/// the id every assertion compares against; a connection that records none is the other class,
/// <see cref="LinearClaimGateWithoutViewerIdTests"/>, because an install holds one Linear connection.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class LinearClaimGateTests : IClassFixture<PostgresFixture>, IDisposable
{
    private const string KeyVariable = "HALL9K_TEST_LINEAR_CLAIM_GATE_KEY";
    private static readonly Uri Workspace = new("https://linear.app/hall9k");
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly ExternalReference Issue = new(WorkItemProvider.Linear, "ENG-123");

    private readonly PostgresFixture postgres;
    private readonly List<string> repositoryPaths = [];

    public LinearClaimGateTests(PostgresFixture postgres)
    {
        this.postgres = postgres;
        Environment.SetEnvironmentVariable(KeyVariable, "lin_api_test_key");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KeyVariable, null);
        foreach (string path in repositoryPaths.Where(Directory.Exists))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    // ── the gate applies to a Linear issue ────────────────────────────────────────────────────

    [Fact]
    public void The_gate_admits_a_linear_reference_only_when_the_project_is_gated()
    {
        TrackerClaimGate.Gates(ClaimGate.TrackerAssignee, Issue).Should().BeTrue();
        TrackerClaimGate.Gates(ClaimGate.Off, Issue).Should().BeFalse();
    }

    [Fact]
    public async Task A_project_with_the_gate_off_makes_no_linear_call_to_decide_a_claim()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();

        TrackerClaimDecision decision = await Gate(tracker).CheckAsync(
            postgres.Store, ClaimGate.Off, Issue, Path.GetTempPath(), cts.Token);

        decision.Verdict.Should().Be(TrackerClaimVerdict.NotGated);
        tracker.Requests.Should().BeEmpty();
    }

    // ── the read ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_issue_assigned_to_the_viewer_id_passes_with_one_assignee_read_and_no_viewer_read()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.HeldBy(
            RecordingLinearTracker.ViewerId, RecordingLinearTracker.ViewerName);

        TrackerClaimDecision decision = await Gate(tracker).CheckAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        decision.Verdict.Should().Be(TrackerClaimVerdict.Assigned);
        decision.Assignee!.Identity.Should().Be(RecordingLinearTracker.ViewerId);
        decision.Identity.Should().Be(RecordingLinearTracker.ViewerId);

        RecordedLinearRequest read = tracker.Requests.Should().ContainSingle().Subject;
        read.Query.Should().Contain("assignee");
        read.Query.Should().NotContain("viewer", "the viewer id is the one the connection recorded, never read live");
        read.Query.Should().NotContain("delegate", "Linear's delegate field is neither read nor written");
    }

    [Fact]
    public async Task An_unassigned_issue_waits_and_names_linear_the_identifier_and_the_issue_url()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);

        TrackerClaimDecision decision = await Gate(RecordingLinearTracker.Unassigned()).CheckAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        decision.Verdict.Should().Be(TrackerClaimVerdict.Unassigned);
        decision.Tracker.Should().Be("Linear");
        decision.ItemUrl.Should().Be(new Uri("https://linear.app/hall9k/issue/ENG-123"));
        decision.RefusalLine.Should().Contain("Linear shows ENG-123 assigned to nobody");
        decision.RefusalLine.Should().Contain("Assign ENG-123 to yourself in the tracker");
        decision.RefusalLine.Should().Contain("https://linear.app/hall9k/issue/ENG-123");
        decision.RefusalLine.Should().NotContain("GitHub");
        decision.ReasonLine.Should().Contain("waiting for Linear to show ENG-123 assigned to you");
    }

    [Fact]
    public async Task An_issue_somebody_else_holds_waits_and_names_the_holder()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);

        TrackerClaimDecision decision = await Gate(
            RecordingLinearTracker.HeldBy(RecordingLinearTracker.OtherId, RecordingLinearTracker.OtherName))
            .CheckAsync(postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        decision.Verdict.Should().Be(TrackerClaimVerdict.HeldByOther);
        decision.Holder.Should().Be(RecordingLinearTracker.OtherName);
        decision.RefusalLine.Should().Contain("Linear shows ENG-123 assigned to Someone Else");
    }

    [Fact]
    public async Task The_comparison_is_on_the_user_id_ordinally_and_never_on_the_name()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);

        // The viewer's own name on somebody else's id is somebody else.
        TrackerClaimDecision sameNameOtherId = await Gate(
            RecordingLinearTracker.HeldBy(RecordingLinearTracker.OtherId, RecordingLinearTracker.ViewerName))
            .CheckAsync(postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);
        sameNameOtherId.Verdict.Should().Be(TrackerClaimVerdict.HeldByOther);

        // The viewer's id folded to another case is another id, as Jira's accountId is.
        TrackerClaimDecision foldedId = await Gate(
            RecordingLinearTracker.HeldBy(RecordingLinearTracker.ViewerId.ToUpperInvariant(), "Brian Hall"))
            .CheckAsync(postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);
        foldedId.Verdict.Should().Be(TrackerClaimVerdict.HeldByOther);

        // The viewer's id under any display name is the viewer.
        TrackerClaimDecision sameIdOtherName = await Gate(
            RecordingLinearTracker.HeldBy(RecordingLinearTracker.ViewerId, "a renamed display name"))
            .CheckAsync(postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);
        sameIdOtherName.Verdict.Should().Be(TrackerClaimVerdict.Assigned);
    }

    [Theory]
    [InlineData("rejected-401", TrackerReadFailure.Credentials)]
    [InlineData("rejected-graphql", TrackerReadFailure.Credentials)]
    [InlineData("http-429", TrackerReadFailure.Outage)]
    [InlineData("ratelimited-code-on-200", TrackerReadFailure.Outage)]
    [InlineData("http-503", TrackerReadFailure.Outage)]
    [InlineData("timeout", TrackerReadFailure.Outage)]
    [InlineData("unreachable", TrackerReadFailure.Outage)]
    [InlineData("not-found", TrackerReadFailure.Item)]
    [InlineData("other-graphql-error", TrackerReadFailure.Item)]
    public async Task A_read_failure_is_classified_keeps_linears_words_and_names_the_right_remedy(
        string shape, TrackerReadFailure expected)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();
        string linearsWords = shape switch
        {
            "rejected-401" => "Authentication required, not authenticated",
            "rejected-graphql" => "The API key is invalid",
            "http-429" => "Too many requests",
            "ratelimited-code-on-200" => "Rate limit exceeded",
            "http-503" => "Service unavailable",
            "not-found" => "Entity not found: Issue",
            _ => "Argument Validation Error",
        };
        tracker.Override = _ => shape switch
        {
            "rejected-401" => RecordingLinearTracker.Errors(linearsWords, "AUTHENTICATION_ERROR", 401),
            "rejected-graphql" => RecordingLinearTracker.Errors(linearsWords, "AUTHENTICATION_ERROR"),
            "http-429" => RecordingLinearTracker.Errors(linearsWords, "RATELIMITED", 429),
            "ratelimited-code-on-200" => RecordingLinearTracker.Errors(linearsWords, "RATELIMITED"),
            "http-503" => RecordingLinearTracker.Json("upstream: " + linearsWords, 503),
            "timeout" => throw new TaskCanceledException("the call timed out"),
            "unreachable" => throw new HttpRequestException("name resolution failed"),
            "not-found" => RecordingLinearTracker.Errors(linearsWords),
            _ => RecordingLinearTracker.Errors(linearsWords),
        };

        TrackerClaimDecision decision = await Gate(tracker).CheckAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        decision.Verdict.Should().Be(TrackerClaimVerdict.Unreadable);
        decision.Holds.Should().BeTrue();
        decision.AuthenticationRefusal.Should().Be(expected == TrackerReadFailure.Credentials);
        if (shape is not ("timeout" or "unreachable"))
        {
            decision.Error.Should().Contain(linearsWords, "the hold keeps Linear's own words");
        }

        switch (expected)
        {
            case TrackerReadFailure.Credentials:
                decision.Lever.Should().Contain("h9k connection add linear");
                break;
            case TrackerReadFailure.Outage:
                decision.Lever.Should().Contain("next sweep");
                break;
            default:
                decision.Lever.Should().Contain("its own words above").And.Contain("link-linear");
                decision.Lever.Should().NotContain(
                    "Nothing on this install can end the hold",
                    "the shared sentence that nothing on this install ends the hold is not true of a moved issue");
                decision.Lever.Should().NotContain("nothing on this install ends the hold");
                break;
        }
    }

    [Fact]
    public async Task A_not_found_issue_names_the_moved_team_relink()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();
        tracker.Override = _ => RecordingLinearTracker.Errors("Entity not found: Issue");

        TrackerClaimDecision decision = await Gate(tracker).CheckAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        decision.Error.Should().Contain("may have moved to another team").And.Contain("Entity not found: Issue");
        decision.Error.Should().Contain("h9k task link-linear");
    }

    [Fact]
    public async Task A_200_carrying_an_errors_array_is_never_read_as_an_assignment()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();
        tracker.Override = _ => RecordingLinearTracker.Json(
            "{\"errors\":[{\"message\":\"Partial failure\"}],\"data\":{\"issue\":{\"identifier\":\"ENG-123\","
            + $"\"assignee\":{{\"id\":\"{RecordingLinearTracker.ViewerId}\",\"name\":\"x\"}}}}}}");

        TrackerClaimDecision decision = await Gate(tracker).CheckAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        decision.Verdict.Should().Be(TrackerClaimVerdict.Unreadable);
        decision.Error.Should().Contain("Partial failure");
    }

    [Fact]
    public async Task An_answer_with_no_assignee_member_is_unreadable_rather_than_nobody()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();
        tracker.Override = _ => RecordingLinearTracker.Json("""{"data":{"issue":{"identifier":"ENG-123"}}}""");

        TrackerClaimDecision decision = await Gate(tracker).CheckAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        decision.Verdict.Should().Be(TrackerClaimVerdict.Unreadable);
    }

    [Fact]
    public async Task A_hold_round_trips_through_the_published_row_with_linear_as_the_tracker()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        TrackerClaimDecision decision = await Gate(
            RecordingLinearTracker.HeldBy(RecordingLinearTracker.OtherId, RecordingLinearTracker.OtherName))
            .CheckAsync(postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        TrackerClaimHold hold = decision.ToHold(DomainId.New(), DomainId.New(), "machine");
        TrackerClaimDecision.FromHold(hold).RefusalLine.Should().Be(decision.RefusalLine);
        TrackerClaimDecision.FromHold(hold).Tracker.Should().Be("Linear");
    }

    // ── the take ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_take_on_an_unassigned_issue_reads_writes_and_reads_back_touching_only_the_assignee()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();

        TrackerTake take = await Taker(tracker).TakeAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        take.Verdict.Should().Be(TrackerTakeVerdict.Taken);
        take.Wrote.Should().BeTrue();
        take.Assignee!.Identity.Should().Be(RecordingLinearTracker.ViewerId);
        take.TookLine.Should().Contain("Linear now shows ENG-123 assigned to");

        tracker.Requests.Select(request => request.IsWrite).Should().Equal(
            [false, true, false], "the take is a fresh read, one write, and a read back");

        RecordedLinearRequest write = tracker.Writes.Single();
        write.Variables.GetProperty("id").GetString().Should().Be("ENG-123");
        write.Variables.GetProperty("assigneeId").GetString().Should().Be(RecordingLinearTracker.ViewerId);
        write.Variables.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(["id", "assigneeId"]);
        write.Query.Should().Contain("input: { assigneeId: $assigneeId }", "the input carries the assignee and nothing else");
        write.Query.Should().NotContainAny("stateId", "labelIds", "delegate", "priority", "title");
    }

    [Fact]
    public async Task A_take_records_the_assignment_from_the_read_back_through_the_assign_door()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(postgres.Store, cts.Token);
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();
        Guid projectId = await SeedProjectAsync(ClaimGate.TrackerAssignee, cts.Token);
        Guid taskId = await SeedTaskAsync(projectId, node.OwnerId, dispatchable: false, cts.Token);

        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            OwnerDetails owner = (await session.LoadAsync<OwnerDetails>(node.OwnerId, cts.Token))!;
            await TaskAssignCommand.AppendAsync(session, task, owner, node.OwnerId, cts.Token);
            (TrackerTake? taken, _) = await TaskAssignCommand.TakeBeforeAssigningAsync(
                postgres.Store, session, task, take: true, Taker(tracker), offer: null, cts.Token);
            await session.SaveChangesAsync(cts.Token);
            taken!.Verdict.Should().Be(TrackerTakeVerdict.Taken);
        }

        List<object> events = await EventsAsync(taskId, cts.Token);
        TrackerAssignmentWritten written = events.OfType<TrackerAssignmentWritten>().Should().ContainSingle().Subject;
        written.AssigneeIdentity.Should().Be(RecordingLinearTracker.ViewerId);
        written.Reference.Should().Be("linear:ENG-123");
        events.OfType<TrackerAssignmentObserved>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_take_on_an_issue_somebody_else_holds_is_refused_and_nothing_is_written()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.HeldBy(
            RecordingLinearTracker.OtherId, RecordingLinearTracker.OtherName);

        TrackerTake take = await Taker(tracker).TakeAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        take.Verdict.Should().Be(TrackerTakeVerdict.HeldByOther);
        take.Passes.Should().BeFalse();
        take.RefusalLine.Should().Contain("Linear shows ENG-123 assigned to Someone Else");
        tracker.Writes.Should().BeEmpty();
        tracker.AssigneeId.Should().Be(RecordingLinearTracker.OtherId);
    }

    [Fact]
    public async Task A_take_on_an_issue_already_mine_proceeds_without_writing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.HeldBy(
            RecordingLinearTracker.ViewerId, RecordingLinearTracker.ViewerName);

        TrackerTake take = await Taker(tracker).TakeAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        take.Verdict.Should().Be(TrackerTakeVerdict.AlreadyMine);
        take.Passes.Should().BeTrue();
        take.Wrote.Should().BeFalse();
        tracker.Writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("success-false")]
    [InlineData("errors-array")]
    public async Task A_refused_write_keeps_linears_words_and_says_the_key_needs_write_permission(string shape)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();
        tracker.Override = request => !request.IsWrite
            ? null
            : shape == "success-false"
                ? RecordingLinearTracker.Json("""{"data":{"issueUpdate":{"success":false}}}""")
                : RecordingLinearTracker.Errors("You do not have permission to update this issue", "FORBIDDEN_TEAM");

        TrackerTake take = await Taker(tracker).TakeAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        take.Verdict.Should().Be(TrackerTakeVerdict.WriteRefused);
        take.Passes.Should().BeFalse();
        take.RefusalLine.Should().Contain("write permission on the issue's team");
        if (shape == "errors-array")
        {
            take.RefusalLine.Should().Contain("You do not have permission to update this issue");
        }

        tracker.Reads.Should().ContainSingle("a refused write is not followed by a read-back");
    }

    [Fact]
    public async Task A_write_that_does_not_land_is_unconfirmed_and_records_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();
        tracker.WriteLands = false;

        TrackerTake take = await Taker(tracker).TakeAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        take.Verdict.Should().Be(TrackerTakeVerdict.Unconfirmed);
        take.Wrote.Should().BeFalse();
    }

    [Fact]
    public async Task The_read_back_compares_the_user_id_so_a_different_id_is_unconfirmed_and_never_contested()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);

        // A racing install overwrote the single assignee between the write and the read-back: the
        // assignee is one user, so this install is simply not on the issue, and no Contested
        // verdict can be built from it.
        RecordingLinearTracker overwritten = RecordingLinearTracker.Unassigned();
        overwritten.WriteLands = false;
        int reads = 0;
        overwritten.Override = request => !request.IsWrite && ++reads == 2
            ? RecordingLinearTracker.IssueAssignedTo(RecordingLinearTracker.OtherId, RecordingLinearTracker.ViewerName)
            : null;

        TrackerTake take = await Taker(overwritten).TakeAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        take.Verdict.Should().Be(TrackerTakeVerdict.Unconfirmed, "the read-back shows the viewer's name on another user id");
        take.Verdict.Should().NotBe(TrackerTakeVerdict.Contested);
    }

    [Fact]
    public async Task An_unreadable_issue_is_not_taken_blind()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();
        tracker.Override = _ => RecordingLinearTracker.Errors("Entity not found: Issue");

        TrackerTake take = await Taker(tracker).TakeAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        take.Verdict.Should().Be(TrackerTakeVerdict.Unreadable);
        tracker.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task The_take_is_reachable_through_the_constructor_that_names_the_linear_requester_directly()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();
        TrackerAssignmentTake take = new(
            RecordingProcessRunner.NeverInvoked(), FakeJiraRequester.NeverInvoked(), linearRequester: tracker.Requester);

        (await take.TakeAsync(postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token))
            .Verdict.Should().Be(TrackerTakeVerdict.Taken);
        tracker.Writes.Should().ContainSingle();
    }

    // ── the cooperative grant ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("unassigned")]
    [InlineData("mine")]
    public async Task A_grant_moves_the_issue_to_the_grantee_id_from_unassigned_or_from_this_install(string start)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = start == "mine"
            ? RecordingLinearTracker.HeldBy(RecordingLinearTracker.ViewerId, RecordingLinearTracker.ViewerName)
            : RecordingLinearTracker.Unassigned();

        TrackerTake grant = await Taker(tracker).GrantToAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), RecordingLinearTracker.OtherId, cts.Token);

        grant.Verdict.Should().Be(TrackerTakeVerdict.Taken);
        tracker.Writes.Should().ContainSingle().Which.Variables.GetProperty("assigneeId").GetString()
            .Should().Be(RecordingLinearTracker.OtherId);
        tracker.AssigneeId.Should().Be(RecordingLinearTracker.OtherId);
    }

    [Fact]
    public async Task A_grant_does_not_take_an_issue_from_a_third_party()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.HeldBy("a-third-user-id", "Third");

        TrackerTake grant = await Taker(tracker).GrantToAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), RecordingLinearTracker.OtherId, cts.Token);

        grant.Verdict.Should().Be(TrackerTakeVerdict.HeldByOther);
        tracker.Writes.Should().BeEmpty();
    }

    // ── the release ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_release_clears_an_issue_this_install_holds_by_sending_an_explicit_json_null()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.HeldBy(
            RecordingLinearTracker.ViewerId, RecordingLinearTracker.ViewerName);

        TrackerRelease release = await Taker(tracker).ReleaseAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        release.Succeeded.Should().BeTrue(release.FailureReason);
        release.Wrote.Should().BeTrue();
        tracker.Requests.Select(request => request.IsWrite).Should().Equal([false, true, false]);

        RecordedLinearRequest write = tracker.Writes.Single();
        write.Variables.TryGetProperty("assigneeId", out JsonElement assignee).Should().BeTrue();
        assignee.ValueKind.Should().Be(JsonValueKind.Null);
        write.Body.Should().Contain("\"assigneeId\":null", "the clear is an explicit JSON null, not an omitted member");
        tracker.AssigneeId.Should().BeNull();
    }

    [Fact]
    public async Task A_release_whose_read_back_still_shows_this_install_has_failed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.HeldBy(
            RecordingLinearTracker.ViewerId, RecordingLinearTracker.ViewerName);
        tracker.WriteLands = false;

        TrackerRelease release = await Taker(tracker).ReleaseAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        release.Succeeded.Should().BeFalse();
        release.FailureReason.Should().Contain("still shows this install");
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("somebody-else")]
    public async Task A_release_has_nothing_to_do_on_an_issue_held_by_nobody_or_by_somebody_else(string holder)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = holder == "nobody"
            ? RecordingLinearTracker.Unassigned()
            : RecordingLinearTracker.HeldBy(RecordingLinearTracker.OtherId, RecordingLinearTracker.OtherName);

        TrackerRelease release = await Taker(tracker).ReleaseAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        release.Should().Be(TrackerRelease.NothingToDo);
        tracker.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_release_on_an_issue_that_cannot_be_read_fails_and_writes_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EnsureConnectionAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.HeldBy(
            RecordingLinearTracker.ViewerId, RecordingLinearTracker.ViewerName);
        tracker.Override = _ => RecordingLinearTracker.Json("{}", 503);

        TrackerRelease release = await Taker(tracker).ReleaseAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        release.Succeeded.Should().BeFalse();
        tracker.Writes.Should().BeEmpty();
    }

    // ── the dispatcher ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_dispatcher_refuses_publishes_the_hold_then_claims_once_linear_shows_the_viewer_id()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(postgres.Store, cts.Token);
        await EnsureConnectionAsync(cts.Token);
        Guid projectId = await SeedProjectAsync(ClaimGate.TrackerAssignee, cts.Token);
        Guid taskId = await SeedTaskAsync(projectId, node.OwnerId, dispatchable: true, cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.HeldBy(
            RecordingLinearTracker.OtherId, RecordingLinearTracker.OtherName);
        DispatchEngine engine = Engine(node, Gate(tracker));

        (await engine.ClaimEligibleAsync(cts.Token)).Should().NotContain(work => work.TaskId == taskId);

        await using (IQuerySession refused = postgres.Store.QuerySession())
        {
            (await refused.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(TaskState.Queued);
            TrackerClaimHold hold = (await refused.LoadAsync<TrackerClaimHold>(
                TrackerClaimHold.KeyFor(taskId, node.NodeId), cts.Token))!;
            hold.Provider.Should().Be("linear");
            hold.Holder.Should().Be(RecordingLinearTracker.OtherName);
            TrackerClaimDecision.FromHold(hold).ReasonLine.Should().Contain("waiting for Linear to show ENG-123");
        }

        tracker.AssigneeId = RecordingLinearTracker.ViewerId;
        tracker.AssigneeName = RecordingLinearTracker.ViewerName;

        (await engine.ClaimEligibleAsync(cts.Token)).Should().Contain(work => work.TaskId == taskId);

        await using IQuerySession verify = postgres.Store.QuerySession();
        (await verify.LoadAsync<TrackerClaimHold>(TrackerClaimHold.KeyFor(taskId, node.NodeId), cts.Token))
            .Should().BeNull();
        (await EventsAsync(taskId, cts.Token)).OfType<TrackerAssignmentObserved>().Should().ContainSingle()
            .Which.AssigneeIdentity.Should().Be(RecordingLinearTracker.ViewerId);
    }

    [Fact]
    public async Task The_claim_time_mirror_assigns_a_linear_issue_on_a_project_with_the_gate_off()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(postgres.Store, cts.Token);
        await EnsureConnectionAsync(cts.Token);
        Guid projectId = await SeedProjectAsync(ClaimGate.Off, cts.Token);
        Guid taskId = await SeedTaskAsync(projectId, node.OwnerId, dispatchable: true, cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();

        (await Engine(node, Gate(tracker)).ClaimEligibleAsync(cts.Token)).Should().Contain(work => work.TaskId == taskId);

        tracker.Writes.Should().ContainSingle(
            "the claim itself read nothing (the gate is off), and the mirror took the issue through the gate's own double")
            .Which.Variables.GetProperty("assigneeId").GetString().Should().Be(RecordingLinearTracker.ViewerId);
        (await EventsAsync(taskId, cts.Token)).OfType<TrackerAssignmentWritten>().Should().ContainSingle()
            .Which.AssigneeIdentity.Should().Be(RecordingLinearTracker.ViewerId);
        await using IQuerySession query = postgres.Store.QuerySession();
        (await query.LoadAsync<TaskTrackerAssignMirrorPending>(
            TaskTrackerAssignMirrorPending.KeyFor(taskId, node.NodeId), cts.Token)).Should().BeNull();
    }

    [Fact]
    public async Task A_mirror_write_that_does_not_land_leaves_the_pending_row_the_sweep_retries()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(postgres.Store, cts.Token);
        await EnsureConnectionAsync(cts.Token);
        Guid projectId = await SeedProjectAsync(ClaimGate.Off, cts.Token);
        Guid taskId = await SeedTaskAsync(projectId, node.OwnerId, dispatchable: true, cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();
        tracker.Override = request => request.IsWrite
            ? RecordingLinearTracker.Errors("You do not have permission to update this issue")
            : null;
        DispatchEngine engine = Engine(node, Gate(tracker));

        (await engine.ClaimEligibleAsync(cts.Token)).Should().Contain(
            work => work.TaskId == taskId, "a mirror that did not land never fails the claim");

        string key = TaskTrackerAssignMirrorPending.KeyFor(taskId, node.NodeId);
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            TaskTrackerAssignMirrorPending row = (await session.LoadAsync<TaskTrackerAssignMirrorPending>(key, cts.Token))!;
            row.LastFailureReason.Should().Contain("You do not have permission to update this issue");

            // Backdate the attempt so the existing backoff has elapsed; the cadence itself is not this test's subject.
            row.RecordedAt = DateTimeOffset.UtcNow.AddDays(-1);
            session.Store(row);
            await session.SaveChangesAsync(cts.Token);
        }

        tracker.Override = null;
        await engine.SweepExpiredLeasesAsync(DateTimeOffset.UtcNow, cts.Token);

        tracker.AssigneeId.Should().Be(RecordingLinearTracker.ViewerId, "the retry wrote the issue");
        await using IQuerySession after = postgres.Store.QuerySession();
        (await after.LoadAsync<TaskTrackerAssignMirrorPending>(key, cts.Token)).Should().BeNull();
    }

    // ── seams and seeding ─────────────────────────────────────────────────────────────────────

    private static TrackerClaimGate Gate(RecordingLinearTracker tracker) =>
        new(RecordingProcessRunner.NeverInvoked(), FakeJiraRequester.NeverInvoked(), linearRequester: tracker.Requester);

    /// <summary>
    /// The two-argument constructor the dispatcher builds its mirror with: the recorded double reaches
    /// its writes only through the gate it shares.
    /// </summary>
    private static TrackerAssignmentTake Taker(RecordingLinearTracker tracker) =>
        new(Gate(tracker), RecordingProcessRunner.NeverInvoked());

    private DispatchEngine Engine(NodeContext node, TrackerClaimGate gate) =>
        new(
            postgres.Store,
            node,
            new DaemonConnection(postgres.ConnectionString),
            new FakeProcessManager(),
            new LaunchHoldEngine(postgres.Store, NullLogger<LaunchHoldEngine>.Instance),
            Options.Create(new DaemonOptions { MaxConcurrentTaskRuns = 200, PullRequestPollInterval = TimeSpan.Zero }),
            NullLogger<DispatchEngine>.Instance,
            gate,
            ledger: new FakeLedger());

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        if (await WorkItemConnections.FindLinearConnectionAsync(session, cancellationToken) is not null)
        {
            return;
        }

        Guid connectionId = DomainId.New();
        session.Events.StartStream<ConnectionAggregate>(
            connectionId,
            ConnectionDecider.Register(
                connectionId, DomainId.New(), WorkItemProvider.Linear, RecordingLinearTracker.ViewerName,
                CredentialReference.EnvironmentVariable(KeyVariable), Now, Workspace),
            new ConnectionTrackerIdentityObserved(connectionId, RecordingLinearTracker.ViewerId, Now));
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid> SeedProjectAsync(ClaimGate gate, CancellationToken cancellationToken)
    {
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"h9k-linear-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        repositoryPaths.Add(repositoryPath);

        Guid projectId = DomainId.New();
        await using IDocumentSession seed = postgres.Store.LightweightSession();
        seed.Store(new ProjectDetails
        {
            Id = projectId,
            Name = $"linear-{projectId:N}"[..12],
            RepositoryPath = repositoryPath,
            RepositoryUrl = new Uri("https://github.com/Hallmanac/hall9k"),
            BaseBranch = "main",
            ClaimGate = gate,
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await seed.SaveChangesAsync(cancellationToken);
        return projectId;
    }

    private async Task<Guid> SeedTaskAsync(Guid projectId, Guid ownerId, bool dispatchable, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "claim gate over linear:ENG-123", ["it is done"], TaskType.Chore, null, null, Issue, Now,
            ownerId);
        await using IDocumentSession seed = postgres.Store.LightweightSession();
        seed.Events.StartStream<TaskAggregate>(
            taskId, dispatchable ? TaskSeed.Dispatchable(added, ownerId, Now) : TaskSeed.Publishable(added, ownerId, Now));
        await seed.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task<List<object>> EventsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = postgres.Store.QuerySession();
        IReadOnlyList<IEvent> stream = await session.Events.FetchStreamAsync(taskId, token: cancellationToken);
        return [.. stream.Select(@event => @event.Data)];
    }
}
