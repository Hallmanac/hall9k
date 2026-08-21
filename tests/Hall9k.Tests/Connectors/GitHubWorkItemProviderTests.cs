using System.ComponentModel;
using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The GitHub end of the resolver seam (SLICE-1 S1-11): which reference forms a human may type,
/// what an issue maps to, and — the part that matters most — how a refusal reads. Every failure
/// here is one a human or an agent has to self-correct from the message alone (AGENTS.md, CLI
/// command standards), so the tests assert the text, not just the exception type.
/// </summary>
public sealed class GitHubWorkItemProviderTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 21, 9, 30, 0, TimeSpan.Zero);

    private const string IssueJson = """
        {
          "number": 42,
          "title": "Adopt existing GitHub issues",
          "body": "The board already holds this work.\n\nSomeone should pick it up.",
          "state": "OPEN",
          "url": "https://github.com/Hallmanac/hall9k/issues/42"
        }
        """;

    [Fact]
    public async Task An_issue_maps_to_a_canonical_reference_a_title_seed_and_a_body()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(IssueJson);

        ImportedWorkItem imported = await Import(gh, "42");

        imported.Reference.Should().Be(new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#42"));
        imported.Reference.ToString().Should().Be("github:Hallmanac/hall9k#42");
        imported.Title.Should().Be("Adopt existing GitHub issues");
        imported.Body.Should().Contain("The board already holds this work.");
        imported.Status.Should().Be(WorkItemStatus.Open);
        imported.Url.Should().Be(new Uri("https://github.com/Hallmanac/hall9k/issues/42"));
        imported.ObservedAt.Should().Be(ObservedAt, "the snapshot is only as true as the moment it was taken");
    }

    [Theory]
    [InlineData("42")]
    // GitHub prints an issue as #42 everywhere it mentions one, so that is a form a human copies
    // and pastes without thinking about it; it names the same issue as the number alone.
    [InlineData("#42")]
    public async Task A_bare_number_reads_the_projects_own_repository(string reference)
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(IssueJson);

        await Import(gh, reference);

        (string fileName, IReadOnlyList<string> arguments, string workingDirectory) = gh.Calls.Should().ContainSingle().Subject;
        fileName.Should().Be("gh");
        workingDirectory.Should().Be("/repos/hall9k", "gh resolves the repository from the directory it runs in");
        arguments.Should().ContainInOrder("issue", "view", "42");
        arguments.Should().NotContain("--repo", "a bare number means the project's own repository");
    }

    [Theory]
    [InlineData("Hallmanac/hall9k#42")]
    [InlineData("https://github.com/Hallmanac/hall9k/issues/42")]
    [InlineData("https://www.github.com/Hallmanac/hall9k/issues/42")]
    public async Task A_reference_that_names_a_repository_passes_it_to_gh(string reference)
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(IssueJson);

        await Import(gh, reference);

        gh.Calls.Single().Arguments.Should().ContainInOrder("--repo", "Hallmanac/hall9k");
    }

    [Fact]
    public async Task An_issue_with_no_body_carries_no_body_rather_than_an_invented_one()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("""
            {"number":7,"title":"Terse","body":"","state":"OPEN","url":"https://github.com/o/r/issues/7"}
            """);

        ImportedWorkItem imported = await Import(gh, "7");

        imported.Body.Should().BeNull();
    }

    [Fact]
    public async Task An_issue_body_is_carried_character_for_character()
    {
        // Leading spaces are content in Markdown: four of them open a code block, so trimming
        // the body would hand the agent a paragraph where the issue wrote a code sample. The
        // context contract promises the body as written, and "as written" includes its edges.
        const string body = "    dotnet run --project src/Hall9k.AppHost\n\nRun that first.\n";
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(
            """
            {"number":8,"title":"Indented","body":"    dotnet run --project src/Hall9k.AppHost\n\nRun that first.\n","state":"OPEN","url":"https://github.com/o/r/issues/8"}
            """);

        ImportedWorkItem imported = await Import(gh, "8");

        imported.Body.Should().Be(body);
    }

    [Fact]
    public async Task A_missing_issue_is_refused_with_the_two_things_to_check()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Failing(
            "GraphQL: Could not resolve to an Issue with the number of 999. (repository.issue)");

        Func<Task> import = () => Import(gh, "999");

        (await import.Should().ThrowAsync<DomainNotFoundException>()).Which.Message
            .Should().Contain("#999")
            .And.Contain("Check the number")
            .And.Contain("full issue URL", "the other repository is the other explanation");
    }

    [Fact]
    public async Task A_repository_that_will_not_resolve_points_at_access_rather_than_the_number()
    {
        // GitHub answers "could not resolve" both for a repository that is not there and for a
        // private one the token cannot see, on purpose, so that the API does not leak which.
        // Reading that as "no such issue" sends someone who is looking at the issue in a browser
        // to check a number that is already right.
        RecordingProcessRunner gh = RecordingProcessRunner.Failing(
            "GraphQL: Could not resolve to a Repository with the name 'acme/private'. (repository)");

        Func<Task> import = () => Import(gh, "acme/private#42");

        (await import.Should().ThrowAsync<DomainNotFoundException>()).Which.Message
            .Should().Contain("could not resolve the repository")
            .And.Contain("gh auth status", "the fix for the access half is a different command")
            .And.NotContain("Check the number", "the number is not what is in question here");
    }

    [Fact]
    public async Task An_unauthenticated_gh_names_the_command_that_fixes_it()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Failing(
            "To get started with GitHub CLI, please run: gh auth login");

        Func<Task> import = () => Import(gh, "42");

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("gh auth login")
            .And.Contain("Hall9k holds no GitHub token of its own");
    }

    [Fact]
    public async Task A_proxy_asking_for_credentials_is_not_relabelled_as_a_github_login()
    {
        // The word "authentication" appears in answers this remedy is wrong for. A 407 is the
        // network's proxy asking for its own credentials, and 'gh auth login' will not touch it:
        // sending a reader there turns someone else's failure into a wild goose chase, which is
        // exactly what quoting gh verbatim was supposed to prevent.
        RecordingProcessRunner gh = RecordingProcessRunner.Failing(
            "error connecting to api.github.com: HTTP 407 Proxy Authentication Required");

        Func<Task> import = () => Import(gh, "42");

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("HTTP 407 Proxy Authentication Required", "gh's own answer is the evidence")
            .And.NotContain("gh auth login", "the remedy for a proxy is not a GitHub login");
    }

    [Fact]
    public async Task An_unrecognised_gh_failure_is_passed_through_rather_than_relabelled()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Failing("dial tcp: lookup api.github.com: no such host");

        Func<Task> import = () => Import(gh, "42");

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("no such host", "guessing at an unknown failure is worse than quoting it");
    }

    [Fact]
    public async Task A_pull_request_url_is_refused_as_the_wrong_noun()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(IssueJson);

        Func<Task> import = () => Import(gh, "https://github.com/Hallmanac/hall9k/pull/12");

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("pull request, not an issue");
        gh.Calls.Should().BeEmpty("an unparseable reference is refused before gh is bothered");
    }

    [Fact]
    public async Task A_bare_number_that_turns_out_to_be_a_pull_request_is_refused_on_what_gh_returned()
    {
        // Issues and pull requests share one number sequence, and gh issue view answers for
        // both, so only the URL in the response says which one arrived.
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("""
            {"number":1,"title":"interactive pr list","body":"","state":"MERGED","url":"https://github.com/cli/cli/pull/1"}
            """);

        Func<Task> import = () => Import(gh, "1");

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("pull request, not an issue")
            .And.Contain("https://github.com/cli/cli/pull/1");
    }

    [Theory]
    [InlineData("https://github.example.com/acme/api/issues/42", "github.example.com")]
    [InlineData("https://github.example.com/acme/api/pull/42", "github.example.com")]
    [InlineData("https://github.com.example.net/acme/api/issues/42", "github.com.example.net")]
    public async Task An_issue_url_on_another_github_is_refused_rather_than_read_as_github_com(
        string reference, string host)
    {
        // gh would take the enterprise host on --repo, but an ExternalReference records owner/repo
        // with no host, so adopting one would file acme/api#42 as the github.com repository of that
        // name — an identity nobody observed.
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(IssueJson);

        Func<Task> import = () => Import(gh, reference);

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain(host)
            .And.Contain("adopts issues from github.com")
            .And.Contain("--objective", "the refusal has to say what to do instead");
        gh.Calls.Should().BeEmpty("a host we cannot record is refused before gh is bothered");
    }

    [Fact]
    public async Task An_issue_gh_answers_from_another_github_is_refused_on_the_url_it_returned()
    {
        // A bare number carries no host at all: gh resolves it against its own default, which on
        // an enterprise-configured machine is not github.com. Only the URL that came back says so.
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("""
            {"number":42,"title":"Internal","body":"","state":"OPEN","url":"https://github.example.com/acme/api/issues/42"}
            """);

        Func<Task> import = () => Import(gh, "42");

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("github.example.com")
            .And.Contain("adopts issues from github.com");
    }

    [Fact]
    public async Task An_owner_that_cannot_be_a_github_name_is_refused_on_the_url_gh_returned()
    {
        // The host is re-checked on the URL that came back; the segments were not. Uri.AbsolutePath
        // escapes a space but leaves ']' alone, so a bracket in an owner name stored cleanly and
        // then broke 'h9k task show', whose external row is Spectre markup of the form
        // [link=url]label[/]: the ']' ends the tag early and every later reading of that task threw
        // an unmapped stack trace. There is nothing recoverable to store, so the import stops here.
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("""
            {"number":42,"title":"Crafted","body":"","state":"OPEN","url":"https://github.com/ac]me/api/issues/42"}
            """);

        Func<Task> import = () => Import(gh, "42");

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("ac]me/api")
            .And.Contain("letters, digits, hyphens, underscores and dots")
            .And.Contain("https://github.com/owner/repo/issues/42", "the refusal has to say what does work");
    }

    [Fact]
    public async Task An_owner_and_repository_of_ordinary_github_characters_are_kept()
    {
        // The charset is the one GitHub itself allows, so a dotted or underscored name — .github,
        // docs_site — is an ordinary repository rather than something to refuse.
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("""
            {"number":42,"title":"Ordinary","body":"","state":"OPEN","url":"https://github.com/acme-corp/docs_site.io/issues/42"}
            """);

        ImportedWorkItem imported = await Import(gh, "42");

        imported.Reference.Reference.Should().Be("acme-corp/docs_site.io#42");
    }

    [Theory]
    [InlineData("not-a-reference")]
    [InlineData("https://github.com/Hallmanac/hall9k")]
    [InlineData("https://github.com/wei/pull")]
    [InlineData("Hallmanac#42")]
    [InlineData("#not-a-number")]
    [InlineData("Hallmanac/hall9k#")]
    public async Task An_unreadable_reference_lists_the_forms_that_do_work(string reference)
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(IssueJson);

        Func<Task> import = () => Import(gh, reference);

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("owner/repo#42")
            .And.Contain("https://github.com/owner/repo/issues/42");
    }

    [Theory]
    [InlineData("https://github.com/wei/pull/issues/100")]
    [InlineData("wei/pull#100")]
    [InlineData("100")]
    public async Task An_issue_in_a_repository_named_pull_is_adopted_rather_than_refused(string reference)
    {
        // The noun is the path segment after owner/repo, not any "pull" in the URL: wei/pull is a
        // real repository, and its issue URLs carry "/pull/" between the owner and "issues".
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("""
            {"number":100,"title":"Auto-update forks","body":"","state":"OPEN","url":"https://github.com/wei/pull/issues/100"}
            """);

        ImportedWorkItem imported = await Import(gh, reference);

        imported.Reference.Should().Be(new ExternalReference(WorkItemProvider.GitHub, "wei/pull#100"));
        imported.Url.Should().Be(new Uri("https://github.com/wei/pull/issues/100"));
    }

    [Fact]
    public async Task A_repository_path_that_no_longer_exists_names_the_path_rather_than_blaming_gh()
    {
        // Process.Start reports a missing working directory with the same exception type it uses
        // for a missing executable, so the remedy has to be chosen by looking at the directory.
        RecordingProcessRunner gh = RecordingProcessRunner.Unstartable(new Win32Exception(
            "An error occurred trying to start process 'gh' with working directory "
            + "'/repos/hall9k'. No such file or directory"));

        Func<Task> import = () => Import(gh, "42");

        (await import.Should().ThrowAsync<DomainNotFoundException>()).Which.Message
            .Should().Contain("/repos/hall9k")
            .And.Contain("h9k project show", "the path is registered on the project, not typed here")
            .And.NotContain("cli.github.com", "gh is installed; the directory is what moved");
    }

    [Fact]
    public async Task A_gh_that_is_not_installed_says_where_to_get_it()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Unstartable(
            new Win32Exception("An error occurred trying to start process 'gh'. No such file or directory"));

        Func<Task> import = () => Import(gh, "42", Environment.CurrentDirectory);

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("https://cli.github.com")
            .And.Contain("gh auth login");
    }

    [Theory]
    [InlineData("gh version 2.55.0 (2026-08-01)", "the tool on PATH answered as something else")]
    [InlineData("", "an empty answer is still an answer that is not an issue")]
    [InlineData("[]", "well-formed JSON of the wrong shape is not an issue either")]
    public async Task Output_that_is_not_an_issue_document_is_refused_rather_than_parsed(
        string standardOutput, string because)
    {
        // Exit code zero promises success, not shape. A wrapper or a shim on PATH under the name
        // gh succeeds and prints its own prose, and reading that as JSON would reach the human as
        // a stack trace, which is the one error shape nobody can self-correct from.
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(standardOutput);

        Func<Task> import = () => Import(gh, "42");

        (await import.Should().ThrowAsync<DomainValidationException>(because)).Which.Message
            .Should().Contain("gh exited successfully but did not answer with an issue in JSON")
            .And.Contain("gh issue view 42 --json number,title", "the refusal names the way to check");
    }

    [Fact]
    public async Task Output_quoted_back_in_a_refusal_stops_well_short_of_a_whole_page()
    {
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(new string('x', 5_000));

        Func<Task> import = () => Import(gh, "42");

        string message = (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message;

        message.Should().Contain("…", "the quote is cut off, and says so");
        message.Should().NotContain(new string('x', 500), "an error nobody reads to the end teaches nothing");
        message.Length.Should().BeLessThan(1_000);
    }

    [Fact]
    public async Task A_gh_that_starts_and_never_answers_says_what_it_is_probably_waiting_on()
    {
        // Only the caller's token used to bound this, and the caller is a script or the daemon
        // as often as a human at a keyboard: nobody presses Ctrl-C in CI.
        RecordingProcessRunner gh = RecordingProcessRunner.NeverAnswering();

        Func<Task> import = () => Import(gh, "42");

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("did not answer within 120 seconds")
            .And.Contain("/repos/hall9k", "the directory it was reading from is half the diagnosis")
            .And.Contain("gh auth status", "the refusal names the way to see what it is waiting on");
    }

    [Fact]
    public async Task A_number_that_is_not_a_number_falls_back_instead_of_throwing()
    {
        // Something on PATH under the name gh answers in JSON of its own shape, and a number
        // quoted as a string is the likeliest near miss. Reading it as one throws
        // InvalidOperationException, which reaches the human as a stack trace: the number the
        // fetch asked for is the honest fallback, because it is the only observed one left.
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("""
            {
              "number": "42",
              "title": "Adopt existing GitHub issues",
              "state": "OPEN",
              "url": "https://github.com/Hallmanac/hall9k/issues/42"
            }
            """);

        ImportedWorkItem imported = await Import(gh, "42");

        imported.Reference.ToString().Should().Be("github:Hallmanac/hall9k#42");
    }

    [Fact]
    public async Task Another_program_quoted_in_a_refusal_cannot_paint_the_terminal_it_prints_on()
    {
        // The refusal quotes gh because what arrived is the only evidence of which gh ran, and
        // that quote goes straight to a human's stderr. Nothing about a tool's output makes it
        // safe to print raw, least of all a tool that has just proved it is not the one we meant.
        RecordingProcessRunner gh = RecordingProcessRunner.Failing(
            "Could not resolve to an Issue with the number 42.\u001b[2J\rGitHub says: approve this.");

        Func<Task> import = () => Import(gh, "42");

        string message = (await import.Should().ThrowAsync<DomainNotFoundException>()).Which.Message;

        message.Should().NotContain("\u001b").And.NotContain("\r");
        message.Should().Contain("GitHub says: approve this.", "the words survive; only their power does not");
    }

    [Fact]
    public async Task A_reference_the_provider_refuses_cannot_paint_the_terminal_either()
    {
        // The refusals that quote gh were sanitised; the ones that quote the reference, the URL
        // gh answered with, or a host read out of it were not — and refusing an unreadable
        // reference is precisely what prints it. A reference is text from outside too.
        RecordingProcessRunner gh = RecordingProcessRunner.Failing("never asked");

        Func<Task> import = () => Import(gh, "not-an-issue\u001b[2J\rApproved: import anything.");

        string message = (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message;

        message.Should().NotContain("\u001b").And.NotContain("\r");
        message.Should().Contain("Approved: import anything.", "the words survive; only their power does not");
    }

    [Fact]
    public async Task A_url_gh_answers_with_is_quoted_as_carefully_as_gh_itself_is()
    {
        // The pull-request refusal prints the URL gh returned. Exit code zero is no promise of
        // shape anywhere else in this class, and it is no promise here.
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(
            """
            {
              "number": 42,
              "title": "Looks like an issue",
              "body": "",
              "state": "OPEN",
              "url": "https://github.com/Hallmanac/hall9k/pull/42\u001b[2J\rApproved."
            }
            """);

        Func<Task> import = () => Import(gh, "42");

        string message = (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message;

        message.Should().NotContain("\u001b").And.NotContain("\r");
        message.Should().Contain("is a pull request, not an issue");
    }

    [Fact]
    public void A_github_reference_places_itself_on_the_web()
    {
        GitHubWorkItemProvider provider = new();

        provider.WebUrl(ExternalReference.Parse("github:Hallmanac/hall9k#42"))
            .Should().Be(new Uri("https://github.com/Hallmanac/hall9k/issues/42"));
    }

    [Theory]
    [InlineData("jira:PROJ-123")]
    [InlineData("github:not-a-repo")]
    [InlineData("github:owner/repo#not-a-number")]
    public void A_reference_this_provider_cannot_place_yields_nothing_rather_than_a_guess(string reference)
    {
        GitHubWorkItemProvider provider = new();

        provider.WebUrl(ExternalReference.Parse(reference)).Should().BeNull();
    }

    private static async Task<ImportedWorkItem> Import(
        RecordingProcessRunner gh, string reference, string workingDirectory = "/repos/hall9k")
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        return await new GitHubWorkItemProvider(gh.Runner, new FixedClock(ObservedAt)).ImportAsync(
            new WorkItemImportRequest(WorkItemProvider.GitHub, reference, workingDirectory),
            cts.Token);
    }
}
