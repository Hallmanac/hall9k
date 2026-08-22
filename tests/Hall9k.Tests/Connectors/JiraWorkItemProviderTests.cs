using System.Net;
using System.Text;
using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// What the Jira connector does with the answers Jira actually gives: the mapping on the way in,
/// and the refusals on the way out. It runs against recorded responses rather than a live tenant,
/// which is what the requester seam exists for — the same reason the GitHub provider's tests run
/// against recorded gh output.
/// </summary>
[Collection("Hall9kHome")]
public sealed class JiraWorkItemProviderTests : IDisposable
{
    private const string TokenVariable = "HALL9K_TEST_JIRA_TOKEN";
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 9, 30, 0, TimeSpan.Zero);

    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(1));

    private CancellationToken Token => _cancellation.Token;

    public JiraWorkItemProviderTests() => Environment.SetEnvironmentVariable(TokenVariable, "a-token");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TokenVariable, null);
        _cancellation.Dispose();
    }

    /// <summary>Answers every call with one recorded response, and keeps what it was asked.</summary>
    private sealed class RecordingRequester(int statusCode, string body)
    {
        public List<JiraRequest> Requests { get; } = [];

        public JiraRequester Requester => (request, _) =>
        {
            Requests.Add(request);
            return Task.FromResult(new JiraResponse(statusCode, body));
        };
    }

    private static JiraWorkItemProvider Provider(RecordingRequester requester) =>
        new(
            new JiraAccount(
                new Uri("https://hall9k.atlassian.net"),
                "brian@example.com",
                CredentialReference.EnvironmentVariable(TokenVariable)),
            requester.Requester,
            new FixedClock(Now));

    /// <summary>
    /// Registration proves a token before anything writes it down, and this is the account that
    /// makes that order possible: it authenticates from the token in hand and carries no
    /// credential reference at all, so there is nothing on disk for it to have replaced.
    /// <para>
    /// The order matters because the file a stored token lands in is named from the site and the
    /// account. Verifying after storing means a mistyped or expired token has already overwritten
    /// the working one by the time Jira rejects it, and Atlassian shows an API token once. Origin
    /// incident (2026-08-21): the pre-PR review of the Jira branch found h9k connection add jira
    /// storing first and verifying second.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_account_is_proven_from_a_token_in_hand_before_anything_stores_it()
    {
        RecordingRequester requester = new(200, """
        {"accountId": "5b10a2844c20165700ede21g", "displayName": "Brian Hall"}
        """);
        JiraWorkItemProvider provider = new(
            JiraAccount.WithTokenInHand(
                new Uri("https://hall9k.atlassian.net"), "brian@example.com", "a-fresh-token"),
            requester.Requester);

        string displayName = await provider.VerifyAccessAsync(Token);

        displayName.Should().Be("Brian Hall");
        requester.Requests.Should().ContainSingle().Which.Authorization.Should().Be(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("brian@example.com:a-fresh-token")),
            "the token being checked is the one the human just typed, not one read back from a file");
    }

    private static string Card(string statusName, string categoryKey, string? description = "The description.") =>
        $$"""
        {
          "key": "PROJ-123",
          "fields": {
            "summary": "Cards should carry their own summary",
            "description": {{(description is null ? "null" : "\"" + description + "\"")}},
            "status": { "name": "{{statusName}}", "statusCategory": { "key": "{{categoryKey}}" } }
          }
        }
        """;

    [Fact]
    public async Task A_card_maps_onto_the_shape_every_source_answers_in()
    {
        RecordingRequester jira = new(200, Card("To Do", "new"));

        ImportedWorkItem card = await Provider(jira).ReadAsync(
            JiraIssueKey.Parse("PROJ-123", new Uri("https://hall9k.atlassian.net")), Token);

        card.Reference.Should().Be(new ExternalReference(WorkItemProvider.Jira, "PROJ-123"));
        card.Title.Should().Be("Cards should carry their own summary");
        card.Body.Should().Be("The description.");
        card.Url.Should().Be(new Uri("https://hall9k.atlassian.net/browse/PROJ-123"));
        card.ObservedAt.Should().Be(Now);
    }

    [Fact]
    public async Task The_request_carries_the_token_as_basic_auth_and_asks_only_for_the_fields_it_maps()
    {
        RecordingRequester jira = new(200, Card("To Do", "new"));

        await Provider(jira).ReadAsync(
            JiraIssueKey.Parse("PROJ-123", new Uri("https://hall9k.atlassian.net")), Token);

        JiraRequest request = jira.Requests.Should().ContainSingle().Subject;
        request.Url.ToString().Should().Be(
            "https://hall9k.atlassian.net/rest/api/2/issue/PROJ-123?fields=summary,description,status");
        request.Authorization.Should().Be(
            "Basic " + Convert.ToBase64String("brian@example.com:a-token"u8.ToArray()),
            "Jira Cloud authenticates as email plus API token over HTTP Basic");
    }

    /// <summary>
    /// The mapping reads statusCategory rather than the status name, which is what makes it
    /// survive a board whose states are called anything at all — and the name still rides along
    /// as the observed label, so the agent context stamps what the board said rather than what
    /// the platform concluded.
    /// </summary>
    [Theory]
    [InlineData("To Do", "new", true)]
    [InlineData("In Progress", "indeterminate", true)]
    [InlineData("Ready for Ozzie", "indeterminate", true)]
    [InlineData("Done", "done", false)]
    [InlineData("Shipped", "done", false)]
    public async Task A_boards_own_vocabulary_is_mapped_at_this_boundary_and_still_reported(
        string name, string category, bool open)
    {
        RecordingRequester jira = new(200, Card(name, category));

        ImportedWorkItem card = await Provider(jira).ReadAsync(
            JiraIssueKey.Parse("PROJ-123", new Uri("https://hall9k.atlassian.net")), Token);

        card.Status.IsOpen.Should().Be(open);
        card.Status.ToString().Should().Contain(name, "the board's own word for the state is the observation");
    }

    [Fact]
    public async Task A_status_with_no_category_is_carried_verbatim_so_the_gate_refuses_it()
    {
        // Nobody could say whether this is open, and the importer's gate reads positively. The
        // raw name survives so the refusal quotes what was actually observed.
        RecordingRequester jira = new(200, """
            { "key": "PROJ-123", "fields": { "summary": "s", "status": { "name": "Bespoke" } } }
            """);

        ImportedWorkItem card = await Provider(jira).ReadAsync(
            JiraIssueKey.Parse("PROJ-123", new Uri("https://hall9k.atlassian.net")), Token);

        card.Status.IsOpen.Should().BeFalse();
        card.Status.ToString().Should().Be("Bespoke");
    }

    [Fact]
    public async Task A_card_with_no_description_carries_none_rather_than_an_empty_section()
    {
        RecordingRequester jira = new(200, Card("To Do", "new", description: null));

        ImportedWorkItem card = await Provider(jira).ReadAsync(
            JiraIssueKey.Parse("PROJ-123", new Uri("https://hall9k.atlassian.net")), Token);

        card.Body.Should().BeNull();
    }

    [Fact]
    public async Task The_key_Jira_answers_with_wins_over_the_key_that_was_asked_for()
    {
        // Jira moves a card between projects by giving it a new key and keeps the old one
        // resolving, so the canonical answer is the one that is still right tomorrow.
        RecordingRequester jira = new(200, """
            {
              "key": "MOVED-9",
              "fields": { "summary": "s", "status": { "name": "To Do", "statusCategory": { "key": "new" } } }
            }
            """);

        ImportedWorkItem card = await Provider(jira).ReadAsync(
            JiraIssueKey.Parse("PROJ-123", new Uri("https://hall9k.atlassian.net")), Token);

        card.Reference.Reference.Should().Be("MOVED-9");
    }

    [Fact]
    public async Task A_missing_card_names_the_key_the_site_and_both_reasons_it_could_be_missing()
    {
        RecordingRequester jira = new((int)HttpStatusCode.NotFound, """
            { "errorMessages": ["Issue does not exist or you do not have permission to see it."], "errors": {} }
            """);

        Func<Task> read = () => Provider(jira).ReadAsync(
            JiraIssueKey.Parse("PROJ-123", new Uri("https://hall9k.atlassian.net")), Token);

        (await read.Should().ThrowAsync<DomainNotFoundException>())
            .WithMessage("*Could not find PROJ-123 at https://hall9k.atlassian.net*")
            .WithMessage("*check the key, or confirm which project it was created in*")
            .WithMessage("*Issue does not exist or you do not have permission to see it.*");
    }

    [Fact]
    public async Task Rejected_credentials_name_the_account_the_site_and_the_likely_fix()
    {
        RecordingRequester jira = new((int)HttpStatusCode.Unauthorized, string.Empty);

        Func<Task> read = () => Provider(jira).ReadAsync(
            JiraIssueKey.Parse("PROJ-123", new Uri("https://hall9k.atlassian.net")), Token);

        (await read.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*brian@example.com*")
            .WithMessage("*hall9k.atlassian.net*")
            .WithMessage("*API token*")
            .WithMessage("*h9k connection add jira*");
    }

    /// <summary>
    /// The sign-in check is the first Jira call a new install ever makes, so its refusals are the
    /// first thing a human or an agent reads. Origin incident (2026-08-21): the pre-PR review of
    /// this branch found every one of them rendering as "sign in to brian@example.com", naming the
    /// account where the sentence promised the site.
    /// </summary>
    [Fact]
    public async Task A_refused_sign_in_check_names_the_site_and_reads_as_the_account_it_tried()
    {
        RecordingRequester jira = new((int)HttpStatusCode.Unauthorized, string.Empty);

        Func<Task> verify = () => Provider(jira).VerifyAccessAsync(Token);

        (await verify.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*https://hall9k.atlassian.net rejected the credentials*")
            .WithMessage("*sign in as brian@example.com*")
            .WithMessage("*API token*");
    }

    [Fact]
    public async Task A_rate_limit_says_outright_that_nothing_is_retried_for_you()
    {
        RecordingRequester jira = new((int)HttpStatusCode.TooManyRequests, string.Empty);

        Func<Task> comment = () => Provider(jira).CommentAsync(
            JiraIssueKey.Parse("PROJ-123", new Uri("https://hall9k.atlassian.net")),
            "merged",
            Token);

        (await comment.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*does not retry*");
    }

    [Fact]
    public async Task A_comment_posts_the_text_as_the_cards_own_body()
    {
        RecordingRequester jira = new(201, "{}");

        await Provider(jira).CommentAsync(
            JiraIssueKey.Parse("PROJ-123", new Uri("https://hall9k.atlassian.net")),
            "The pull request merged.",
            Token);

        JiraRequest request = jira.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Url.ToString().Should().Be("https://hall9k.atlassian.net/rest/api/2/issue/PROJ-123/comment");
        request.JsonBody.Should().Be("""{"body":"The pull request merged."}""");
    }

    [Fact]
    public async Task Something_that_is_not_Jira_answering_is_reported_as_that_rather_than_as_a_card()
    {
        // A proxy or an SSO portal in front of the tenant answers 200 with HTML. Mapping that as
        // a card with no summary would record a lie; saying what it looks like is the clue.
        RecordingRequester jira = new(200, "<html><body>Sign in</body></html>");

        Func<Task> read = () => Provider(jira).ReadAsync(
            JiraIssueKey.Parse("PROJ-123", new Uri("https://hall9k.atlassian.net")), Token);

        (await read.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*not a Jira card*");
    }

    [Fact]
    public async Task A_two_hundred_carrying_json_that_is_not_a_card_is_refused_rather_than_thrown_through()
    {
        // Valid JSON is not the same thing as a card: an API gateway answering an array or a bare
        // string still has to leave as the sentence that names the cause, not as the
        // InvalidOperationException that asking an array for a property raises.
        RecordingRequester jira = new(200, "[]");

        Func<Task> read = () => Provider(jira).ReadAsync(
            JiraIssueKey.Parse("PROJ-123", new Uri("https://hall9k.atlassian.net")), Token);

        (await read.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*not a Jira card*Array*");
    }

    /// <summary>
    /// The sign-in gate exists to prove credentials before anything is written down, so a 2xx
    /// that proves nothing has to be refused as loudly as a 401. Origin incident (2026-08-22):
    /// the pre-PR review of this branch found the check treating any parseable body as a
    /// successful sign-in, so an identity-aware proxy answering 200 with its own JSON registered
    /// a connection, stored the token, and printed a confirmation for an account that had never
    /// authenticated — with the real failure deferred to the middle of a dispatched session.
    /// </summary>
    [Theory]
    [InlineData("""{"error": "authentication required", "login_url": "https://sso.example.com"}""")]
    [InlineData("[]")]
    [InlineData("""{"displayName": "Brian Hall"}""")]
    public async Task A_two_hundred_that_is_not_a_jira_account_is_refused_rather_than_read_as_a_sign_in(string body)
    {
        RecordingRequester jira = new(200, body);

        Func<Task> verify = () => Provider(jira).VerifyAccessAsync(Token);

        (await verify.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*not a Jira account*")
            .WithMessage("*https://your-org.atlassian.net*",
                "the site URL is the usual cause, so the refusal says what a right one looks like");
    }

    [Fact]
    public void A_jira_reference_is_placed_from_the_registered_site()
    {
        RecordingRequester jira = new(200, "{}");

        Provider(jira).WebUrl(new ExternalReference(WorkItemProvider.Jira, "PROJ-123"))
            .Should().Be(new Uri("https://hall9k.atlassian.net/browse/PROJ-123"));
        Provider(jira).WebUrl(new ExternalReference(WorkItemProvider.GitHub, "o/r#1"))
            .Should().BeNull("this provider speaks for Jira and says nothing about anyone else's references");
    }

    [Fact]
    public async Task Verifying_access_reports_who_the_credentials_actually_are()
    {
        RecordingRequester jira = new(200, """{ "displayName": "Brian Hall", "accountId": "abc" }""");

        string who = await Provider(jira).VerifyAccessAsync(Token);

        who.Should().Be("Brian Hall", "the useful confirmation is the one that could have come out different");
        jira.Requests.Should().ContainSingle()
            .Which.Url.ToString().Should().Be("https://hall9k.atlassian.net/rest/api/2/myself");
    }
}
