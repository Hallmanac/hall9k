using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// What <see cref="JiraWriteCoordinator"/> does with the executor's duplicate-card search: a create
/// adopts a card an earlier attempt made instead of filing a second one, anchors its search window
/// on the task's first create request rather than the attempt in hand, and never adopts a card some
/// other task already carries. Jira is a stand-in that keeps the cards it has "created" and applies
/// the search's relative time bound itself, so what is asserted is which card ends up linked, not
/// what the query looked like.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class JiraCreateDedupTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly Uri Site = new("https://hall9k.atlassian.net");
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private const string Summary = "Fix the thing";
    private const string Description = "Some context.";

    private static readonly JiraWritePayload ComposedCreate = new(
        "Dev Task",
        new Dictionary<string, string> { ["summary"] = Summary, ["description"] = Description },
        Comment: null);

    /// <summary>A Jira with a board of existing cards, a create that adds one, and a search that honours <c>created &gt;= -Nm</c>.</summary>
    private sealed class FakeJira(Func<DateTimeOffset> now)
    {
        public List<(string Key, string Summary, DateTimeOffset Created)> Cards { get; } = [];

        public List<JiraRequest> Creates { get; } = [];

        public bool ReadBackFails { get; set; }

        public JiraResponse Respond(JiraRequest request)
        {
            if (request.Method == HttpMethod.Post && request.Url.AbsolutePath.EndsWith("/search/jql", StringComparison.Ordinal))
            {
                using JsonDocument body = JsonDocument.Parse(request.JsonBody ?? "{}");
                string jql = body.RootElement.GetProperty("jql").GetString() ?? string.Empty;
                int minutes = int.Parse(Regex.Match(jql, @"created >= -(\d+)m").Groups[1].Value, CultureInfo.InvariantCulture);
                JsonArray issues = new([.. Cards
                    .Where(card => now() - card.Created <= TimeSpan.FromMinutes(minutes))
                    .Select(card => (JsonNode)new JsonObject
                    {
                        ["key"] = card.Key,
                        ["fields"] = new JsonObject { ["summary"] = card.Summary },
                    })]);
                return new JiraResponse(200, new JsonObject { ["issues"] = issues }.ToJsonString());
            }

            if (request.Method == HttpMethod.Post)
            {
                Creates.Add(request);
                string key = $"PROJ-{100 + Cards.Count}";
                Cards.Add((key, Summary, now()));
                return new JiraResponse(201, $$"""{"key":"{{key}}"}""");
            }

            return ReadBackFails
                ? new JiraResponse(500, "boom")
                : new JiraResponse(200, $$"""{"key":"{{request.Url.Segments[^1].Split('?')[0]}}"}""");
        }
    }

    private static JiraWriteExecutor ExecutorFor(FakeJira jira, DateTimeOffset at) =>
        new(JiraAccount.WithTokenInHand(Site, "brian@example.com", "a-token"), new RecordingJiraRequester(jira.Respond).Requester, new FixedClock(at));

    private static async Task<Guid> SeedTaskAsync(IDocumentStore store, ExternalReference? linked, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid owner = DomainId.New();
        await using IDocumentSession seed = store.LightweightSession();
        seed.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId, DomainId.New(), "Create the card", ["A card exists"], TaskType.Feature,
            agentContext: null, constraints: null, linked, Now, owner));
        await seed.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private static async Task<JiraWriteAttemptResult> SubmitCreateAsync(
        IDocumentStore store, Guid taskId, JiraWriteExecutor executor, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        return await JiraWriteCoordinator.SubmitAsync(
            session, taskId, JiraWriteOperation.Create, issueKey: null, ComposedCreate, JiraProjectKey.Parse("PROJ"),
            DomainId.New(), executor, cancellationToken);
    }

    private static async Task<TaskAggregate> LoadAsync(IDocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        return await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new InvalidOperationException($"The seeded task {taskId} is gone.");
    }

    [Fact]
    public async Task A_create_with_no_matching_card_creates_one_with_the_description_as_composed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedTaskAsync(postgres.Store, linked: null, cts.Token);
        FakeJira jira = new(() => DateTimeOffset.UtcNow);
        jira.Cards.Add(("PROJ-1", "A different card", DateTimeOffset.UtcNow));

        JiraWriteAttemptResult result = await SubmitCreateAsync(postgres.Store, taskId, ExecutorFor(jira, DateTimeOffset.UtcNow), cts.Token);

        result.Outcome.Should().Be(JiraWriteOutcome.Succeeded);
        JiraRequest create = jira.Creates.Should().ContainSingle().Subject;
        using JsonDocument sent = JsonDocument.Parse(create.JsonBody ?? "{}");
        sent.RootElement.GetProperty("fields").GetProperty("description").GetString().Should().Be(Description);
        (await LoadAsync(postgres.Store, taskId, cts.Token)).ExternalReference
            .Should().Be(new ExternalReference(WorkItemProvider.Jira, result.IssueKey ?? string.Empty));
    }

    [Fact]
    public async Task A_create_adopts_a_matching_card_instead_of_creating()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedTaskAsync(postgres.Store, linked: null, cts.Token);
        FakeJira jira = new(() => DateTimeOffset.UtcNow);
        jira.Cards.Add(("PROJ-7", Summary, DateTimeOffset.UtcNow));

        JiraWriteAttemptResult result = await SubmitCreateAsync(postgres.Store, taskId, ExecutorFor(jira, DateTimeOffset.UtcNow), cts.Token);

        result.Outcome.Should().Be(JiraWriteOutcome.Succeeded);
        result.IssueKey.Should().Be("PROJ-7");
        jira.Creates.Should().BeEmpty("the card was already there");
        (await LoadAsync(postgres.Store, taskId, cts.Token)).ExternalReference
            .Should().Be(new ExternalReference(WorkItemProvider.Jira, "PROJ-7"));
    }

    [Fact]
    public async Task A_second_create_request_on_the_same_task_adopts_the_card_the_first_attempt_made()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedTaskAsync(postgres.Store, linked: null, cts.Token);
        DateTimeOffset clock = DateTimeOffset.UtcNow;
        FakeJira jira = new(() => clock) { ReadBackFails = true };

        // The first attempt: Jira makes the card, then the read-back that verifies it fails, so
        // hall9k records a failure and the task is left with no link.
        JiraWriteAttemptResult first = await SubmitCreateAsync(postgres.Store, taskId, ExecutorFor(jira, clock), cts.Token);
        first.Outcome.Should().Be(JiraWriteOutcome.Failed);
        jira.Creates.Should().ContainSingle();
        TaskAggregate afterFirst = await LoadAsync(postgres.Store, taskId, cts.Token);
        afterFirst.ExternalReference.Should().BeNull();
        DateTimeOffset firstRequestedAt = afterFirst.FirstJiraCreateRequestedAt
            ?? throw new InvalidOperationException("The first create request was not recorded.");

        // Half an hour later the operator resubmits. The new request carries a new request time, so
        // a window anchored on it would start well after the card; the first request's does not.
        jira.ReadBackFails = false;
        clock = firstRequestedAt + TimeSpan.FromMinutes(30);
        JiraWriteAttemptResult second = await SubmitCreateAsync(postgres.Store, taskId, ExecutorFor(jira, clock), cts.Token);

        second.Outcome.Should().Be(JiraWriteOutcome.Succeeded);
        second.IssueKey.Should().Be(jira.Cards.Single().Key);
        jira.Creates.Should().ContainSingle("the resubmission adopted the first attempt's card");
        (await LoadAsync(postgres.Store, taskId, cts.Token)).FirstJiraCreateRequestedAt.Should().Be(firstRequestedAt);
    }

    [Fact]
    public async Task A_matching_card_another_task_already_carries_is_not_adopted()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid holder = await SeedTaskAsync(postgres.Store, new ExternalReference(WorkItemProvider.Jira, "PROJ-9"), cts.Token);
        Guid taskId = await SeedTaskAsync(postgres.Store, linked: null, cts.Token);
        FakeJira jira = new(() => DateTimeOffset.UtcNow);
        jira.Cards.Add(("PROJ-9", Summary, DateTimeOffset.UtcNow));

        JiraWriteAttemptResult result = await SubmitCreateAsync(postgres.Store, taskId, ExecutorFor(jira, DateTimeOffset.UtcNow), cts.Token);

        result.Outcome.Should().Be(JiraWriteOutcome.Succeeded);
        result.IssueKey.Should().NotBe("PROJ-9");
        jira.Creates.Should().ContainSingle("the only match belongs to another task, so a new card is made");
        (await LoadAsync(postgres.Store, holder, cts.Token)).ExternalReference
            .Should().Be(new ExternalReference(WorkItemProvider.Jira, "PROJ-9"));
    }

    [Fact]
    public async Task The_earliest_matching_card_nobody_else_carries_is_the_one_adopted()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SeedTaskAsync(postgres.Store, new ExternalReference(WorkItemProvider.Jira, "PROJ-20"), cts.Token);
        Guid taskId = await SeedTaskAsync(postgres.Store, linked: null, cts.Token);
        FakeJira jira = new(() => DateTimeOffset.UtcNow);
        jira.Cards.Add(("PROJ-20", Summary, DateTimeOffset.UtcNow));
        jira.Cards.Add(("PROJ-21", Summary, DateTimeOffset.UtcNow));
        jira.Cards.Add(("PROJ-22", Summary, DateTimeOffset.UtcNow));

        JiraWriteAttemptResult result = await SubmitCreateAsync(postgres.Store, taskId, ExecutorFor(jira, DateTimeOffset.UtcNow), cts.Token);

        result.IssueKey.Should().Be("PROJ-21");
        jira.Creates.Should().BeEmpty();
    }

    [Fact]
    public async Task A_window_over_the_cap_refuses_and_creates_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedTaskAsync(postgres.Store, linked: null, cts.Token);
        FakeJira jira = new(() => DateTimeOffset.UtcNow);
        foreach (int i in Enumerable.Range(1, JiraWriteExecutor.MaxWindowCards + 1))
        {
            jira.Cards.Add(($"PROJ-{i}", $"Card {i}", DateTimeOffset.UtcNow));
        }

        JiraWriteAttemptResult result = await SubmitCreateAsync(postgres.Store, taskId, ExecutorFor(jira, DateTimeOffset.UtcNow), cts.Token);

        result.Outcome.Should().Be(JiraWriteOutcome.Failed);
        result.Message.Should().Contain("PROJ").And.Contain(Summary).And.Contain("UTC");
        jira.Creates.Should().BeEmpty();
    }
}
