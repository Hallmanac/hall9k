using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using JasperFx.Events;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k task assign --take</c> against a real store and recorded tracker answers (idea
/// 64c75e43, Decisions Log #143): claiming stops being a two-place act, because one command moves
/// the tracker and the board together and the gate then passes on its own.
/// <para>
/// Both providers are exercised at every outcome, because the two differ in exactly the places
/// that could break independently: Jira's write is a REST field update through
/// <see cref="JiraWriteExecutor"/> and its identity is an <c>accountId</c>, GitHub's is
/// <c>gh issue edit --add-assignee</c> with a login read live, GitHub compares that login
/// case-insensitively where a Jira <c>accountId</c> is compared byte for byte, and only GitHub has
/// a collaborator requirement to refuse on.
/// </para>
/// <para>
/// Every tracker fake here is <em>stateful</em> — the item starts unassigned and the write is what
/// makes the read-back show a holder — because the read-back is the whole point of the feature: a
/// fake that answered "assigned to you" before the write would pass a test that proves nothing.
/// Each fake also answers only about its own item key and refuses every other, for
/// <see cref="ClaimGateTests"/>' own reason: <see cref="PostgresFixture"/> shares one database
/// across the class and a neighbour's gated task is a real, expected neighbour.
/// </para>
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class TrackerAssignmentTakeTests : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly PostgresFixture postgres;

    private const string TokenVariable = "HALL9K_TEST_TAKE_TOKEN";
    private const string JiraAccountId = "5b10a2844c20165700ede21g";
    private const string JiraDisplayName = "Brian Hall";
    private const string GitHubLogin = "Hallmanac";
    private const string Repository = "Hallmanac/hall9k";
    private static readonly Uri Site = new("https://hall9k.atlassian.net");
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> repositoryPaths = [];

    public TrackerAssignmentTakeTests(PostgresFixture postgres)
    {
        this.postgres = postgres;
        Environment.SetEnvironmentVariable(TokenVariable, "a-token");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TokenVariable, null);
        foreach (string path in repositoryPaths.Where(Directory.Exists))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    // ── taking an item nobody holds ───────────────────────────────────────────────────────────

    /// <summary>
    /// The whole feature in one path: the tracker shows the item assigned to nobody, the take
    /// writes this install's own identity into its assignee field, reads the item back, records
    /// <see cref="TrackerAssignmentWritten"/> from what the read-back showed rather than from what
    /// was asked for, and the assignment proceeds — so the gate the dispatcher reads next passes on
    /// its own.
    /// </summary>
    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task Take_writes_the_assignment_reads_it_back_and_records_what_it_saw(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-1" : "701";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned(provider, key);
        TrackerTake? take = await TakeAsync(store, taskId, tracker, take: true, offer: null, cts.Token);

        take!.Verdict.Should().Be(TrackerTakeVerdict.Taken);
        take.Wrote.Should().BeTrue();
        tracker.Writes.Should().ContainSingle("the item is written to exactly once, never once per door");
        take.TookLine.Should().Contain(provider == "jira" ? "TAKE-1" : $"{Repository}#701");
        take.TookLine.Should().Contain("claim gate is tracker-assignee");

        TrackerAssignmentWritten written = (await EventsAsync(store, taskId, cts.Token))
            .OfType<TrackerAssignmentWritten>().Should().ContainSingle().Subject;
        written.AssigneeIdentity.Should().Be(provider == "jira" ? JiraAccountId : GitHubLogin);
        written.AssigneeName.Should().Be(
            provider == "jira" ? JiraDisplayName : null,
            "the name is the tracker's own displayName where it offered one, and honestly null where it did not");
        written.ObservedAt.Should().NotBe(default);
        written.Reference.Should().Contain(key);

        (await EventsAsync(store, taskId, cts.Token)).OfType<TrackerAssignmentObserved>()
            .Should().BeEmpty("this install wrote the assignment, and a stream that spelt the two the same could not say so");

        // The gate the dispatcher reads next is the read the take just made true.
        TrackerClaimDecision after = await tracker.Gate().CheckAsync(
            store, ClaimGate.TrackerAssignee, await ReferenceAsync(store, taskId, cts.Token),
            await RepositoryPathAsync(store, taskId, cts.Token), cts.Token);
        after.Verdict.Should().Be(TrackerClaimVerdict.Assigned, "the gate now passes on its own");
    }

    /// <summary>
    /// Jira's half of the write, checked at the wire: a single <c>PUT</c> to the card's own
    /// endpoint carrying a <c>fields.assignee.accountId</c> and nothing else. No transition
    /// endpoint is touched and no status field is sent — the item's status is not this platform's
    /// to move (Decisions Log #102), and the assignee field is the one thing the take was asked to
    /// change.
    /// </summary>
    [Fact]
    public async Task Take_writes_jira_as_a_field_update_carrying_only_the_assignee()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "jira", "TAKE-2", ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned("jira", "TAKE-2");
        await TakeAsync(store, taskId, tracker, take: true, offer: null, cts.Token);

        string body = tracker.Writes.Should().ContainSingle().Subject;
        using JsonDocument written = JsonDocument.Parse(body);
        JsonElement fields = written.RootElement.GetProperty("fields");
        fields.EnumerateObject().Select(field => field.Name).Should().Equal(["assignee"]);
        fields.GetProperty("assignee").GetProperty("accountId").GetString().Should().Be(JiraAccountId);

        tracker.Requests.Should().NotContain(
            request => request.Url.AbsolutePath.Contains("transition", StringComparison.OrdinalIgnoreCase),
            "an assignment is not a workflow move, and this platform never makes one");
    }

    /// <summary>
    /// GitHub's half at the wire: <c>gh issue edit &lt;number&gt; --repo owner/repo --add-assignee
    /// &lt;login&gt;</c>, with the login the one <c>gh</c> itself reported on this very check. Add
    /// rather than replace, and nothing about state, labels or milestone — the repository's own
    /// workflow is not this platform's to run.
    /// </summary>
    [Fact]
    public async Task Take_writes_github_by_adding_the_live_login_as_an_assignee()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "github", "702", ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned("github", "702");
        await TakeAsync(store, taskId, tracker, take: true, offer: null, cts.Token);

        IReadOnlyList<string> edit = tracker.GhCalls
            .Should().ContainSingle(arguments => arguments.Contains("edit")).Subject;
        edit.Should().Equal(["issue", "edit", "702", "--repo", Repository, "--add-assignee", GitHubLogin]);
        tracker.GhCalls.Should().NotContain(
            arguments => arguments.Contains("--add-label") || arguments.Contains("close"),
            "the take touches the assignee field and nothing else");
    }

    // ── refusing an item somebody else holds ──────────────────────────────────────────────────

    /// <summary>
    /// The refusal the idea's own race question turns on: an item somebody else holds is never
    /// overwritten. Nothing is written, the task is left exactly as it was — not assigned, not
    /// queued — and the sentence names the holder and says outright that no flag exists that would
    /// have taken it.
    /// </summary>
    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task Take_refuses_an_item_somebody_else_holds_and_writes_nothing(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-3" : "703";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.HeldBy(provider, key, provider == "jira" ? "someone-else" : "teammate");

        Func<Task> take = () => TakeAsync(store, taskId, tracker, take: true, offer: null, cts.Token);
        DomainBusinessRuleException refusal = (await take.Should().ThrowAsync<DomainBusinessRuleException>()).Which;

        refusal.Message.Should().Contain(provider == "jira" ? "someone-else" : "teammate");
        refusal.Message.Should().Contain("never takes one from another person");
        refusal.Message.Should().Contain("there is no flag that does");
        tracker.Writes.Should().BeEmpty("nothing is written to an item somebody else holds");
        tracker.GhCalls.Should().NotContain(arguments => arguments.Contains("edit"));

        await using IQuerySession verify = store.QuerySession();
        TaskListItem row = (await verify.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        row.State.Should().Be(TaskState.Published, "a refused take leaves the task exactly as before");
        row.AssignedOwnerId.Should().BeNull();
        (await EventsAsync(store, taskId, cts.Token))
            .Where(@event => @event is TrackerAssignmentWritten or TrackerAssignmentObserved or TaskAssigned)
            .Should().BeEmpty();
    }

    // ── the item is already this install's ────────────────────────────────────────────────────

    /// <summary>
    /// Already mine writes nothing at all and records the <em>observation</em> rather than a write
    /// this install did not make — the distinction the two events exist for. The assignment
    /// proceeds either way, which is also what makes re-running <c>--take</c> after a half-finished
    /// one safe.
    /// </summary>
    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task Take_on_an_item_already_mine_records_the_observation_and_proceeds(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-4" : "704";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        // Deliberately spelt in the other case for GitHub: a login is matched case-insensitively,
        // and a take that re-wrote an item because "hallmanac" is not "Hallmanac" would put a
        // second, identical assignment on somebody's board every time it ran.
        FakeTracker tracker = FakeTracker.HeldBy(
            provider, key, provider == "jira" ? JiraAccountId : GitHubLogin.ToLowerInvariant());

        TrackerTake? take = await TakeAsync(store, taskId, tracker, take: true, offer: null, cts.Token);

        take!.Verdict.Should().Be(TrackerTakeVerdict.AlreadyMine);
        take.Wrote.Should().BeFalse();
        take.TookLine.Should().Contain("nothing was written");
        tracker.Writes.Should().BeEmpty();
        tracker.GhCalls.Should().NotContain(arguments => arguments.Contains("edit"));

        IReadOnlyList<object> events = await EventsAsync(store, taskId, cts.Token);
        events.OfType<TrackerAssignmentObserved>().Should().ContainSingle().Which
            .AssigneeIdentity.Should().Be(provider == "jira" ? JiraAccountId : GitHubLogin.ToLowerInvariant());
        events.OfType<TrackerAssignmentWritten>().Should().BeEmpty("this install wrote nothing");
    }

    // ── the interactive offer, and its decline ────────────────────────────────────────────────

    /// <summary>
    /// No <c>--take</c>, but a human at the terminal and an item nobody holds: the offer is made,
    /// and accepting it is the identical take.
    /// </summary>
    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task An_accepted_offer_takes_the_item(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-5" : "705";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned(provider, key);
        List<TrackerClaimDecision> offered = [];
        TrackerTake? take = await TakeAsync(
            store, taskId, tracker, take: false,
            offer: decision =>
            {
                offered.Add(decision);
                return true;
            },
            cts.Token);

        offered.Should().ContainSingle().Which.Verdict.Should().Be(
            TrackerClaimVerdict.Unassigned, "only an item the tracker shows assigned to nobody is offered");
        take!.Verdict.Should().Be(TrackerTakeVerdict.Taken);
        tracker.Writes.Should().ContainSingle();
        (await EventsAsync(store, taskId, cts.Token)).OfType<TrackerAssignmentWritten>().Should().ContainSingle();
    }

    /// <summary>
    /// The decline: nothing is written, and the door falls back to exactly what it did before this
    /// flag existed — the read it already made is warned with rather than read a second time, and
    /// the assignment goes ahead, because the tracker is the go signal and the queue is where the
    /// task is meant to wait.
    /// </summary>
    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task A_declined_offer_writes_nothing_and_leaves_the_warning_to_the_door(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-6" : "706";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned(provider, key);
        int asked = 0;
        (TrackerTake? take, TrackerClaimDecision? decision) = await ReadTakeBeforeAssigningAsync(
            store, taskId, tracker, take: false,
            offer: _ =>
            {
                asked++;
                return false;
            },
            cts.Token);

        asked.Should().Be(1, "the human is asked once");
        take.Should().BeNull();
        decision!.Verdict.Should().Be(TrackerClaimVerdict.Unassigned);
        decision.RefusalLine.Should().Contain("assigned to nobody");
        tracker.Writes.Should().BeEmpty();
        tracker.GhCalls.Should().NotContain(arguments => arguments.Contains("edit"));
        (await EventsAsync(store, taskId, cts.Token))
            .Where(@event => @event is TrackerAssignmentWritten or TrackerAssignmentObserved)
            .Should().BeEmpty("a declined offer observed a hold, and a hold is warned about, not recorded");
    }

    /// <summary>
    /// The unattended run: no <c>--take</c> and nobody to ask, so the tracker is never written to
    /// and never even read here — the door's own post-commit warning is what reads it and says the
    /// queue will not move, exactly as the gate task specifies. "Never writing silently" is the
    /// criterion, and this is what makes it structural rather than a promise.
    /// </summary>
    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task A_non_interactive_assign_never_writes(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-7" : "707";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned(provider, key);
        (TrackerTake? take, TrackerClaimDecision? decision) = await ReadTakeBeforeAssigningAsync(
            store, taskId, tracker, take: false, offer: null, cts.Token);

        take.Should().BeNull();
        decision.Should().BeNull("with nobody to ask, the door reads the gate after the commit as it always did");
        tracker.Writes.Should().BeEmpty();
        tracker.GhCalls.Should().NotContain(arguments => arguments.Contains("edit"));
    }

    // ── refusals from the tracker itself ──────────────────────────────────────────────────────

    /// <summary>
    /// GitHub only accepts an assignee who can be assigned on that repository. gh's own stderr is
    /// quoted verbatim rather than relabelled, the rule is named beside it, and the task is left
    /// exactly as it was — the refusal is not classified by matching gh's wording, because the text
    /// GitHub answers a non-assignable login with has not been observed from this build environment
    /// (AGENTS.md, never guess at unobserved facts).
    /// </summary>
    [Fact]
    public async Task Take_quotes_githubs_own_collaborator_refusal_and_changes_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "github", "708", ownerId, cts.Token);

        const string GhSaid =
            "failed to update https://api.github.com/graphql: could not add assignee: "
            + "'Hallmanac' is not a collaborator on Hallmanac/hall9k";
        FakeTracker tracker = FakeTracker.Unassigned("github", "708", refuseWrite: GhSaid);

        Func<Task> take = () => TakeAsync(store, taskId, tracker, take: true, offer: null, cts.Token);
        DomainBusinessRuleException refusal = (await take.Should().ThrowAsync<DomainBusinessRuleException>()).Which;

        refusal.Message.Should().Contain(GhSaid, "gh's own answer is the evidence, quoted rather than summarised");
        refusal.Message.Should().Contain("collaborator");
        refusal.Message.Should().Contain("this task is unchanged");

        await using IQuerySession verify = store.QuerySession();
        (await verify.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(TaskState.Published);
        (await EventsAsync(store, taskId, cts.Token))
            .Where(@event => @event is TrackerAssignmentWritten or TaskAssigned).Should().BeEmpty();
    }

    /// <summary>
    /// Jira refusing the write itself — a permission problem on the field, most often — is the same
    /// shape: the tenant's own words, the task untouched, and no event recorded for a write that
    /// did not happen.
    /// </summary>
    [Fact]
    public async Task Take_quotes_jiras_own_refusal_of_the_write_and_changes_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "jira", "TAKE-9", ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned(
            "jira", "TAKE-9", refuseWrite: "Field 'assignee' cannot be set. It is not on the appropriate screen.");

        Func<Task> take = () => TakeAsync(store, taskId, tracker, take: true, offer: null, cts.Token);
        DomainBusinessRuleException refusal = (await take.Should().ThrowAsync<DomainBusinessRuleException>()).Which;

        refusal.Message.Should().Contain("not on the appropriate screen");
        refusal.Message.Should().Contain("this task is unchanged");
        (await EventsAsync(store, taskId, cts.Token))
            .Where(@event => @event is TrackerAssignmentWritten or TaskAssigned).Should().BeEmpty();
    }

    /// <summary>
    /// The one case where the write may genuinely have landed: the tracker accepted it, and the
    /// read-back afterwards does not show this install holding. Nothing is recorded — a read-back
    /// is what an event is composed from, and there is nothing here to compose one out of — the
    /// task is left alone, and the refusal says plainly that the write may have gone through and
    /// that running the same command again is safe.
    /// </summary>
    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task A_write_the_read_back_cannot_confirm_records_nothing_and_says_so(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-10" : "710";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned(provider, key, writeIsInvisible: true);

        Func<Task> take = () => TakeAsync(store, taskId, tracker, take: true, offer: null, cts.Token);
        DomainBusinessRuleException refusal = (await take.Should().ThrowAsync<DomainBusinessRuleException>()).Which;

        refusal.Message.Should().Contain("did not show it holding");
        refusal.Message.Should().Contain("the write may well have landed");
        refusal.Message.Should().Contain("run the same command again");
        (await EventsAsync(store, taskId, cts.Token))
            .Where(@event => @event is TrackerAssignmentWritten or TrackerAssignmentObserved or TaskAssigned)
            .Should().BeEmpty();
    }

    /// <summary>
    /// The race the "never take a held item" refusal alone does not close, on the only provider
    /// that can produce it: two installs read the same GitHub issue as unassigned inside each
    /// other's write window, and because <c>gh issue edit --add-assignee</c> <em>adds</em> rather
    /// than replaces, both writes succeed and the issue ends up carrying both logins. Reading that
    /// back as <see cref="TrackerTakeVerdict.Taken"/> would let both installs claim the same card —
    /// silently and permanently, since being among several assignees passes the gate on every later
    /// read (Decisions Log #142) — so it is refused, nothing is recorded, the task is left untouched,
    /// and the sentence names who else is on the issue instead of telling the human to run it again
    /// (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_read_back_naming_somebody_else_beside_this_install_is_refused_as_contested()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "github", "713", ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned("github", "713", contender: "teammate");

        Func<Task> take = () => TakeAsync(store, taskId, tracker, take: true, offer: null, cts.Token);
        DomainBusinessRuleException refusal = (await take.Should().ThrowAsync<DomainBusinessRuleException>()).Which;

        refusal.Message.Should().Contain("teammate", "the install that arrives second is told who else is on it");
        refusal.Message.Should().Contain("taking the same item in the same moment");
        refusal.Message.Should().Contain("this task is unchanged");
        refusal.Message.Should().Contain(
            "Do not simply run this again",
            "an item assigned to both of them passes the gate for each, so a retry would claim it");
        tracker.Writes.Should().ContainSingle(
            "the write did land — that is exactly what makes this contested rather than refused");
        (await EventsAsync(store, taskId, cts.Token))
            .Where(@event => @event is TrackerAssignmentWritten or TrackerAssignmentObserved or TaskAssigned)
            .Should().BeEmpty("a contested read-back is the one observation this feature refuses to record");
    }

    /// <summary>
    /// Jira's assignee <c>PUT</c> answered 204 and only <see cref="JiraWriteExecutor"/>'s own
    /// existence read-back after it failed — which that executor's message says in as many words is
    /// not a refusal of the write. Calling it one would print "could not be assigned to this
    /// install" about a write that landed and refuse an assignment the take's own read-back confirms,
    /// so the take carries Jira's sentence along and goes on to the one read it actually rests on
    /// (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_failed_existence_read_back_is_not_a_refusal_and_the_assignee_read_still_decides()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "jira", "TAKE-13", ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned("jira", "TAKE-13", existenceReadBackFails: true);
        TrackerTake? take = await TakeAsync(store, taskId, tracker, take: true, offer: null, cts.Token);

        take!.Verdict.Should().Be(TrackerTakeVerdict.Taken);
        tracker.Writes.Should().ContainSingle();
        (await EventsAsync(store, taskId, cts.Token)).OfType<TrackerAssignmentWritten>()
            .Should().ContainSingle("the read-back is what an event is composed from, and it confirmed");
    }

    /// <summary>
    /// The same failure where the take's own read-back cannot settle it either: the refusal carries
    /// both halves — Jira's sentence about the read-back that failed, and what this install's own
    /// assignee read then showed — because a human deciding whether to write again needs to know the
    /// update itself succeeded.
    /// </summary>
    [Fact]
    public async Task An_unverified_write_the_assignee_read_cannot_confirm_carries_both_sentences()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "jira", "TAKE-14", ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned(
            "jira", "TAKE-14", writeIsInvisible: true, existenceReadBackFails: true);

        Func<Task> take = () => TakeAsync(store, taskId, tracker, take: true, offer: null, cts.Token);
        DomainBusinessRuleException refusal = (await take.Should().ThrowAsync<DomainBusinessRuleException>()).Which;

        refusal.Message.Should().Contain(
            "do not record this as a refusal of the write", "Jira's own sentence about its read-back is carried");
        refusal.Message.Should().Contain(
            "assigned to nobody", "and so is what this install's own assignee read then showed");
        refusal.Message.Should().Contain("run the same command again");
        (await EventsAsync(store, taskId, cts.Token))
            .Where(@event => @event is TrackerAssignmentWritten or TaskAssigned).Should().BeEmpty();
    }

    /// <summary>
    /// A tracker that cannot be read at all refuses the take rather than writing: a take that
    /// cannot see who holds an item cannot know it is taking it from nobody, which is the
    /// fails-closed doctrine of Decisions Log #142 applied to a write.
    /// </summary>
    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task Take_refuses_an_unreadable_tracker_rather_than_writing_blind(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-11" : "711";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unreadable(provider, key);

        Func<Task> take = () => TakeAsync(store, taskId, tracker, take: true, offer: null, cts.Token);
        DomainBusinessRuleException refusal = (await take.Should().ThrowAsync<DomainBusinessRuleException>()).Which;

        refusal.Message.Should().Contain("could not be read");
        refusal.Message.Should().Contain("cannot know it is taking it from nobody");
        tracker.Writes.Should().BeEmpty();
        tracker.GhCalls.Should().NotContain(arguments => arguments.Contains("edit"));
    }

    // ── the flag on a project with nothing to take ────────────────────────────────────────────

    /// <summary>
    /// <c>--take</c> where there is no gate to satisfy is refused rather than quietly honoured:
    /// the flag asks that the gate pass on its own, and with the gate off there is nothing to pass,
    /// so writing to somebody's tracker anyway would be this command doing more than it was asked
    /// on a guess about intent. The refusal names which half of the rule turned it down and how to
    /// turn the gate on.
    /// </summary>
    [Theory]
    [InlineData(true, "claim gate is off")]
    [InlineData(false, "no linked Jira card or GitHub issue")]
    public async Task Take_refuses_where_there_is_no_gate_to_satisfy(bool gateOff, string expected)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid projectId = DomainId.New();
        await SeedProjectAsync(store, projectId, gateOff ? ClaimGate.Off : ClaimGate.TrackerAssignee, cts.Token);
        Guid taskId = await SeedTaskAsync(
            store, projectId, gateOff ? new ExternalReference(WorkItemProvider.Jira, "TAKE-12") : null,
            ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned("jira", "TAKE-12");
        Func<Task> take = () => TakeAsync(store, taskId, tracker, take: true, offer: null, cts.Token);

        DomainValidationException refusal = (await take.Should().ThrowAsync<DomainValidationException>()).Which;
        refusal.Message.Should().Contain(expected);
        refusal.Message.Should().Contain(
            gateOff ? "--claim-gate tracker-assignee" : "h9k task link-jira",
            "the remedy has to match the reason — a gate that is already on is not the thing to turn on");
        tracker.Writes.Should().BeEmpty();
    }

    // ── the payload's own guardrail ───────────────────────────────────────────────────────────

    /// <summary>
    /// The write the take composes, checked against the guardrail it has to pass: an update
    /// carrying only <c>assignee</c> validates clean, because putting a person's name on a card is
    /// not moving it through anyone's states — which is exactly what
    /// <see cref="JiraWritePayload"/>'s forbidden-field list is about. The three neighbours are
    /// asserted alongside it so this test fails loudly rather than vacuously if that list ever
    /// grows to swallow the assignee field.
    /// </summary>
    [Fact]
    public void A_jira_update_carrying_only_assignee_passes_the_payloads_forbidden_field_check()
    {
        JiraWritePayload assignee = new(
            null, new Dictionary<string, string> { ["assignee"] = """{"accountId":"5b10a2844c20165700ede21g"}""" }, null);

        assignee.Invoking(payload => payload.Validate(JiraWriteOperation.Update)).Should().NotThrow();

        foreach (string forbidden in new[] { "status", "transition", "resolution", "resolutiondate" })
        {
            JiraWritePayload workflow = new(
                null, new Dictionary<string, string> { [forbidden] = "\"Done\"" }, null);
            workflow.Invoking(payload => payload.Validate(JiraWriteOperation.Update))
                .Should().Throw<DomainValidationException>(
                    "the fields the take must never send are still refused, which is what makes assignee's own pass meaningful")
                .WithMessage("*workflow states*");
        }
    }

    // ── driving the door ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The assign door's own pre-assignment step, which is what <c>h9k task assign</c> calls: it
    /// composes the assignment first, takes second, and saves last, so a refused take leaves the
    /// task untouched. Reproduced here rather than reaching into the command's
    /// <c>ExecuteAsync</c>, which opens its own store against the install's real database.
    /// </summary>
    private async Task<(TrackerTake? Take, TrackerClaimDecision? Decision)> ReadTakeBeforeAssigningAsync(
        DocumentStore store,
        Guid taskId,
        FakeTracker tracker,
        bool take,
        TrackerClaimCheck.TakeOffer? offer,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, token: cancellationToken))!;
        (TrackerTake? taken, TrackerClaimDecision? decision) = await TaskAssignCommand.TakeBeforeAssigningAsync(
            store, session, task, take, tracker.Taker(), offer, cancellationToken);
        return (taken, decision);
    }

    /// <summary>The same door, with the assignment actually composed and committed around the take — the full sequence a refusal has to leave alone.</summary>
    private async Task<TrackerTake?> TakeAsync(
        DocumentStore store,
        Guid taskId,
        FakeTracker tracker,
        bool take,
        TrackerClaimCheck.TakeOffer? offer,
        CancellationToken cancellationToken)
    {
        Guid ownerId = await SoleOwnerAsync(store, cancellationToken);
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, token: cancellationToken))!;
        await TaskAssignCommand.AppendAsync(session, task, ownerId, ownerId, cancellationToken);

        (TrackerTake? taken, TrackerClaimDecision? _) = await TaskAssignCommand.TakeBeforeAssigningAsync(
            store, session, task, take, tracker.Taker(), offer, cancellationToken);

        await session.SaveChangesAsync(cancellationToken);
        return taken;
    }

    private static async Task<Guid> SoleOwnerAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        return (await session.Query<OwnerDetails>().ToListAsync(cancellationToken))[0].Id;
    }

    private static async Task<IReadOnlyList<object>> EventsAsync(
        DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        IReadOnlyList<IEvent> stream = await session.Events.FetchStreamAsync(taskId, token: cancellationToken);
        return [.. stream.Select(@event => @event.Data)];
    }

    private static async Task<ExternalReference?> ReferenceAsync(
        DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, token: cancellationToken))!;
        return task.ExternalReference;
    }

    private static async Task<string> RepositoryPathAsync(
        DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, token: cancellationToken))!;
        return (await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken))!.RepositoryPath;
    }

    // ── seeding ───────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedGatedAsync(
        DocumentStore store, string provider, string key, Guid ownerId, CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        await SeedProjectAsync(store, projectId, ClaimGate.TrackerAssignee, cancellationToken);
        if (provider == "jira")
        {
            await SeedJiraConnectionAsync(store, cancellationToken);
        }

        return await SeedTaskAsync(
            store,
            projectId,
            provider == "jira"
                ? new ExternalReference(WorkItemProvider.Jira, key)
                : new ExternalReference(WorkItemProvider.GitHub, $"{Repository}#{key}"),
            ownerId,
            cancellationToken);
    }

    /// <summary>
    /// A task published and ready to assign, and deliberately no further: the state a refused take
    /// has to leave it in is only checkable if the take is what would have moved it.
    /// </summary>
    private static async Task<Guid> SeedTaskAsync(
        DocumentStore store,
        Guid projectId,
        ExternalReference? reference,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession seed = store.LightweightSession();
        seed.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Publishable(
            TaskDecider.Add(
                taskId, projectId, $"take the item behind {reference?.ToString() ?? "nothing"}", ["it is done"],
                TaskType.Chore, null, null, reference, Now, ownerId),
            ownerId, Now));
        await seed.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task SeedProjectAsync(
        DocumentStore store, Guid projectId, ClaimGate gate, CancellationToken cancellationToken)
    {
        // A real directory, because gh reads the repository from the directory it runs in and the
        // take passes this path straight through to the runner — the fakes never look at it, but a
        // path that does not exist would be a fact this seed asserted and never observed.
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"h9k-take-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        repositoryPaths.Add(repositoryPath);

        await using IDocumentSession seed = store.LightweightSession();
        seed.Store(new ProjectDetails
        {
            Id = projectId,
            Name = $"take-{projectId:N}"[..12],
            RepositoryPath = repositoryPath,
            RepositoryUrl = new Uri($"https://github.com/{Repository}"),
            BaseBranch = "main",
            ClaimGate = gate,
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await seed.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// One Jira connection, with its identity deliberately unrecorded — a connection registered
    /// before the accountId was captured, so the take's own read reads it live from
    /// <c>/rest/api/2/myself</c> and records it, which every Jira case here therefore exercises.
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

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    /// <summary>
    /// One item on one tracker, with state: it starts held by whoever the test says, the write is
    /// what changes hands, and the read-back afterwards reads the changed state — which is the only
    /// way a test can tell a read-back that proved something from one that was told the answer in
    /// advance.
    /// <para>
    /// Both providers live in one fake because a take's own shape is provider-agnostic (read,
    /// write, read) and every test here asserts the same three steps against whichever one it was
    /// given. <see cref="Writes"/> is the JSON body of every Jira field update and
    /// <see cref="GhCalls"/> every gh invocation, so "nothing was written" is asserted against what
    /// actually left the process rather than against a verdict the code under test chose.
    /// </para>
    /// </summary>
    private sealed class FakeTracker
    {
        private readonly string provider;
        private readonly string key;
        private readonly string? refuseWrite;
        private readonly bool unreadable;

        /// <summary>
        /// A write the tracker accepts and the read-back does not reflect — the genuinely ambiguous
        /// case (a write applied to a different field, a replica read behind the write, an
        /// automation that reassigned it in the same instant), which the take must refuse to record
        /// rather than claim.
        /// </summary>
        private readonly bool writeIsInvisible;

        /// <summary>
        /// Somebody else's login, which appears on the item at the moment this install's own write
        /// lands and not before — the GitHub race in a fake: the read that licensed the write still
        /// showed the item assigned to nobody, and it is the read-back that finds two people on it.
        /// </summary>
        private readonly string? contender;

        /// <summary>
        /// A failure of the existence read-back <see cref="JiraWriteExecutor"/> runs after its own
        /// <c>PUT</c> has already answered 204 — a failure of the verification and not of the write,
        /// which the executor's own message says outright. The assignee read the take then makes for
        /// itself is answered normally, because it is a different call on a different field.
        /// </summary>
        private readonly bool existenceReadBackFails;

        private string? holder;
        private bool contenderArrived;

        private FakeTracker(
            string provider,
            string key,
            string? holder,
            string? refuseWrite,
            bool unreadable,
            bool writeIsInvisible,
            string? contender,
            bool existenceReadBackFails)
        {
            this.provider = provider;
            this.key = key;
            this.holder = holder;
            this.refuseWrite = refuseWrite;
            this.unreadable = unreadable;
            this.writeIsInvisible = writeIsInvisible;
            this.contender = contender;
            this.existenceReadBackFails = existenceReadBackFails;
        }

        public static FakeTracker Unassigned(
            string provider,
            string key,
            string? refuseWrite = null,
            bool writeIsInvisible = false,
            string? contender = null,
            bool existenceReadBackFails = false) =>
            new(provider, key, null, refuseWrite, unreadable: false, writeIsInvisible, contender, existenceReadBackFails);

        public static FakeTracker HeldBy(string provider, string key, string holder) =>
            new(provider, key, holder, null, false, false, null, false);

        public static FakeTracker Unreadable(string provider, string key) =>
            new(provider, key, null, null, true, false, null, false);

        /// <summary>Every Jira request, so a test can prove no transition endpoint was touched.</summary>
        public List<JiraRequest> Requests { get; } = [];

        /// <summary>The JSON body of every field update actually sent, Jira and GitHub alike (gh's write records its own argument list too).</summary>
        public List<string> Writes { get; } = [];

        /// <summary>Every gh invocation's arguments, in order.</summary>
        public List<IReadOnlyList<string>> GhCalls { get; } = [];

        public TrackerAssignmentTake Taker() => new(GhRunner(), JiraRequester());

        public TrackerClaimGate Gate() => new(GhRunner(), JiraRequester());

        private ProcessRunner GhRunner() => (_, arguments, _, _) =>
        {
            GhCalls.Add(arguments);
            return Task.FromResult(AnswerGh(arguments));
        };

        private ProcessResult AnswerGh(IReadOnlyList<string> arguments)
        {
            if (arguments is ["api", "user", ..])
            {
                return new ProcessResult(0, GitHubLogin + "\n", string.Empty);
            }

            if (!arguments.Contains(key))
            {
                return new ProcessResult(1, string.Empty, "Could not resolve to an Issue: some other test's issue");
            }

            if (arguments.Contains("edit"))
            {
                if (refuseWrite is { } refusal)
                {
                    return new ProcessResult(1, string.Empty, refusal);
                }

                Writes.Add(string.Join(" ", arguments));
                if (!writeIsInvisible)
                {
                    holder = arguments[^1];
                }

                // Independent of this install's own write landing or not: the contender's arrival is
                // somebody else's act, and what the read-back then finds is the whole question.
                contenderArrived = true;
                return new ProcessResult(0, string.Empty, string.Empty);
            }

            return unreadable
                ? new ProcessResult(1, string.Empty, "error connecting to api.github.com")
                : new ProcessResult(0, AssigneesJson(), string.Empty);
        }

        /// <summary>Who the issue carries right now, in gh's own <c>--json assignees</c> shape.</summary>
        private string AssigneesJson()
        {
            List<string> logins = [];
            if (holder is { } login)
            {
                logins.Add(login);
            }

            if (contenderArrived && contender is { } other)
            {
                logins.Add(other);
            }

            return "{\"assignees\":["
                + string.Join(",", logins.Select(name => "{\"login\":\"" + name + "\"}"))
                + "]}";
        }

        private JiraRequester JiraRequester() => (request, _) =>
        {
            Requests.Add(request);
            return Task.FromResult(AnswerJira(request));
        };

        private JiraResponse AnswerJira(JiraRequest request)
        {
            if (request.Url.AbsolutePath.EndsWith("/myself", StringComparison.Ordinal))
            {
                return new JiraResponse(
                    200, $$"""{"accountId":"{{JiraAccountId}}","displayName":"{{JiraDisplayName}}"}""");
            }

            if (!request.Url.ToString().Contains(key, StringComparison.Ordinal))
            {
                return new JiraResponse(404, """{"errorMessages":["some other test's card"]}""");
            }

            if (request.Method == HttpMethod.Put)
            {
                if (refuseWrite is { } refusal)
                {
                    // Concatenated rather than an interpolated raw literal: the JSON's own trailing
                    // braces run three deep, which no number of '$' delimiters reads cleanly — the
                    // same reason ClaimGateTests spells its own card bodies this way.
                    return new JiraResponse(
                        400,
                        "{\"errors\":{\"assignee\":" + JsonSerializer.Serialize(refusal) + "}}");
                }

                Writes.Add(request.JsonBody ?? string.Empty);
                if (!writeIsInvisible)
                {
                    holder = JiraAccountId;
                }

                return new JiraResponse(204, string.Empty);
            }

            if (unreadable)
            {
                return new JiraResponse(503, """{"errorMessages":["Service temporarily unavailable"]}""");
            }

            // The update's own existence-only read-back asks for fields=key; the take's assignee
            // read-back asks for fields=assignee. Both are GETs on the same card, so the query is
            // what tells them apart.
            if (request.Url.Query.Contains("fields=assignee", StringComparison.Ordinal))
            {
                return new JiraResponse(
                    200,
                    holder is { } accountId
                        ? "{\"key\":\"" + key + "\",\"fields\":{\"assignee\":{\"accountId\":\"" + accountId
                            + "\",\"displayName\":\"" + Displayed(accountId) + "\"}}}"
                        : "{\"key\":\"" + key + "\",\"fields\":{\"assignee\":null}}");
            }

            // What is left is the executor's own existence read-back (fields=key), which a caller
            // may want to fail after the PUT has already answered 204.
            return existenceReadBackFails
                ? new JiraResponse(503, """{"errorMessages":["Service temporarily unavailable"]}""")
                : new JiraResponse(200, "{\"key\":\"" + key + "\"}");
        }

        /// <summary>The display name a fake tenant puts beside an accountId, so a holder reads as a person.</summary>
        private static string Displayed(string accountId) =>
            accountId == JiraAccountId ? JiraDisplayName : accountId;
    }
}
