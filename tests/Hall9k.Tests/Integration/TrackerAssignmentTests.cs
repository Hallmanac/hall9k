using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.JiraWrites;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// This install's own identity in the tracker, from the take that writes it to the gate that
/// reads it back to the retry that gets the write through — one container across the three seams
/// below (idea 64c75e43, Decisions Log #142, #143). They were three classes for eleven to
/// seventeen tests each and already shared this discipline: every assertion is about a specific
/// task id, the ceiling is set far above anything a neighbour could occupy, and every tracker
/// fake answers only about its own item key and refuses every other — because
/// <see cref="PostgresFixture"/> shares one database across a class and
/// <c>NodeContext.InitializeAsync</c> reuses this machine's one node and owner, so a task another
/// test left Queued is a real, expected neighbour. That is what makes the three safe beside each
/// other too, and it is why the seeding helpers they had three near-identical copies of
/// (<see cref="EnsureJiraConnectionAsync"/>, <c>SeedProjectAsync</c>) are now one each — the
/// connection seeder emphatically so, since the install holds exactly one Jira connection and two
/// first-wins seeders for it would decide the credential and account by test order alone.
/// <para>
/// <c>h9k task assign --take</c> — claiming stops being a two-place act, because one command
/// moves the tracker and the board together and the gate then passes on its own. Every tracker
/// fake here is <em>stateful</em>: the item starts unassigned and the write is what makes the
/// read-back show a holder, because the read-back is the whole point of the feature — a fake that
/// answered "assigned to you" before the write would pass a test that proves nothing.
/// </para>
/// <para>
/// A project whose claim gate is on — the tracker's own assignment is the one act that hands out
/// work, so two teammates' installs cannot both run the same card.
/// </para>
/// <para>
/// Both providers are exercised at every outcome and every door in both of those seams, because
/// the two differ in exactly the places that could break independently: Jira's write is a REST
/// field update through <see cref="JiraWriteExecutor"/> and its identity is an <c>accountId</c>
/// recorded on a registered connection, GitHub's is <c>gh issue edit --add-assignee</c> with a
/// login read live on every check; GitHub compares that login case-insensitively where a Jira
/// <c>accountId</c> is compared byte for byte; Jira has one assignee where a GitHub issue may
/// have several; and only GitHub has a collaborator requirement to refuse on.
/// </para>
/// <para>
/// The write retry — the acceptance criterion that daemon sweep is built around (Brian's design,
/// 2026-08-28; the write path's own transport moved off the Atlassian CLI (twg) onto hall9k's REST
/// client, Decisions Log #114): a rejected credential is a handled, expected state, and the
/// identical payload succeeds on retry once the connection is fixed, rather than being lost.
/// Nothing exercised <see cref="JiraWriteRetryEngine.PollOnceAsync"/> before it (independent
/// pre-PR review, cycle 1) — <c>JiraWriteExecutorTests</c> covers only argument construction and
/// failure classification against a fake HTTP response, and <c>TaskJiraWriteTests</c> only the
/// decider and projections in isolation — so a regression in the sweep's own filter, its payload
/// round-trip, or which write id an outcome gets recorded against could ship green.
/// </para>
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class TrackerAssignmentTests : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly PostgresFixture postgres;

    /// <summary>
    /// The one variable the class's one Jira connection records as its credential reference. It
    /// is a single name rather than one per seam because
    /// <see cref="EnsureJiraConnectionAsync"/> registers a single install-wide connection for
    /// every seam here (see that method's own doc comment for why a second one is a refusal), so
    /// a per-seam name would only mean the connection recorded whichever seam's test happened to
    /// run first — which credential a real <c>CredentialVault</c> then resolves would depend on
    /// xUnit's name-derived ordering (independent pre-PR review, cycle 1, conformance lens). The
    /// production name is the one kept, because the write-retry seam's own
    /// <see cref="JiraWriteRetryEngine"/> resolves this reference for real before it can build
    /// the executor a sweep retries with.
    /// </summary>
    private const string JiraTokenVariable = "JIRA_TOKEN";

    private const string JiraAccountId = "5b10a2844c20165700ede21g";
    private const string JiraDisplayName = "Brian Hall";
    private const string GitHubLogin = "Hallmanac";
    private const string Repository = "Hallmanac/hall9k";
    private static readonly Uri Site = new("https://hall9k.atlassian.net");
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> repositoryPaths = [];

    /// <summary>
    /// Every seam here reads its credential out of the one variable the class's one Jira
    /// connection names, <see cref="JiraTokenVariable"/> — set here and cleared in
    /// <see cref="Dispose"/>. The take, the gate and the write retry each had a variable of their
    /// own while they were three classes; they cannot keep three now that they share one
    /// connection, because only one of the three names could ever be the one recorded on it.
    /// </summary>
    public TrackerAssignmentTests(PostgresFixture postgres)
    {
        this.postgres = postgres;
        Environment.SetEnvironmentVariable(JiraTokenVariable, "a-token");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(JiraTokenVariable, null);
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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-1" : "701";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned(provider, key);
        TrackerTake? take = await TakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, cts.Token);

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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "jira", "TAKE-2", ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned("jira", "TAKE-2");
        await TakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, cts.Token);

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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "github", "702", ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned("github", "702");
        await TakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, cts.Token);

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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-3" : "703";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.HeldBy(provider, key, provider == "jira" ? "someone-else" : "teammate");

        Func<Task> take = () => TakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, cts.Token);
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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-4" : "704";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        // Deliberately spelt in the other case for GitHub: a login is matched case-insensitively,
        // and a take that re-wrote an item because "hallmanac" is not "Hallmanac" would put a
        // second, identical assignment on somebody's board every time it ran.
        FakeTracker tracker = FakeTracker.HeldBy(
            provider, key, provider == "jira" ? JiraAccountId : GitHubLogin.ToLowerInvariant());

        TrackerTake? take = await TakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, cts.Token);

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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-5" : "705";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned(provider, key);
        List<TrackerClaimDecision> offered = [];
        TrackerTake? take = await TakeAsync(
            store, ownerId, taskId, tracker, take: false,
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "github", "708", ownerId, cts.Token);

        const string GhSaid =
            "failed to update https://api.github.com/graphql: could not add assignee: "
            + "'Hallmanac' is not a collaborator on Hallmanac/hall9k";
        FakeTracker tracker = FakeTracker.Unassigned("github", "708", refuseWrite: GhSaid);

        Func<Task> take = () => TakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, cts.Token);
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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "jira", "TAKE-9", ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned(
            "jira", "TAKE-9", refuseWrite: "Field 'assignee' cannot be set. It is not on the appropriate screen.");

        Func<Task> take = () => TakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, cts.Token);
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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-10" : "710";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned(provider, key, writeIsInvisible: true);

        Func<Task> take = () => TakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, cts.Token);
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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "github", "713", ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned("github", "713", contender: "teammate");

        Func<Task> take = () => TakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, cts.Token);
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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "jira", "TAKE-13", ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned("jira", "TAKE-13", existenceReadBackFails: true);
        TrackerTake? take = await TakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, cts.Token);

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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "jira", "TAKE-14", ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned(
            "jira", "TAKE-14", writeIsInvisible: true, existenceReadBackFails: true);

        Func<Task> take = () => TakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, cts.Token);
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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "TAKE-11" : "711";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unreadable(provider, key);

        Func<Task> take = () => TakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, cts.Token);
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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid projectId = DomainId.New();
        await SeedProjectAsync(store, projectId, gateOff ? ClaimGate.Off : ClaimGate.TrackerAssignee, cts.Token);
        Guid taskId = await SeedTaskAsync(
            store, projectId, gateOff ? new ExternalReference(WorkItemProvider.Jira, "TAKE-12") : null,
            ownerId, cts.Token);

        FakeTracker tracker = FakeTracker.Unassigned("jira", "TAKE-12");
        Func<Task> take = () => TakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, cts.Token);

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
        Guid ownerId,
        Guid taskId,
        FakeTracker tracker,
        bool take,
        TrackerClaimCheck.TakeOffer? offer,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, token: cancellationToken))!;
        await TaskAssignCommand.AppendAsync(session, task, ownerId, ownerId, cancellationToken);

        (TrackerTake? taken, TrackerClaimDecision? _) = await TaskAssignCommand.TakeBeforeAssigningAsync(
            store, session, task, take, tracker.Taker(), offer, cancellationToken);

        await session.SaveChangesAsync(cancellationToken);
        return taken;
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
            await EnsureJiraConnectionAsync(store, ownerId, cancellationToken);
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
                    // same reason this class's other Jira card bodies are spelt this way.
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

    // ── the claim gate that reads the assignee back ──
    private static readonly DateTimeOffset ClaimGateNow = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "GATE-1" : "601";
        (Guid projectId, Guid taskId) = await SeedGatedProjectAndTaskAsync(store, provider, key, ownerId, cts.Token);

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
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "GATE-2" : "602";
        (Guid projectId, Guid taskId) = await SeedGatedProjectAndTaskAsync(store, provider, key, ownerId, cts.Token);

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
        DocumentStore store = postgres.Store;
        BootstrapContext context = await BootstrapAsync(store, cts.Token);
        // A Jira key has a shape (PROJ-123), and a gate that cannot parse one refuses for that
        // reason instead — which is a real behaviour, but not the one this test is about.
        string key = provider == "jira"
            ? (door == "work" ? "GATE-11" : "GATE-12")
            : (door == "work" ? "611" : "612");
        (Guid projectId, Guid taskId) = await SeedGatedProjectAndTaskAsync(store, provider, key, context.OwnerId, cts.Token);

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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        string key = provider == "jira" ? "GATE-3" : "603";
        (Guid projectId, Guid taskId) = await SeedGatedProjectAndTaskAsync(store, provider, key, node.OwnerId, cts.Token);

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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        string key = provider == "jira"
            ? (authenticationRefusal ? "GATE-4" : "GATE-5")
            : (authenticationRefusal ? "604" : "605");
        (Guid projectId, Guid taskId) = await SeedGatedProjectAndTaskAsync(store, provider, key, node.OwnerId, cts.Token);

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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        const string key = "GATE-6";
        (Guid projectId, Guid taskId) = await SeedGatedProjectAndTaskAsync(store, "jira", key, node.OwnerId, cts.Token);

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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        const string key = "606";
        (Guid projectId, Guid taskId) = await SeedGatedProjectAndTaskAsync(store, "github", key, node.OwnerId, cts.Token);

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
        DocumentStore store = postgres.Store;
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
                    ClaimGateNow, node.OwnerId),
                node.OwnerId, ClaimGateNow);
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
            new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance),
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

    private async Task<(Guid ProjectId, Guid TaskId)> SeedGatedProjectAndTaskAsync(
        DocumentStore store, string provider, string key, Guid ownerId, CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        await SeedProjectAsync(store, projectId, ClaimGate.TrackerAssignee, cancellationToken);
        if (provider == "jira")
        {
            await EnsureJiraConnectionAsync(store, ownerId, cancellationToken);
        }

        ExternalReference reference = provider == "jira"
            ? new ExternalReference(WorkItemProvider.Jira, key)
            : new ExternalReference(WorkItemProvider.GitHub, $"{Repository}#{key}");

        await using IDocumentSession seed = store.LightweightSession();
        seed.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
            TaskDecider.Add(
                taskId, projectId, $"claim gate over {reference}", ["it is done"], TaskType.Chore,
                null, null, reference, ClaimGateNow, ownerId),
            ownerId, ClaimGateNow));
        await seed.SaveChangesAsync(cancellationToken);
        return (projectId, taskId);
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

    // ── the retry that gets a stuck Jira write through ──
    // The registered connection every test seeds (EnsureJiraConnectionAsync) records its
    // credential as JiraTokenVariable, which is exactly what the daemon's own account resolution
    // (JiraWriteRetryEngine building a JiraAccount from the connection) reads at retry time —
    // unlike the twg-process era, where the credential never mattered to a fake process runner,
    // JiraWriteRetryEngine now resolves a real CredentialVault reference before it can build the
    // executor a sweep retries with, so this has to actually resolve.
    private static readonly DateTimeOffset JiraWriteNow = new(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);
    private static JiraWriteExecutor Executor(RecordingJiraRequester requester) =>
        new(JiraAccount.WithTokenInHand(Site, "brian@hallmanac.com", "a-token"), requester.Requester);

    private static RecordingJiraRequester AuthRejected() =>
        RecordingJiraRequester.Succeeding(401, """{"errorMessages":["denied"]}""");

    [Fact]
    public async Task An_auth_stuck_write_succeeds_on_retry_once_the_sweep_reattempts_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-123"), cts.Token);

        RecordingJiraRequester rejecting = AuthRejected();
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult submitted = await JiraWriteCoordinator.SubmitAsync(
                session, taskId, JiraWriteOperation.Comment, issueKey: null,
                new JiraWritePayload(null, null, "The pull request merged."), JiraProjectKey.None,
                DomainId.New(), Executor(rejecting), cts.Token);

            submitted.Outcome.Should().Be(JiraWriteOutcome.PendingAuthentication);
        }

        TaskAggregate? stuck = await LoadAsync(store, taskId, cts.Token);
        stuck!.PendingJiraWriteIsAuthFailure.Should().BeTrue("the payload has to survive to be retried, not be lost");

        // A fresh, working credential — the identical comment goes through this time.
        RecordingJiraRequester reauthenticated = RecordingJiraRequester.Succeeding(200, """{"key":"PROJ-123"}""");
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, reauthenticated.Requester, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Should().Be(new JiraWriteRetrySweepResult(Retried: 1, Succeeded: 1));
        TaskAggregate? resolved = await LoadAsync(store, taskId, cts.Token);
        resolved!.PendingJiraWriteId.Should().BeNull("the retry finished the request it already made rather than losing it");
    }

    /// <summary>
    /// An auth-classified write has no retry ceiling by design — the sweep re-attempts it on every
    /// <see cref="DaemonOptions.JiraWriteRetryInterval"/> for as long as the connection stays
    /// rejected — so a rejected credential left unattended must not grow the task's stream by one
    /// <see cref="JiraWriteFailed"/> per sweep forever (independent pre-PR review, adversarial
    /// lens, cycle 5). Jira answers with the identical 401 on every call here, so only the first
    /// attempt's failure should land on the stream; the second and third sweeps still retry (the
    /// write itself is not abandoned) but record nothing new.
    /// </summary>
    [Fact]
    public async Task Repeated_identical_auth_failures_do_not_grow_the_stream_without_bound()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-321"), cts.Token);

        RecordingJiraRequester rejecting = AuthRejected();
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult submitted = await JiraWriteCoordinator.SubmitAsync(
                session, taskId, JiraWriteOperation.Comment, issueKey: null,
                new JiraWritePayload(null, null, "The pull request merged."), JiraProjectKey.None,
                DomainId.New(), Executor(rejecting), cts.Token);

            submitted.Outcome.Should().Be(JiraWriteOutcome.PendingAuthentication);
        }

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, AuthRejected().Requester, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult second = await engine.PollOnceAsync(cts.Token);
        JiraWriteRetrySweepResult third = await engine.PollOnceAsync(cts.Token);

        second.Should().Be(new JiraWriteRetrySweepResult(Retried: 1, Succeeded: 0));
        third.Should().Be(new JiraWriteRetrySweepResult(Retried: 1, Succeeded: 0));

        TaskAggregate? stuck = await LoadAsync(store, taskId, cts.Token);
        stuck!.PendingJiraWriteIsAuthFailure.Should().BeTrue("the sweep must keep retrying, not give up on the write");

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<IEvent> stream = await query.Events.FetchStreamAsync(taskId, token: cts.Token);
        stream.Select(recorded => recorded.Data).OfType<JiraWriteFailed>().Should().ContainSingle(
            "Jira gave the identical answer on every attempt, so only the first failure is new information");

        // This test's own write is deliberately left auth-stuck above so the assertions could
        // observe it — cleaned up here so it does not leak into a later test's own sweep
        // (every test in this class shares one Postgres fixture, and so one database).
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate current = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.RecordJiraWriteFailure(
                current, current.PendingJiraWriteId!.Value,
                "Test cleanup: ending the auth-stuck write so it does not leak into a later test.",
                isAuthFailure: false, JiraWriteNow));
            await session.SaveChangesAsync(cts.Token);
        }
    }

    /// <summary>
    /// Archiving a project (task: a project can be archived, listed as archived, reactivated, and
    /// renamed) stops this sweep from retrying an auth-stuck write against it, the same skip
    /// CloseoutEngine and the render sweep already give an archived project — without it, an
    /// auth-stuck write under an archived project kept retrying against a real Jira site
    /// (independent pre-PR review, cycle 1, conformance lens; cycle 2 verify pass).
    /// </summary>
    [Fact]
    public async Task An_auth_stuck_write_under_an_archived_project_is_left_pending_rather_than_retried()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(
            store, new ExternalReference(WorkItemProvider.Jira, "PROJ-987"), cts.Token, archiveProject: true);

        RecordingJiraRequester rejecting = AuthRejected();
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult submitted = await JiraWriteCoordinator.SubmitAsync(
                session, taskId, JiraWriteOperation.Comment, issueKey: null,
                new JiraWritePayload(null, null, "The pull request merged."), JiraProjectKey.None,
                DomainId.New(), Executor(rejecting), cts.Token);

            submitted.Outcome.Should().Be(JiraWriteOutcome.PendingAuthentication);
        }

        RecordingJiraRequester mustNotRun = RecordingJiraRequester.RespondingTo(
            _ => throw new InvalidOperationException("an archived project's write must never be retried"));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, mustNotRun.Requester, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Should().Be(new JiraWriteRetrySweepResult(Retried: 0, Succeeded: 0), "the project is archived");
        TaskAggregate? stillStuck = await LoadAsync(store, taskId, cts.Token);
        stillStuck!.PendingJiraWriteIsAuthFailure.Should().BeTrue("the write stays pending until the project is reactivated");
    }

    [Fact]
    public async Task A_failure_that_is_not_about_authentication_is_left_for_a_freshly_composed_write()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-456"), cts.Token);

        RecordingJiraRequester refusing = RecordingJiraRequester.Succeeding(
            400, """{"errorMessages":["field 'customfield_10010' is required"]}""");
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult submitted = await JiraWriteCoordinator.SubmitAsync(
                session, taskId, JiraWriteOperation.Comment, issueKey: null,
                new JiraWritePayload(null, null, "The pull request merged."), JiraProjectKey.None,
                DomainId.New(), Executor(refusing), cts.Token);

            submitted.Outcome.Should().Be(JiraWriteOutcome.Failed);
        }

        RecordingJiraRequester mustNotRun = RecordingJiraRequester.RespondingTo(
            _ => throw new InvalidOperationException("a non-auth failure must not be retried by the sweep"));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, mustNotRun.Requester, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Should().Be(new JiraWriteRetrySweepResult(Retried: 0, Succeeded: 0));
    }

    [Fact]
    public async Task A_queued_merge_notice_waits_behind_an_outstanding_write_then_drains_once_it_clears()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-789"), cts.Token);

        // Closeout tried to submit the merge comment while another write was still outstanding on
        // this task and queued it instead of losing it (CloseoutEngine.QueueJiraMergeNoticeAsync).
        RecordingJiraRequester rejecting = AuthRejected();
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult submitted = await JiraWriteCoordinator.SubmitAsync(
                session, taskId, JiraWriteOperation.Comment, issueKey: null,
                new JiraWritePayload(null, null, "An earlier comment."), JiraProjectKey.None,
                DomainId.New(), Executor(rejecting), cts.Token);
            submitted.Outcome.Should().Be(JiraWriteOutcome.PendingAuthentication);

            TaskAggregate outstanding = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.QueueJiraMergeNotice(outstanding, JiraWriteNow));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingJiraRequester reauthenticated = RecordingJiraRequester.Succeeding(200, """{"key":"PROJ-789"}""");
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, reauthenticated.Requester, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

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
    /// Archiving a project (task: a project can be archived, listed as archived, reactivated, and
    /// renamed) stops this sweep from draining a queued merge notice against it, the same skip
    /// CloseoutEngine and the render sweep already give an archived project — without it, a queued
    /// notice under an archived project drained to a real Jira write the moment nothing blocked it
    /// any more (independent pre-PR review, cycle 1, conformance lens; cycle 2 verify pass).
    /// </summary>
    [Fact]
    public async Task A_queued_merge_notice_under_an_archived_project_is_left_queued_rather_than_drained()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(
            store, new ExternalReference(WorkItemProvider.Jira, "PROJ-135"), cts.Token, archiveProject: true);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.QueueJiraMergeNotice(task, JiraWriteNow));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingJiraRequester mustNotRun = RecordingJiraRequester.RespondingTo(
            _ => throw new InvalidOperationException("an archived project's queued notice must never drain"));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, mustNotRun.Requester, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Should().Be(
            new JiraWriteRetrySweepResult(Retried: 0, Succeeded: 0, MergeNoticesDrained: 0),
            "the project is archived");
        TaskAggregate? stillQueued = await LoadAsync(store, taskId, cts.Token);
        stillQueued!.HasQueuedJiraMergeNotice.Should().BeTrue("the notice stays queued until the project is reactivated");
    }

    /// <summary>
    /// <c>h9k pr resolve</c>'s own archived-project refusal (task: a project can be archived,
    /// listed as archived, reactivated, and renamed), the same guard <c>TaskAssignCommand.AppendAsync</c>
    /// gives h9k task assign — without it, this command reopened a Done task straight to Queued,
    /// which <c>DispatchEngine.ReadQueueAsync</c> then filters out for an archived project,
    /// stranding it invisibly rather than dispatching a follow-up (independent pre-PR review,
    /// cycle 1, adversarial lens; cycle 2 verify pass). Exercised directly against
    /// <see cref="PullRequestResolveCommand.RefuseIfArchivedAsync"/> rather than through the
    /// command's own <c>ExecuteAsync</c>, which opens its own store against the install's real
    /// database.
    /// </summary>
    [Fact]
    public async Task A_pull_request_resolve_under_an_archived_project_is_refused_before_reopening()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(
            store, new ExternalReference(WorkItemProvider.Jira, "PROJ-246"), cts.Token, archiveProject: true);

        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;

        Func<Task> refuse = () => PullRequestResolveCommand.RefuseIfArchivedAsync(session, task.ProjectId, cts.Token);

        DomainValidationException refusal = (await refuse.Should().ThrowAsync<DomainValidationException>()).Which;
        refusal.Message.Should().Contain("is archived").And.Contain("h9k project reactivate");
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
    /// requester throws if it is ever called, since the fixed code must refuse before reaching it.
    /// </summary>
    [Fact]
    public async Task A_queued_merge_notice_survives_a_racing_write_that_beats_it_to_the_outstanding_write_guard()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid targetTaskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-222"), cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate target = (await session.Events.AggregateStreamAsync<TaskAggregate>(targetTaskId, token: cts.Token))!;
            session.Events.Append(targetTaskId, TaskDecider.QueueJiraMergeNotice(target, JiraWriteNow));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingJiraRequester mustNotRun = RecordingJiraRequester.RespondingTo(
            _ => throw new InvalidOperationException(
                "the notice's own submit must be refused before it ever reaches Jira"));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, mustNotRun.Requester, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance)
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
                    target, JiraWriteOperation.Comment, issueKey: null, "{}", DomainId.New(), JiraWriteNow, DomainId.New()));
                await racingSession.SaveChangesAsync(ct);
            },
        };

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Should().Be(new JiraWriteRetrySweepResult(Retried: 0, Succeeded: 0, MergeNoticesDrained: 0));

        TaskAggregate? afterSweep = await LoadAsync(store, targetTaskId, cts.Token);
        afterSweep!.HasQueuedJiraMergeNotice.Should().BeTrue(
            "the notice was refused before it ever reached Jira, so it must stay queued rather than be marked attempted and dropped");
        afterSweep.PendingJiraWriteId.Should().NotBeNull("the racing write is what is actually outstanding now, not the notice's own attempt");

        // This test's own racing write, and the notice it left genuinely queued, are deliberately
        // left unresolved above so the assertions could observe them — cleaned up here so neither
        // lingers for a later test's own sweep to pick up (every test in this class shares one
        // Postgres fixture, and so one database).
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate afterRace = (await session.Events.AggregateStreamAsync<TaskAggregate>(targetTaskId, token: cts.Token))!;
            session.Events.Append(targetTaskId, TaskDecider.RecordJiraWriteFailure(
                afterRace, afterRace.PendingJiraWriteId!.Value,
                "Test cleanup: ending the racing write so it does not leak into a later test.",
                isAuthFailure: false, JiraWriteNow));
            await session.SaveChangesAsync(cts.Token);

            TaskAggregate afterRaceCleared = (await session.Events.AggregateStreamAsync<TaskAggregate>(targetTaskId, token: cts.Token))!;
            session.Events.Append(targetTaskId, TaskDecider.RecordJiraMergeNoticeAttempted(afterRaceCleared, JiraWriteNow));
            await session.SaveChangesAsync(cts.Token);
        }
    }

    /// <summary>
    /// Jira's own comment call can succeed while this write's own outcome still fails to record —
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
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-654"), cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.QueueJiraMergeNotice(task, JiraWriteNow));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                // Jira's own comment call already succeeded by the time the mandatory read-back
                // runs; racing a second write outcome in here, before control returns to
                // AttemptAsync's own RecordSuccessAsync, reproduces "something committed after
                // Jira's call but before this write's own success could be recorded" without
                // needing to fault-inject Postgres.
                using IDocumentSession racing = store.LightweightSession();
                TaskAggregate current = racing.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token)
                    .GetAwaiter().GetResult()!;
                racing.Events.Append(taskId, TaskDecider.RecordJiraWriteFailure(
                    current, current.PendingJiraWriteId!.Value, "Raced by another write.", isAuthFailure: false, JiraWriteNow));
                racing.SaveChangesAsync(cts.Token).GetAwaiter().GetResult();
                return new JiraResponse(200, """{"key":"PROJ-654"}""");
            }

            return new JiraResponse(201, "{}");
        });

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, requester.Requester, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.MergeNoticesDrained.Should().Be(1, "the notice is marked attempted even though its own outcome could not be recorded");
        TaskAggregate? after = await LoadAsync(store, taskId, cts.Token);
        after!.HasQueuedJiraMergeNotice.Should().BeFalse(
            "attempted exactly once — auto-retrying an unwatched comment risks posting it twice");

        RecordingJiraRequester mustNotRunAgain = RecordingJiraRequester.RespondingTo(
            _ => throw new InvalidOperationException("the notice must not be retried automatically once marked attempted"));
        JiraWriteRetryEngine secondEngine = new(
            store, node, mustNotRunAgain.Requester, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult second = await secondEngine.PollOnceAsync(cts.Token);

        second.MergeNoticesDrained.Should().Be(0, "there is nothing left queued for a later sweep to pick up");
    }

    /// <summary>
    /// The cancellation-grace hole independent pre-PR review, cycle 1 (conformance lens) found: a
    /// cancellation firing between Jira's own call returning and this method's own re-aggregation
    /// read (the shape a graceful <c>h9k daemon stop</c> mid-drain leaves behind) used to throw on
    /// the already-fired ambient token immediately, before the queue marker could ever be cleared —
    /// and unlike a stuck write, nothing backstops a queued notice on a ceiling, so the identical
    /// comment would repost for certain on the next sweep. Simulated here by having the fake
    /// requester's own read-back call cancel the ambient token and throw, reproducing
    /// <c>JiraWriteCoordinator.AttemptAsync</c>'s own cancellation catch recording this write's
    /// failure under its own short grace period and returning normally — exactly the state
    /// <c>DrainMergeNoticeAsync</c>'s own post-submit bookkeeping has to survive.
    /// </summary>
    [Fact]
    public async Task A_merge_notice_drain_clears_its_queue_marker_despite_a_cancellation_that_fired_after_jira_ran()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-741"), cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.QueueJiraMergeNotice(task, JiraWriteNow));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                // Jira's own comment call already posted by the time this mandatory read-back runs;
                // the daemon stopping right here is what leaves AttemptAsync's own cancellation
                // catch to record this write's outcome under its own grace period and return
                // normally, with the ambient token already fired for everything downstream.
                cts.Cancel();
                throw new OperationCanceledException();
            }

            return new JiraResponse(201, "{}");
        });

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, requester.Requester, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.MergeNoticesDrained.Should().Be(1, "the queue marker must clear even though the ambient token fired mid-drain");
        TaskAggregate? after = await LoadAsync(store, taskId, CancellationToken.None);
        after!.HasQueuedJiraMergeNotice.Should().BeFalse(
            "left set, the next sweep would repost the identical comment a second time — a comment has no dedup gate");
    }

    /// <summary>
    /// A second, narrower cancellation-grace hole independent pre-PR review, cycle 2 found: the
    /// test above cancels while the read-back <em>call itself</em> fails, which
    /// <c>JiraWriteCoordinator.AttemptAsync</c>'s own Jira-call catch already handles. Here the
    /// read-back succeeds — the comment call posted and verified — and the ambient token only
    /// fires afterward, in the window <c>RecordSuccessAsync</c>'s own aggregate-and-save occupies.
    /// That window sits outside every catch around the Jira call, so the cancellation used to escape
    /// <c>AttemptAsync</c>, <c>JiraWriteCoordinator.SubmitAsync</c> (whose own
    /// <c>distinguishPostAppendFailures</c> catch excludes <see cref="OperationCanceledException"/>
    /// on purpose) and <see cref="DrainMergeNoticeAsync"/> entirely, leaving
    /// <c>PendingJiraWriteId</c> set for a comment that had already gone through — certain to repost
    /// once the stale-pending ceiling eventually cleared it, since a comment carries no dedup gate.
    /// </summary>
    [Fact]
    public async Task A_merge_notice_drain_records_its_own_success_despite_a_cancellation_that_fired_after_the_readback_succeeded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-852"), cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.QueueJiraMergeNotice(task, JiraWriteNow));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                // The read-back succeeds — the comment call already posted and this confirms it —
                // before the ambient token fires. Cancelling only after building the result
                // reproduces a daemon stop landing in RecordSuccessAsync's own aggregate-and-save,
                // the window outside every catch around the Jira call itself.
                JiraResponse verified = new(200, """{"key":"PROJ-852"}""");
                cts.Cancel();
                return verified;
            }

            return new JiraResponse(201, "{}");
        });

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, requester.Requester, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.MergeNoticesDrained.Should().Be(1, "Jira's own call succeeded, so the notice must not be left for a later sweep to re-post");
        TaskAggregate? after = await LoadAsync(store, taskId, CancellationToken.None);
        after!.HasQueuedJiraMergeNotice.Should().BeFalse(
            "left set, the next sweep would repost the identical comment a second time — a comment has no dedup gate");
        after.PendingJiraWriteId.Should().BeNull("the verified success must be recorded, not left pending for a comment that already went through");
    }

    /// <summary>
    /// Pins the discriminator <see cref="JiraWriteSubmissionException"/> exists to make, rather than
    /// the coincidental case above where the racing failure clears the marker back to null. Here
    /// the racing session, in the same transaction that fails this write's own pending marker,
    /// requests a fresh write of its own too — so <c>PendingJiraWriteId</c> reads non-null by the
    /// time <c>RecordSuccessAsync</c> tries to record this attempt's own outcome, exactly the shape
    /// a version-based or id-comparison discriminator would misread as "somebody else's write, stays
    /// queued" (independent pre-PR review, cycle 5's own committed fix). But Jira's own call for
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
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-987"), cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.QueueJiraMergeNotice(task, JiraWriteNow));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingJiraRequester requester = RecordingJiraRequester.RespondingTo(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                // Jira's own comment call already succeeded by the time the mandatory read-back
                // runs; racing both a failure for this write and a fresh request in here, in the
                // same session, reproduces "our own outcome could not be recorded, and something
                // else is now outstanding too" without needing to fault-inject Postgres.
                using IDocumentSession racing = store.LightweightSession();
                TaskAggregate current = racing.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token)
                    .GetAwaiter().GetResult()!;
                racing.Events.Append(taskId, TaskDecider.RecordJiraWriteFailure(
                    current, current.PendingJiraWriteId!.Value, "Raced by another write.", isAuthFailure: false, JiraWriteNow));
                racing.SaveChangesAsync(cts.Token).GetAwaiter().GetResult();

                TaskAggregate cleared = racing.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token)
                    .GetAwaiter().GetResult()!;
                racing.Events.Append(taskId, TaskDecider.RequestJiraWrite(
                    cleared, JiraWriteOperation.Comment, issueKey: null, "{}", DomainId.New(), JiraWriteNow, DomainId.New()));
                racing.SaveChangesAsync(cts.Token).GetAwaiter().GetResult();

                return new JiraResponse(200, """{"key":"PROJ-987"}""");
            }

            return new JiraResponse(201, "{}");
        });

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, requester.Requester, DefaultOptions(), NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.MergeNoticesDrained.Should().Be(
            1, "Jira's own call for this write succeeded, so the notice must not be left for a later sweep to re-post");
        TaskAggregate? after = await LoadAsync(store, taskId, cts.Token);
        after!.HasQueuedJiraMergeNotice.Should().BeFalse(
            "attempted exactly once even though a fresh write is now outstanding on the task");
        after.PendingJiraWriteId.Should().NotBeNull("the racing session's own fresh write is genuinely outstanding now");

        // The racing session's own fresh write is deliberately left outstanding above so the
        // assertions could observe it — cleaned up here so it does not leak into a later test's own
        // sweep (every test in this class shares one Postgres fixture, and so one database).
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate afterRace = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.RecordJiraWriteFailure(
                afterRace, afterRace.PendingJiraWriteId!.Value,
                "Test cleanup: ending the racing write so it does not leak into a later test.",
                isAuthFailure: false, JiraWriteNow));
            await session.SaveChangesAsync(cts.Token);
        }
    }

    /// <summary>
    /// The physical dedup gate that protects a stuck <em>create</em> retry has no equivalent for
    /// the site-resolution guard covered above — this covers a different, previously untested
    /// branch instead: <c>JiraWriteCoordinator.RecordAlreadyLinkedAsync</c>, reached only from
    /// <c>RetryPendingAsync</c> when a create sits stuck on a rejected credential and the task
    /// acquires its external item some other way in the meantime (an operator's own
    /// <c>h9k task link-jira</c>, run because the login problem had not been noticed yet). The
    /// retry must not record that linked reference verbatim — it owes its own recorded outcome the
    /// identical read-back every other write's success gets, in case the card was since deleted or
    /// the credential is still rejected (independent pre-PR review, adversarial lens, cycle 3;
    /// verified fixed, cycle 4). Nothing exercised this path before (independent pre-PR review,
    /// cycle 4).
    /// </summary>
    [Fact]
    public async Task A_stuck_create_found_linked_by_another_route_is_confirmed_by_a_fresh_read_back()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(store, externalReference: null, cts.Token);

        RecordingJiraRequester stuck = AuthRejected();
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult submitted = await JiraWriteCoordinator.SubmitAsync(
                session, taskId, JiraWriteOperation.Create, issueKey: null,
                new JiraWritePayload("Dev Task", new Dictionary<string, string> { ["summary"] = "Close me out" }, null),
                JiraProjectKey.Parse("PROJ"), DomainId.New(), Executor(stuck), cts.Token);
            submitted.Outcome.Should().Be(JiraWriteOutcome.PendingAuthentication);
        }

        // An operator's own h9k task link-jira, run because the auth problem had not been noticed
        // yet — the create is still pending, but the task now carries the reference some other way.
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.LinkWorkItem(
                task, new ExternalReference(WorkItemProvider.Jira, "PROJ-555"), "Found it", "To Do", JiraWriteNow, JiraWriteNow, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingJiraRequester reauthenticated = RecordingJiraRequester.Succeeding(200, """{"key":"PROJ-555"}""");
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult? retried = await JiraWriteCoordinator.RetryPendingAsync(
                session, taskId, JiraProjectKey.Parse("PROJ"), Executor(reauthenticated), cts.Token);

            retried.Should().NotBeNull();
            retried!.Outcome.Should().Be(JiraWriteOutcome.Succeeded);
            retried.IssueKey.Should().Be("PROJ-555", "the recorded outcome is what Jira answered when read back, not the unverified link");
        }

        JiraRequest readBack = reauthenticated.Requests.Should().ContainSingle().Subject;
        readBack.Method.Should().Be(HttpMethod.Get);
        readBack.Url.ToString().Should().Contain("PROJ-555");
        TaskAggregate? resolved = await LoadAsync(store, taskId, cts.Token);
        resolved!.PendingJiraWriteId.Should().BeNull("the stuck create is resolved rather than left pending forever");
    }

    /// <summary>
    /// A write requested and never given an outcome — the shape a cancellation the coordinator's
    /// own recording grace could not outrun, or a harder process death, leaves behind (independent
    /// pre-PR review, cycle 1, both lenses). Appended directly through the decider rather than by
    /// letting a real attempt run and fail: the whole point of the fix under test is that nothing
    /// else in the platform ever gets the chance to record an outcome for this write, so a fake
    /// requester that fails or cancels here would only be testing the wrong path.
    /// </summary>
    [Fact]
    public async Task A_write_stuck_pending_past_the_ceiling_is_ended_on_the_clock_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-321"), cts.Token);
        Guid writeId;
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            writeId = DomainId.New();
            session.Events.Append(taskId, TaskDecider.RequestJiraWrite(
                task, JiraWriteOperation.Comment, "PROJ-321", "{}", writeId, JiraWriteNow, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingJiraRequester mustNotRun = RecordingJiraRequester.RespondingTo(
            _ => throw new InvalidOperationException("the ceiling sweep must never call Jira — it only ends a stale write"));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(
            store, node, mustNotRun.Requester, Options.Create(new DaemonOptions { PendingJiraWriteCeiling = TimeSpan.Zero }),
            NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Should().Be(new JiraWriteRetrySweepResult(Retried: 0, Succeeded: 0, MergeNoticesDrained: 0, Expired: 1));
        TaskAggregate? ended = await LoadAsync(store, taskId, cts.Token);
        ended!.PendingJiraWriteId.Should().BeNull("the ceiling clears the wedge even though nothing ever attempted the write");
        ended.PendingJiraWriteIsAuthFailure.Should().BeFalse();
    }

    private static IOptions<DaemonOptions> DefaultOptions() => Options.Create(new DaemonOptions());


    private static async Task<TaskAggregate?> LoadAsync(IDocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        return await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
    }

    private static async Task<Guid> SeedTaskAsync(
        IDocumentStore store, ExternalReference? externalReference, CancellationToken cancellationToken,
        bool archiveProject = false)
    {
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        await using IDocumentSession ownerSession = store.LightweightSession();
        ownerSession.Events.StartStream<OwnerAggregate>(ownerId, OwnerDecider.Register(
            ownerId, "Brian Hall", "brian@hallmanac.com", JiraWriteNow));
        await ownerSession.SaveChangesAsync(cancellationToken);

        Guid connectionId = await EnsureJiraConnectionAsync(store, ownerId, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(projectId, ProjectDecider.Register(
            projectId, ownerId, connectionId, "hall9k", "/repos/hall9k.git",
            new Uri("https://github.com/Hallmanac/hall9k"), null, JiraWriteNow));

        session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId, projectId, "Comment on the linked Jira card", ["A comment is posted"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference, JiraWriteNow, ownerId));

        await session.SaveChangesAsync(cancellationToken);

        if (archiveProject)
        {
            await using IDocumentSession archiveSession = store.LightweightSession();
            ProjectAggregate project = (await archiveSession.Events
                .AggregateStreamAsync<ProjectAggregate>(projectId, token: cancellationToken))!;
            archiveSession.Events.Append(projectId, ProjectDecider.Archive(project, null, JiraWriteNow, ownerId));
            await archiveSession.SaveChangesAsync(cancellationToken);
        }

        return taskId;
    }

    /// <summary>
    /// Once per database, not once per test, and one seeder for all three seams: every test in
    /// this class shares the fixture's Postgres, the same reasoning
    /// <c>CloseoutEngineTests.SeedJiraConnectionAsync</c>'s own doc comment gives, and a second
    /// registered Jira connection is a state
    /// <see cref="WorkItemConnections.FindJiraConnectionAsync"/> refuses on purpose (nothing says
    /// which account a project uses) — which would make every test after the first assert a
    /// refusal instead of exercising what it actually means to test.
    /// <para>
    /// It is one seeder rather than one per seam for the same reason (independent pre-PR review,
    /// cycle 1, conformance lens): two first-wins seeders racing for the one install-wide
    /// connection would leave the credential reference and the account it authenticates as
    /// decided by whichever seam's test xUnit happened to order first, with nothing in either
    /// seeder to signpost that the other exists. The identity is deliberately unrecorded — a
    /// connection registered before the accountId was captured, so the take's own read reads it
    /// live from <c>/rest/api/2/myself</c> and records it, which every Jira case here therefore
    /// exercises.
    /// </para>
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
            CredentialReference.EnvironmentVariable(JiraTokenVariable), JiraWriteNow, Site));
        await session.SaveChangesAsync(cancellationToken);
        return connectionId;
    }
}
