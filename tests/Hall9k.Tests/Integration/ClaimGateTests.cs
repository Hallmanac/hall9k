using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// A project whose claim gate is on (idea 64c75e43, Decisions Log #140), against a real store and
/// recorded tracker answers: the tracker's own assignment is the one act that hands out work, so
/// two teammates' installs cannot both run the same card.
/// <para>
/// Both providers are exercised at every door, because the two differ in exactly the places that
/// could break independently: Jira's identity is an <c>accountId</c> recorded on a registered
/// connection and read over HTTP, GitHub's is a login read live from <c>gh</c> on every check;
/// Jira has one assignee, a GitHub issue may have several.
/// </para>
/// <para>
/// <see cref="PostgresFixture"/> shares one database across this class, and
/// <c>NodeContext.InitializeAsync</c> reuses this machine's one node and owner, so every
/// assertion is about a specific task id and the ceiling is set far above anything a neighbour
/// could occupy — a task another method in this class left Queued is a real, expected neighbour.
/// The tracker fakes answer per item key for the same reason: a neighbour's own gated task is
/// re-read by whichever sweep runs next, and a fake that answered every call identically would
/// let it be claimed by the wrong test's answer.
/// </para>
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class ClaimGateTests : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly PostgresFixture postgres;

    private const string TokenVariable = "HALL9K_TEST_CLAIM_GATE_TOKEN";
    private const string JiraAccountId = "5b10a2844c20165700ede21g";
    private const string GitHubLogin = "Hallmanac";
    private const string Repository = "Hallmanac/hall9k";
    private static readonly Uri Site = new("https://hall9k.atlassian.net");
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> _repositoryPaths = [];

    public ClaimGateTests(PostgresFixture postgres)
    {
        this.postgres = postgres;
        Environment.SetEnvironmentVariable(TokenVariable, "a-token");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TokenVariable, null);
        foreach (string path in _repositoryPaths.Where(Directory.Exists))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    // ── h9k task assign: warns and proceeds ───────────────────────────────────────────────────

    /// <summary>
    /// Assignment is still the right act on a gated project: it puts the task in the queue the
    /// gate lets it out of the moment the card is assigned. What the human gets is the warning,
    /// on stderr, naming who holds it and the lever.
    /// </summary>
    [Theory]
    [InlineData("jira", "unassigned")]
    [InlineData("jira", "held")]
    [InlineData("github", "unassigned")]
    [InlineData("github", "held")]
    public async Task Assign_warns_on_stderr_and_assigns_anyway(string provider, string holding)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "GATE-1" : "601";
        (Guid projectId, Guid taskId) = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        TrackerClaimGate gate = provider == "jira"
            ? JiraGate(key, holding == "held" ? "someone-else" : null)
            : GitHubGate(key, holding == "held" ? "teammate" : null);

        StringWriter captured = new();
        TextWriter previousError = Console.Error;
        Console.SetError(captured);
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, token: cts.Token))!;
            await TaskAssignCommand.WarnIfTrackerHoldsAsync(store, session, task, gate, cts.Token);
        }
        finally
        {
            Console.SetError(previousError);
        }

        string warning = captured.ToString();
        warning.Should().Contain("claim gate is tracker-assignee");
        warning.Should().Contain(provider == "jira" ? "GATE-1" : $"{Repository}#601");
        if (holding == "held")
        {
            warning.Should().Contain(provider == "jira" ? "someone-else" : "teammate",
                "who holds it is the thing a human needs to know");
        }
        else
        {
            warning.Should().Contain("assigned to nobody");
        }

        await using IQuerySession verify = store.QuerySession();
        TaskListItem row = (await verify.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        row.State.Should().Be(TaskState.Queued, "the assignment stands — the tracker is the go signal, not this command");
        row.AssignedOwnerId.Should().Be(ownerId);

        await AbandonAsync(store, taskId, cts.Token);
    }

    /// <summary>
    /// The other half of the same door: a card the tracker already shows assigned to this install
    /// warns about nothing and records what it saw, so the stream carries the evidence rather than
    /// only the assignment.
    /// </summary>
    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task Assign_records_what_the_tracker_showed_when_the_gate_passes(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "GATE-2" : "602";
        (Guid projectId, Guid taskId) = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        TrackerClaimGate gate = provider == "jira"
            ? JiraGate(key, JiraAccountId)
            : GitHubGate(key, GitHubLogin);

        StringWriter captured = new();
        TextWriter previousError = Console.Error;
        Console.SetError(captured);
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, token: cts.Token))!;
            await TaskAssignCommand.WarnIfTrackerHoldsAsync(store, session, task, gate, cts.Token);
        }
        finally
        {
            Console.SetError(previousError);
        }

        captured.ToString().Should().BeEmpty("nothing is holding it, so there is nothing to warn about");

        await using IQuerySession verify = store.QuerySession();
        IReadOnlyList<IEvent> stream = await verify.Events.FetchStreamAsync(taskId, token: cts.Token);
        TrackerAssignmentObserved observed = stream.Select(e => e.Data).OfType<TrackerAssignmentObserved>()
            .Should().ContainSingle().Subject;
        observed.AssigneeIdentity.Should().Be(provider == "jira" ? JiraAccountId : GitHubLogin);
        observed.ObservedAt.Should().NotBe(default);

        await AbandonAsync(store, taskId, cts.Token);
    }

    // ── h9k task work / h9k task start: refuse, exit 70 ───────────────────────────────────────

    /// <summary>
    /// The doors that start work now refuse, with the sentence <c>h9k task assign</c> warns with
    /// and the exit code a standing project rule earns — a <see cref="DomainBusinessRuleException"/>,
    /// which Program.cs maps to <see cref="ExitCodes.BusinessRule"/> (70).
    /// </summary>
    [Theory]
    [InlineData("jira", "work")]
    [InlineData("jira", "start")]
    [InlineData("github", "work")]
    [InlineData("github", "start")]
    public async Task Work_and_start_refuse_a_card_this_install_does_not_hold(string provider, string door)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        BootstrapContext context = await BootstrapAsync(store, cts.Token);
        // A Jira key has a shape (PROJ-123), and a gate that cannot parse one refuses for that
        // reason instead — which is a real behaviour, but not the one this test is about.
        string key = provider == "jira"
            ? (door == "work" ? "GATE-11" : "GATE-12")
            : (door == "work" ? "611" : "612");
        (Guid projectId, Guid taskId) = await SeedGatedAsync(store, provider, key, context.OwnerId, cts.Token);

        TrackerClaimGate gate = provider == "jira"
            ? JiraGate(key, "someone-else")
            : GitHubGate(key, "teammate");

        ExitCodes.BusinessRule.Should().Be(70, "the refusal's own exit code is part of the contract");

        Func<Task> act = () => ClaimThroughAsync(store, taskId, context, door, gate, cts.Token);

        (await act.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message
            .Should().Contain("claim gate is tracker-assignee")
            .And.Contain(provider == "jira" ? "someone-else" : "teammate");

        await using IQuerySession verify = store.QuerySession();
        (await verify.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(
            TaskState.Queued, "a refused claim leaves the task exactly where it was");

        await AbandonAsync(store, taskId, cts.Token);
    }

    // ── the dispatcher ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The dispatcher's own door: refused while the tracker names somebody else, claimed the
    /// moment it names this install — with the evidence on the stream and the published hold
    /// cleared, so the board stops explaining a wait that has ended.
    /// </summary>
    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task The_dispatcher_refuses_then_claims_once_the_tracker_shows_this_identity(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        string key = provider == "jira" ? "GATE-3" : "603";
        (Guid projectId, Guid taskId) = await SeedGatedAsync(store, provider, key, node.OwnerId, cts.Token);

        string? holder = "someone-else";
        TrackerClaimGate gate = provider == "jira"
            ? JiraGate(key, () => holder)
            : GitHubGate(key, () => holder);

        // No re-read throttle for this test: the cadence is its own test below, and this one is
        // about the two answers rather than how often they are asked for.
        DispatchEngine engine = Engine(store, node, gate, TimeSpan.Zero);

        (await engine.ClaimEligibleAsync(cts.Token)).Should().NotContain(work => work.TaskId == taskId);

        await using (IQuerySession afterRefusal = store.QuerySession())
        {
            (await afterRefusal.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(TaskState.Queued);
            TrackerClaimHold hold = (await afterRefusal.LoadAsync<TrackerClaimHold>(TrackerClaimHold.KeyFor(taskId, node.NodeId), cts.Token))!;
            hold.Holder.Should().Be("someone-else", "who holds it is named, whichever tracker said so");
            hold.Error.Should().BeNull("the tracker answered fine — it simply named somebody else");
            TrackerClaimDecision.FromHold(hold).ReasonLine.Should().Contain("assigned to you");
        }

        holder = provider == "jira" ? JiraAccountId : GitHubLogin;

        (await engine.ClaimEligibleAsync(cts.Token)).Should().Contain(work => work.TaskId == taskId);

        await using IQuerySession verify = store.QuerySession();
        (await verify.LoadAsync<TrackerClaimHold>(TrackerClaimHold.KeyFor(taskId, node.NodeId), cts.Token)).Should().BeNull(
            "a claim that went through leaves no hold behind to explain");
        IReadOnlyList<IEvent> stream = await verify.Events.FetchStreamAsync(taskId, token: cts.Token);
        List<object> data = [.. stream.Select(e => e.Data)];
        data.OfType<TrackerAssignmentObserved>().Should().ContainSingle()
            .Which.AssigneeIdentity.Should().Be(holder);
        data.FindIndex(e => e is TrackerAssignmentObserved).Should().BeLessThan(
            data.FindIndex(e => e is TaskClaimed),
            "the stream reads in the order the two things happened: what the tracker showed, then the claim");
    }

    /// <summary>
    /// The gate fails closed, and the hold it publishes has to be readable: the tracker's own
    /// error verbatim, what ends it, and whether the credentials were refused rather than the
    /// tracker failing to answer — because those have opposite remedies.
    /// </summary>
    [Theory]
    [InlineData("jira", true)]
    [InlineData("jira", false)]
    [InlineData("github", true)]
    [InlineData("github", false)]
    public async Task An_unreadable_tracker_holds_the_claim_and_names_the_lever(
        string provider, bool authenticationRefusal)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        string key = provider == "jira"
            ? (authenticationRefusal ? "GATE-4" : "GATE-5")
            : (authenticationRefusal ? "604" : "605");
        (Guid projectId, Guid taskId) = await SeedGatedAsync(store, provider, key, node.OwnerId, cts.Token);

        TrackerClaimGate gate = provider == "jira"
            ? JiraGateFailing(key, authenticationRefusal)
            : GitHubGateFailing(key, authenticationRefusal);
        DispatchEngine engine = Engine(store, node, gate, TimeSpan.Zero);

        (await engine.ClaimEligibleAsync(cts.Token)).Should().NotContain(work => work.TaskId == taskId);

        await using IQuerySession verify = store.QuerySession();
        (await verify.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(
            TaskState.Queued, "a gate that cannot read the tracker holds the claim rather than releasing it");
        TrackerClaimHold hold = (await verify.LoadAsync<TrackerClaimHold>(TrackerClaimHold.KeyFor(taskId, node.NodeId), cts.Token))!;
        hold.Error.Should().NotBeNullOrWhiteSpace();
        hold.AuthenticationRefusal.Should().Be(authenticationRefusal);
        hold.Lever.Should().NotBeNullOrWhiteSpace();
        if (authenticationRefusal)
        {
            hold.Lever.Should().Contain(provider == "jira" ? "h9k connection add jira" : "gh auth login");
        }
        else
        {
            hold.Lever.Should().Contain("wait out the outage");
        }

        TrackerClaimDecision.FromHold(hold).ReasonLine.Should()
            .Contain("fails closed")
            .And.Contain(hold.Error!, "the status line quotes the tracker verbatim")
            .And.Contain(hold.Lever!, "and says what ends the hold");

        await AbandonAsync(store, taskId, cts.Token);
    }

    /// <summary>
    /// The last-resort catch, which is the one hold a board could not previously explain (Copilot
    /// review, PR #260): a read that throws rather than answering — the credential store, the
    /// identity append, or a connector failing in a shape none of the classified paths turned
    /// into a decision. It has to hold the claim, and holding is only half of failing closed: a
    /// wait with nothing published reads exactly like an idle queue, so the hold is written and
    /// composes the same sentence every other unreadable hold does.
    /// </summary>
    [Fact]
    public async Task A_read_that_throws_holds_the_claim_and_still_publishes_a_hold()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        const string key = "GATE-6";
        (Guid projectId, Guid taskId) = await SeedGatedAsync(store, "jira", key, node.OwnerId, cts.Token);

        // A requester that throws something the provider does not classify. Every foreseen HTTP
        // failure is already an Unreadable decision — those are the test above — so reaching the
        // catch at all takes a failure shape JiraWorkItemProvider's own catches do not name.
        TrackerClaimGate gate = new(
            RecordingProcessRunner.NeverInvoked(),
            RecordingJiraRequester.RespondingTo(request =>
                request.Url.AbsolutePath.EndsWith("/myself", StringComparison.Ordinal)
                    ? new JiraResponse(200, $$"""{"accountId":"{{JiraAccountId}}","displayName":"Brian Hall"}""")
                    : throw new InvalidOperationException("the requester came apart mid-read")).Requester);
        DispatchEngine engine = Engine(store, node, gate, TimeSpan.Zero);

        (await engine.ClaimEligibleAsync(cts.Token)).Should().NotContain(work => work.TaskId == taskId);

        await using IQuerySession verify = store.QuerySession();
        (await verify.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(
            TaskState.Queued, "an unforeseen failure holds the claim rather than releasing it");
        TrackerClaimHold hold = (await verify.LoadAsync<TrackerClaimHold>(
            TrackerClaimHold.KeyFor(taskId, node.NodeId), cts.Token))!;
        hold.Error.Should().Contain("InvalidOperationException")
            .And.Contain("the requester came apart mid-read", "the failure is quoted, not paraphrased");
        hold.AuthenticationRefusal.Should().BeFalse("nothing refused anything — the read never finished");
        hold.Identity.Should().BeNull("naming this install's identity would mean having read it");
        hold.ItemUrl.Should().BeNull("and the same goes for the item's own URL");
        hold.ItemKey.Should().Be(key);

        TrackerClaimDecision.FromHold(hold).ReasonLine.Should()
            .Contain("fails closed")
            .And.Contain("daemon log", "the lever points at where the stack trace is, since a row cannot carry it");

        await AbandonAsync(store, taskId, cts.Token);
    }

    /// <summary>
    /// The dispatch loop sweeps roughly every five seconds; a held card must not cost a tracker
    /// call every five seconds for as long as it sits in the queue. It is re-read no more often
    /// than <see cref="DaemonOptions.PullRequestPollInterval"/> — three minutes, the same
    /// human-timescale cadence closeout polls pull requests at.
    /// </summary>
    [Fact]
    public async Task A_held_task_is_re_read_no_more_often_than_the_pull_request_poll_interval()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        const string key = "606";
        (Guid projectId, Guid taskId) = await SeedGatedAsync(store, "github", key, node.OwnerId, cts.Token);

        int reads = 0;
        RecordingProcessRunner gh = new(arguments =>
        {
            if (arguments is ["api", "user", ..])
            {
                return new ProcessResult(0, GitHubLogin + "\n", string.Empty);
            }

            if (!arguments.Contains(key))
            {
                return new ProcessResult(1, string.Empty, "some other test's card");
            }

            reads++;
            return new ProcessResult(0, """{"assignees":[{"login":"teammate"}]}""", string.Empty);
        });
        DispatchEngine engine = Engine(
            store, node, new TrackerClaimGate(gh.Runner, FakeJiraRequester.NeverInvoked()),
            TimeSpan.FromMinutes(3));

        await engine.ClaimEligibleAsync(cts.Token);
        await engine.ClaimEligibleAsync(cts.Token);
        await engine.ClaimEligibleAsync(cts.Token);

        reads.Should().Be(1, "three sweeps inside one interval read the tracker once");

        // The same engine with no interval reads again on the very next sweep, which is what says
        // the single read above was the cadence rather than a check that only ever runs once.
        DispatchEngine eager = Engine(
            store, node, new TrackerClaimGate(gh.Runner, FakeJiraRequester.NeverInvoked()), TimeSpan.Zero);
        await eager.ClaimEligibleAsync(cts.Token);
        await eager.ClaimEligibleAsync(cts.Token);

        reads.Should().Be(3);

        await AbandonAsync(store, taskId, cts.Token);
    }

    // ── the untouched paths ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The paths the gate must not touch, each with tracker seams that throw if they are reached
    /// at all: a task with no linked item, one published <c>--untracked</c> (which carries no
    /// reference either, and attests that deliberately), a <c>pr-review</c> task — whose pull
    /// request's own assignment is already auto-pr-review's go signal, and whose assignee field
    /// nobody sets — and a project whose gate is off.
    /// </summary>
    [Theory]
    [InlineData("no-reference")]
    [InlineData("untracked")]
    [InlineData("pr-review")]
    [InlineData("gate-off")]
    public async Task The_untouched_paths_claim_exactly_as_they_did_before(string shape)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        await SeedProjectAsync(
            store, projectId, shape == "gate-off" ? ClaimGate.Off : ClaimGate.TrackerAssignee, cts.Token);

        ExternalReference? reference = shape switch
        {
            "pr-review" => new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{Repository}#77"),
            "gate-off" => new ExternalReference(WorkItemProvider.GitHub, $"{Repository}#607"),
            _ => null,
        };
        TaskType type = shape == "pr-review" ? TaskType.PrReview : TaskType.Chore;

        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] events) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, $"claim gate: {shape}", ["it is done"], type, null, null, reference,
                    Now, node.OwnerId),
                node.OwnerId, Now);
            seed.Events.StartStream<TaskAggregate>(taskId, events);
            await seed.SaveChangesAsync(cts.Token);
            _ = task;
        }

        if (shape == "untracked")
        {
            // The attestation itself is what --untracked records; the reference stays null either
            // way, which is the fact the gate reads.
            await using IQuerySession attested = store.QuerySession();
            IReadOnlyList<IEvent> stream = await attested.Events.FetchStreamAsync(taskId, token: cts.Token);
            stream.Select(e => e.Data).OfType<TaskPublished>().Should().ContainSingle();
        }

        // Both seams refuse to be called at all, so "the gate makes no tracker call here" is
        // enforced rather than merely expected.
        DispatchEngine engine = Engine(
            store,
            node,
            new TrackerClaimGate(RecordingProcessRunner.NeverInvoked(), FakeJiraRequester.NeverInvoked()),
            TimeSpan.Zero);

        (await engine.ClaimEligibleAsync(cts.Token)).Should().Contain(work => work.TaskId == taskId);

        await using IQuerySession verify = store.QuerySession();
        (await verify.LoadAsync<TrackerClaimHold>(TrackerClaimHold.KeyFor(taskId, node.NodeId), cts.Token)).Should().BeNull();
        (await verify.Events.FetchStreamAsync(taskId, token: cts.Token))
            .Select(e => e.Data).OfType<TrackerAssignmentObserved>().Should().BeEmpty(
                "nothing was observed, so the stream asserts nothing");
    }

    // ── seams ─────────────────────────────────────────────────────────────────────────────────

    private static Task ClaimThroughAsync(
        DocumentStore store,
        Guid taskId,
        BootstrapContext context,
        string door,
        TrackerClaimGate gate,
        CancellationToken cancellationToken) =>
        door == "work" ? WorkAsync(store, taskId, context, gate, cancellationToken)
            : StartAsync(store, taskId, context, gate, cancellationToken);

    private static async Task WorkAsync(
        DocumentStore store, Guid taskId, BootstrapContext context, TrackerClaimGate gate,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(taskId, cancellationToken))!;
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cancellationToken))!;
        await TaskWorkCommand.ClaimAndCutAsync(
            store, session, task, fence, context, DomainId.New(),
            SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.InteractiveClaim),
            acknowledgeUnmetDependencies: false, gate, cancellationToken);
    }

    private static async Task StartAsync(
        DocumentStore store, Guid taskId, BootstrapContext context, TrackerClaimGate gate,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(taskId, cancellationToken))!;
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cancellationToken))!;
        await TaskStartCommand.ClaimAndCutAsync(
            store, session, task, fence, context, DomainId.New(),
            SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.Build),
            acknowledgeUnmetDependencies: false, interactiveMode: false, gate, cancellationToken);
    }

    private DispatchEngine Engine(
        DocumentStore store, NodeContext node, TrackerClaimGate gate, TimeSpan reReadInterval) =>
        new(
            store,
            node,
            new DaemonConnection(postgres.ConnectionString),
            new FakeProcessManager(),
            Options.Create(new DaemonOptions
            {
                // Far above anything a neighbouring test method could occupy: the ceiling is not
                // what this class is about, and a task it turned away would read as a gate refusal.
                MaxConcurrentTaskRuns = 200,
                PullRequestPollInterval = reReadInterval,
            }),
            NullLogger<DispatchEngine>.Instance,
            gate);

    /// <summary>Jira answers <c>/myself</c> with this install's own account, and the card with a fixed holder.</summary>
    private static TrackerClaimGate JiraGate(string key, string? assigneeAccountId) =>
        JiraGate(key, () => assigneeAccountId);

    private static TrackerClaimGate JiraGate(string key, Func<string?> assigneeAccountId) =>
        new(
            RecordingProcessRunner.NeverInvoked(),
            RecordingJiraRequester.RespondingTo(request =>
            {
                if (request.Url.AbsolutePath.EndsWith("/myself", StringComparison.Ordinal))
                {
                    return new JiraResponse(200, $$"""{"accountId":"{{JiraAccountId}}","displayName":"Brian Hall"}""");
                }

                if (!request.Url.ToString().Contains(key, StringComparison.Ordinal))
                {
                    return new JiraResponse(404, """{"errorMessages":["some other test's card"]}""");
                }

                // Concatenated rather than an interpolated raw literal: the JSON's own trailing
                // braces run three deep, which no number of '$' delimiters reads cleanly.
                return assigneeAccountId() is { } accountId
                    ? new JiraResponse(
                        200,
                        "{\"key\":\"" + key + "\",\"fields\":{\"assignee\":{\"accountId\":\"" + accountId
                        + "\",\"displayName\":\"" + Displayed(accountId) + "\"}}}")
                    : new JiraResponse(200, "{\"key\":\"" + key + "\",\"fields\":{\"assignee\":null}}");
            }).Requester);

    private static TrackerClaimGate JiraGateFailing(string key, bool authenticationRefusal) =>
        new(
            RecordingProcessRunner.NeverInvoked(),
            RecordingJiraRequester.RespondingTo(request =>
                request.Url.AbsolutePath.EndsWith("/myself", StringComparison.Ordinal)
                    ? new JiraResponse(200, $$"""{"accountId":"{{JiraAccountId}}","displayName":"Brian Hall"}""")
                    : !request.Url.ToString().Contains(key, StringComparison.Ordinal)
                        ? new JiraResponse(404, """{"errorMessages":["some other test's card"]}""")
                        : authenticationRefusal
                            ? new JiraResponse(401, """{"errorMessages":["Client must be authenticated"]}""")
                            : new JiraResponse(503, """{"errorMessages":["Service temporarily unavailable"]}""")
            ).Requester);

    private static TrackerClaimGate GitHubGate(string key, string? assigneeLogin) =>
        GitHubGate(key, () => assigneeLogin);

    private static TrackerClaimGate GitHubGate(string key, Func<string?> assigneeLogin) =>
        new(
            new RecordingProcessRunner(arguments =>
                arguments is ["api", "user", ..]
                    ? new ProcessResult(0, GitHubLogin + "\n", string.Empty)
                    : !arguments.Contains(key)
                        ? new ProcessResult(1, string.Empty, "some other test's card")
                        : new ProcessResult(
                            0,
                            assigneeLogin() is { } login
                                ? $$"""{"assignees":[{"login":"{{login}}"}]}"""
                                : """{"assignees":[]}""",
                            string.Empty)).Runner,
            FakeJiraRequester.NeverInvoked());

    private static TrackerClaimGate GitHubGateFailing(string key, bool authenticationRefusal) =>
        new(
            new RecordingProcessRunner(arguments =>
                arguments is ["api", "user", ..]
                    ? new ProcessResult(0, GitHubLogin + "\n", string.Empty)
                    : new ProcessResult(
                        1,
                        string.Empty,
                        authenticationRefusal
                            ? "gh: To get started with GitHub CLI, please run: gh auth login"
                            : "error connecting to api.github.com")).Runner,
            FakeJiraRequester.NeverInvoked());

    /// <summary>The display name a fake tenant puts beside an accountId, so a holder reads as a person.</summary>
    private static string Displayed(string accountId) =>
        accountId == JiraAccountId ? "Brian Hall" : accountId;

    // ── seeding ───────────────────────────────────────────────────────────────────────────────

    private async Task<(Guid ProjectId, Guid TaskId)> SeedGatedAsync(
        DocumentStore store, string provider, string key, Guid ownerId, CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        await SeedProjectAsync(store, projectId, ClaimGate.TrackerAssignee, cancellationToken);
        if (provider == "jira")
        {
            await SeedJiraConnectionAsync(store, cancellationToken);
        }

        ExternalReference reference = provider == "jira"
            ? new ExternalReference(WorkItemProvider.Jira, key)
            : new ExternalReference(WorkItemProvider.GitHub, $"{Repository}#{key}");

        await using IDocumentSession seed = store.LightweightSession();
        seed.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
            TaskDecider.Add(
                taskId, projectId, $"claim gate over {reference}", ["it is done"], TaskType.Chore,
                null, null, reference, Now, ownerId),
            ownerId, Now));
        await seed.SaveChangesAsync(cancellationToken);
        return (projectId, taskId);
    }

    private async Task SeedProjectAsync(
        DocumentStore store, Guid projectId, ClaimGate gate, CancellationToken cancellationToken)
    {
        // A real directory, because gh reads the repository from the directory it runs in and the
        // gate passes this path straight through to the runner — the fakes never look at it, but a
        // path that does not exist would be a fact this seed asserted and never observed.
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"h9k-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        _repositoryPaths.Add(repositoryPath);

        await using IDocumentSession seed = store.LightweightSession();
        seed.Store(new ProjectDetails
        {
            Id = projectId,
            Name = $"gate-{projectId:N}"[..12],
            RepositoryPath = repositoryPath,
            RepositoryUrl = new Uri($"https://github.com/{Repository}"),
            BaseBranch = "main",
            ClaimGate = gate,
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await seed.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// One Jira connection, with its identity deliberately unrecorded: that is a connection
    /// registered before the accountId was captured, so the first gate check reads it live from
    /// <c>/rest/api/2/myself</c> and records it — which every Jira case here therefore exercises.
    /// </summary>
    private static async Task SeedJiraConnectionAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        if ((await session.Query<ConnectionDetails>().ToListAsync(cancellationToken))
            .Any(connection => connection.Provider == WorkItemProvider.Jira))
        {
            return;
        }

        Guid connectionId = DomainId.New();
        session.Events.StartStream<ConnectionAggregate>(connectionId, ConnectionDecider.Register(
            connectionId, DomainId.New(), WorkItemProvider.Jira, "brian@example.com",
            CredentialReference.EnvironmentVariable(TokenVariable), Now, Site));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task<BootstrapContext> BootstrapAsync(
        DocumentStore store, CancellationToken cancellationToken)
    {
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(store, cancellationToken);
        await using IDocumentSession session = store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return context;
    }

    /// <summary>
    /// Take this test's own task out of the queue on the way out, so a later method's sweep in
    /// this shared database is not re-reading a neighbour's gated card on every assertion.
    /// </summary>
    private static async Task AbandonAsync(DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, token: cancellationToken);
        if (task is null || task.State == TaskState.Abandoned)
        {
            return;
        }

        session.Events.Append(taskId, TaskDecider.Abandon(
            task, "the claim-gate test that seeded it is over", DateTimeOffset.UtcNow, task.AssignedOwnerId ?? Guid.Empty));
        await session.SaveChangesAsync(cancellationToken);
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });
}
