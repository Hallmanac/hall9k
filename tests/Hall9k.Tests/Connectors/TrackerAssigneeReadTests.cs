using System.Net;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The one read a <c>tracker-assignee</c> claim gate makes, against recorded tracker answers
/// rather than a live tenant (idea 64c75e43, Decisions Log #140). Three things are pinned here and
/// nowhere else, because they are what the gate's whole behaviour rests on: exactly which field is
/// asked for (nothing else about the item may be re-read, or the one-time content snapshot rule
/// stops holding — Decisions Log #60), what "assigned to me" is matched on, and whether a failure
/// was the tracker refusing the credentials or merely failing to answer — the distinction that
/// decides whether the remedy printed beside it is renewing a token or waiting out an outage.
/// </summary>
[Collection("Hall9kHome")]
public sealed class TrackerAssigneeReadTests : IDisposable
{
    private const string TokenVariable = "HALL9K_TEST_GATE_JIRA_TOKEN";
    private static readonly Uri Site = new("https://hall9k.atlassian.net");

    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(1));

    private CancellationToken Token => _cancellation.Token;

    public TrackerAssigneeReadTests() => Environment.SetEnvironmentVariable(TokenVariable, "a-token");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TokenVariable, null);
        _cancellation.Dispose();
    }

    [Fact]
    public async Task Jira_asks_for_the_assignee_field_and_nothing_else()
    {
        RecordingJiraRequester jira = RecordingJiraRequester.Succeeding(
            200, """{"key":"PROJ-123","fields":{"assignee":{"accountId":"5b10","displayName":"Brian Hall"}}}""");

        TrackerAssigneeRead read = await Jira(jira).ReadAssigneeAsync(JiraIssueKey.Parse("PROJ-123", Site), Token);

        jira.Requests.Should().ContainSingle().Which.Url.ToString().Should().Be(
            "https://hall9k.atlassian.net/rest/api/2/issue/PROJ-123?fields=assignee",
            "nothing else about the card may be re-read at a gate check (Decisions Log #60)");
        read.Failed.Should().BeFalse();
        read.Assignees.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new TrackerAssignee("5b10", "Brian Hall"));
        read.DescribeHolders().Should().Be("Brian Hall", "a human reads the display name, not the opaque id");
    }

    /// <summary>
    /// A Jira <c>accountId</c> is an opaque token, so it is compared exactly: two ids differing
    /// only in case are two accounts until Atlassian says otherwise, and folding the case would let
    /// the gate pass for somebody else.
    /// </summary>
    [Fact]
    public async Task A_jira_account_id_is_matched_exactly()
    {
        RecordingJiraRequester jira = RecordingJiraRequester.Succeeding(
            200, """{"key":"PROJ-1","fields":{"assignee":{"accountId":"AbC","displayName":"Someone"}}}""");

        TrackerAssigneeRead read = await Jira(jira).ReadAssigneeAsync(JiraIssueKey.Parse("PROJ-1", Site), Token);

        read.IdentityComparison.Should().Be(StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_jira_card_assigned_to_nobody_reads_as_nobody_rather_than_a_failure()
    {
        RecordingJiraRequester jira = RecordingJiraRequester.Succeeding(
            200, """{"key":"PROJ-123","fields":{"assignee":null}}""");

        TrackerAssigneeRead read = await Jira(jira).ReadAssigneeAsync(JiraIssueKey.Parse("PROJ-123", Site), Token);

        read.Failed.Should().BeFalse("an unassigned card is an answer, not an error");
        read.Assignees.Should().BeEmpty();
    }

    /// <summary>
    /// A 2xx that parses cleanly but carries none of what was asked for is a proxy or an SSO portal
    /// answering for the tenant. Reading that as "nobody is assigned" would hold the claim while
    /// blaming an assignment nobody made, so it is unreadable instead.
    /// </summary>
    [Fact]
    public async Task A_jira_answer_that_is_not_a_card_is_unreadable_rather_than_unassigned()
    {
        RecordingJiraRequester jira = RecordingJiraRequester.Succeeding(
            200, """{"error":"authentication required","login_url":"https://sso.example.com"}""");

        TrackerAssigneeRead read = await Jira(jira).ReadAssigneeAsync(JiraIssueKey.Parse("PROJ-123", Site), Token);

        read.Failed.Should().BeTrue();
        read.Error.Should().Contain("carries no fields");
        read.AuthenticationRefusal.Should().BeFalse(
            "the tenant answered 200 — nothing refused the credentials, so 'renew the token' is the wrong remedy");
    }

    [Theory]
    [InlineData((int)HttpStatusCode.Unauthorized)]
    [InlineData((int)HttpStatusCode.Forbidden)]
    public async Task A_jira_credential_refusal_is_told_apart_from_an_outage(int statusCode)
    {
        RecordingJiraRequester jira = RecordingJiraRequester.Succeeding(
            statusCode, """{"errorMessages":["Client must be authenticated"]}""");

        TrackerAssigneeRead read = await Jira(jira).ReadAssigneeAsync(JiraIssueKey.Parse("PROJ-123", Site), Token);

        read.Failed.Should().BeTrue("the gate fails closed on anything it could not read");
        read.AuthenticationRefusal.Should().BeTrue();
        read.Error.Should().Contain("Client must be authenticated", "the tenant's own words, verbatim");
    }

    [Fact]
    public async Task A_jira_outage_is_not_reported_as_a_credential_refusal()
    {
        RecordingJiraRequester jira = RecordingJiraRequester.Succeeding(
            (int)HttpStatusCode.InternalServerError, """{"errorMessages":["Internal server error"]}""");

        TrackerAssigneeRead read = await Jira(jira).ReadAssigneeAsync(JiraIssueKey.Parse("PROJ-123", Site), Token);

        read.Failed.Should().BeTrue();
        read.AuthenticationRefusal.Should().BeFalse();
        read.Error.Should().Contain("status.atlassian.com", "an outage is waited out, not re-authenticated");
    }

    /// <summary>
    /// A tracker that answers definitively about the item — a key that does not resolve, or a
    /// project this account cannot see — will answer the same way on every retry. Classified apart
    /// from an outage so the hold does not name a wait that never ends (self-review, round one).
    /// Both trackers answer identically for a missing item and an invisible one, deliberately, so
    /// one classification covers both and the tracker's own words name both for the reader.
    /// </summary>
    [Fact]
    public async Task A_definitive_answer_about_the_item_is_told_apart_from_both_an_outage_and_a_refusal()
    {
        RecordingJiraRequester jira = RecordingJiraRequester.Succeeding(
            (int)HttpStatusCode.NotFound, """{"errorMessages":["Issue does not exist or you do not have permission to see it."]}""");
        RecordingProcessRunner gh = RecordingProcessRunner.Failing(
            "GraphQL: Could not resolve to an Issue with the number of 42.");

        TrackerAssigneeRead card = await Jira(jira).ReadAssigneeAsync(JiraIssueKey.Parse("PROJ-123", Site), Token);
        TrackerAssigneeRead issue = await new GitHubWorkItemProvider(gh.Runner).ReadAssigneesAsync(
            new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#42"), "/repo", Token);

        card.Failure.Should().Be(TrackerReadFailure.Item);
        card.AuthenticationRefusal.Should().BeFalse("nothing refused the credentials");
        issue.Failure.Should().Be(TrackerReadFailure.Item);
        issue.AuthenticationRefusal.Should().BeFalse();
    }

    /// <summary>
    /// A rate limit is a wait, not a definitive answer, even though it arrives as a 4xx — the one
    /// status code the item/outage split has to carve out by hand.
    /// </summary>
    [Fact]
    public async Task A_jira_rate_limit_is_a_wait_rather_than_an_answer_about_the_card()
    {
        RecordingJiraRequester jira = RecordingJiraRequester.Succeeding(
            (int)HttpStatusCode.TooManyRequests, """{"errorMessages":["Rate limit exceeded"]}""");

        TrackerAssigneeRead read = await Jira(jira).ReadAssigneeAsync(JiraIssueKey.Parse("PROJ-123", Site), Token);

        read.Failure.Should().Be(TrackerReadFailure.Outage);
    }

    /// <summary>
    /// The vault could not produce the token at all — an unset variable, a keychain item that is
    /// gone. That is the credential being unavailable rather than the tenant refusing it, but the
    /// remedy is the same one, so it is reported as a credential problem rather than as an outage
    /// nobody can wait out.
    /// </summary>
    [Fact]
    public async Task An_unresolvable_credential_reads_as_a_credential_problem_and_never_reaches_the_network()
    {
        Environment.SetEnvironmentVariable(TokenVariable, null);
        JiraWorkItemProvider provider = new(
            new JiraAccount(Site, "brian@example.com", CredentialReference.EnvironmentVariable(TokenVariable)),
            FakeJiraRequester.NeverInvoked());

        TrackerAssigneeRead read = await provider.ReadAssigneeAsync(JiraIssueKey.Parse("PROJ-123", Site), Token);

        read.Failed.Should().BeTrue();
        read.AuthenticationRefusal.Should().BeTrue();
    }

    [Fact]
    public async Task GitHub_asks_for_the_assignees_field_and_nothing_else()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(
            """{"assignees":[{"login":"Hallmanac"},{"login":"teammate"}]}""");

        TrackerAssigneeRead read = await new GitHubWorkItemProvider(gh.Runner).ReadAssigneesAsync(
            new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#42"), "/repo", Token);

        gh.Calls.Should().ContainSingle().Which.Arguments.Should().Equal(
            "issue", "view", "42", "--repo", "Hallmanac/hall9k", "--json", "assignees");
        read.Failed.Should().BeFalse();
        // An issue may carry several assignees, and being among them passes.
        read.Assignees.Select(assignee => assignee.Identity).Should().Equal("Hallmanac", "teammate");
        read.Assignees.Select(assignee => assignee.Name).Should().AllBeEquivalentTo<string?>(null,
            "a login is the whole of what gh answers — there is no display name to invent");
    }

    /// <summary>
    /// GitHub logins are case-insensitive, and the platform already matches them that way for
    /// auto-pr-review's own reviewer reads; the gate does not get to disagree.
    /// </summary>
    [Fact]
    public async Task A_github_login_is_matched_case_insensitively()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("""{"assignees":[{"login":"HallManaC"}]}""");

        TrackerAssigneeRead read = await new GitHubWorkItemProvider(gh.Runner).ReadAssigneesAsync(
            new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#42"), "/repo", Token);

        read.IdentityComparison.Should().Be(StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_github_issue_assigned_to_nobody_reads_as_nobody_rather_than_a_failure()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("""{"assignees":[]}""");

        TrackerAssigneeRead read = await new GitHubWorkItemProvider(gh.Runner).ReadAssigneesAsync(
            new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#42"), "/repo", Token);

        read.Failed.Should().BeFalse();
        read.Assignees.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unauthenticated_gh_is_told_apart_from_an_outage()
    {
        RecordingProcessRunner refused = RecordingProcessRunner.Failing(
            "gh: To get started with GitHub CLI, please run: gh auth login");
        RecordingProcessRunner unreachable = RecordingProcessRunner.Failing(
            "error connecting to api.github.com");

        TrackerAssigneeRead refusedRead = await new GitHubWorkItemProvider(refused.Runner).ReadAssigneesAsync(
            new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#42"), "/repo", Token);
        TrackerAssigneeRead unreachableRead = await new GitHubWorkItemProvider(unreachable.Runner).ReadAssigneesAsync(
            new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#42"), "/repo", Token);

        refusedRead.Failed.Should().BeTrue();
        refusedRead.AuthenticationRefusal.Should().BeTrue();
        unreachableRead.Failed.Should().BeTrue();
        unreachableRead.AuthenticationRefusal.Should().BeFalse();
        unreachableRead.Error.Should().Contain("error connecting to api.github.com", "gh's own words, verbatim");
    }

    /// <summary>
    /// Exit code zero is not a promise of shape: something else on PATH named gh succeeds and
    /// prints its own prose. Reading that as an empty assignee list would hold the claim while
    /// blaming a repository decision nobody made.
    /// </summary>
    [Fact]
    public async Task A_gh_answer_carrying_no_assignees_array_is_unreadable_rather_than_unassigned()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("Command 'issue' is deprecated\n");

        TrackerAssigneeRead read = await new GitHubWorkItemProvider(gh.Runner).ReadAssigneesAsync(
            new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#42"), "/repo", Token);

        read.Failed.Should().BeTrue();
        read.AuthenticationRefusal.Should().BeFalse();
    }

    /// <summary>
    /// The login half of a GitHub check, read live on every check and never stored — the same
    /// <c>gh api user</c> call auto-pr-review already makes. <see cref="GitHubReviewAssignments.CurrentLoginAsync"/>
    /// drops the reason because a poll's only move is to try again; the gate keeps it, because a
    /// hold has to say what ends it.
    /// </summary>
    [Fact]
    public async Task The_current_login_is_read_live_and_reports_why_it_could_not_be()
    {
        RecordingProcessRunner signedIn = RecordingProcessRunner.Succeeding("Hallmanac\n");
        RecordingProcessRunner signedOut = RecordingProcessRunner.Failing(
            "error: not logged into any GitHub hosts. Run gh auth login");

        GitHubLoginRead read = await new GitHubReviewAssignments(signedIn.Runner)
            .ReadCurrentLoginAsync("/repo", Token);
        GitHubLoginRead failed = await new GitHubReviewAssignments(signedOut.Runner)
            .ReadCurrentLoginAsync("/repo", Token);

        signedIn.Calls.Should().ContainSingle().Which.Arguments.Should().Equal("api", "user", "-q", ".login");
        read.Login.Should().Be("Hallmanac");
        read.Error.Should().BeNull();

        failed.Login.Should().BeNull();
        failed.AuthenticationRefusal.Should().BeTrue();
        failed.Error.Should().Contain("not logged into any GitHub hosts");

        // The dropped-reason overload still answers exactly what it always did, so auto-pr-review's
        // own poll is untouched by the gate needing more than it does.
        (await new GitHubReviewAssignments(signedOut.Runner).CurrentLoginAsync("/repo", Token))
            .Should().BeNull();
    }

    /// <summary>
    /// A reference this provider cannot parse into an issue is unreadable rather than silently
    /// unassigned — the never-guess rule applied to the one input the gate cannot ask about.
    /// </summary>
    [Fact]
    public async Task A_reference_that_is_not_an_issue_is_unreadable_without_running_gh()
    {
        TrackerAssigneeRead read = await new GitHubWorkItemProvider(RecordingProcessRunner.NeverInvoked())
            .ReadAssigneesAsync(new ExternalReference(WorkItemProvider.GitHub, "not-a-reference"), "/repo", Token);

        read.Failed.Should().BeTrue();
        read.Error.Should().Contain("does not read as a github owner/repo#number reference");
    }

    /// <summary>
    /// A Jira <c>displayName</c> is set by whoever owns that account on a shared tenant, so it is
    /// relayed text exactly like an issue title — and it reaches a terminal three ways (the exit-70
    /// refusal, the <c>task assign</c> warning, a queued row's own line). What is stored as the
    /// claim's evidence stays byte-for-byte what Jira said; what a human is shown is made safe
    /// (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_display_name_a_terminal_would_obey_is_made_safe_before_a_human_is_shown_it()
    {
        RecordingJiraRequester jira = RecordingJiraRequester.Succeeding(
            200,
            """{"key":"PROJ-9","fields":{"assignee":{"accountId":"5b10","displayName":"Jane‮doe\nAssigned to you"}}}""");

        TrackerAssigneeRead read = await Jira(jira).ReadAssigneeAsync(JiraIssueKey.Parse("PROJ-9", Site), Token);

        TrackerAssignee holder = read.Assignees.Should().ContainSingle().Which;
        holder.Name.Should().Be(
            "Jane‮doe\nAssigned to you",
            "the recorded evidence is what the tracker actually said, never a rewritten version of it");
        holder.Described.Should().Be(
            "Janedoe Assigned to you",
            "the override that reverses the line and the newline that adds one of its own are dropped "
            + "and folded on the way to a human");
        read.DescribeHolders().Should().Be(holder.Described);
    }

    /// <summary>
    /// The commonest failure this gate meets on GitHub — gh not authenticated — prints several
    /// lines, and they land in a hold's error, an exit-70 refusal, and a one-line status row. One
    /// line is what those sinks can carry (independent pre-PR review, cycle 1, adversarial lens),
    /// and folding costs the credential-versus-outage classification nothing.
    /// </summary>
    [Fact]
    public async Task Ghs_own_multi_line_complaint_reaches_the_hold_as_one_line()
    {
        RecordingProcessRunner signedOut = RecordingProcessRunner.Failing(
            "To get started with GitHub CLI, please run: gh auth login\n"
            + "Alternatively, populate the GH_TOKEN environment variable\r\n");

        GitHubLoginRead failed = await new GitHubReviewAssignments(signedOut.Runner)
            .ReadCurrentLoginAsync("/repo", Token);

        string error = failed.Error ?? string.Empty;
        error.Should().NotContainAny("\n", "\r")
            .And.Contain("please run: gh auth login Alternatively, populate the GH_TOKEN");
        failed.AuthenticationRefusal.Should().BeTrue(
            "folding the lines keeps every word gh printed, so what it said is still matched on");
    }

    private static JiraWorkItemProvider Jira(RecordingJiraRequester requester) =>
        new(
            new JiraAccount(Site, "brian@example.com", CredentialReference.EnvironmentVariable(TokenVariable)),
            requester.Requester);
}
