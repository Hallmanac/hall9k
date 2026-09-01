using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// How an adopted work item reads on the one surface a human actually opens. The link matters
/// because the point of adoption is the round trip (PLAN.md §3.1a), and the caption matters
/// because the row would otherwise read as live status for something the platform looked at
/// once and never again.
/// </summary>
public sealed class TaskExternalReferenceTests
{
    /// <summary>The importer an install with no Jira connection registered has. No test in this
    /// file reaches a member of <see cref="GitHubWorkItemProvider"/> that shells out — the ones
    /// that use this importer only call <c>ExternalMarkup</c>, which reads <c>WebUrl</c> and never
    /// touches the injected runner — a runner that throws if it is ever actually invoked both
    /// documents that and enforces it (see RecordingProcessRunner.NeverInvoked's own doc comment).</summary>
    private static readonly WorkItemImporter GitHubOnly = new(new GitHubWorkItemProvider(RecordingProcessRunner.NeverInvoked()));

    /// <summary>
    /// The importer an install with a Jira connection has. Constructing it needs a site, which is
    /// the whole asymmetry between the two sources: gh carries the machine's own login, Jira needs
    /// what a connection recorded (PLAN.md §10).
    /// </summary>
    private static readonly WorkItemImporter WithJira = new(
        new GitHubWorkItemProvider(RecordingProcessRunner.NeverInvoked()),
        new JiraWorkItemProvider(
            new JiraAccount(
                new Uri("https://hall9k.atlassian.net"),
                "brian@example.com",
                CredentialReference.EnvironmentVariable("JIRA_TOKEN")),
            FakeJiraRequester.NeverInvoked()));

    [Fact]
    public void An_adopted_github_issue_renders_as_a_link_to_the_issue()
    {
        string markup = TaskShowCommand.ExternalMarkup(GitHubOnly, "github:Hallmanac/hall9k#42");

        markup.Should().Contain("[link=https://github.com/Hallmanac/hall9k/issues/42]")
            .And.Contain("github:Hallmanac/hall9k#42");
    }

    [Fact]
    public void The_row_says_the_state_behind_it_is_not_being_watched()
    {
        string markup = TaskShowCommand.ExternalMarkup(GitHubOnly, "github:Hallmanac/hall9k#42");

        markup.Should().Contain("read once; never re-checked");
    }

    [Fact]
    public void A_reference_no_registered_source_can_place_prints_itself_without_a_link()
    {
        // Placing a Jira key needs the site the connection recorded, so an install with no Jira
        // connection genuinely cannot say where PROJ-123 lives — and says nothing rather than
        // guessing at somebody's tenant.
        string markup = TaskShowCommand.ExternalMarkup(GitHubOnly, "jira:PROJ-123");

        markup.Should().Be("jira:PROJ-123", "a link built on a guess is worse than no link");
    }

    [Fact]
    public void A_jira_card_renders_as_a_link_once_a_connection_names_the_site()
    {
        string markup = TaskShowCommand.ExternalMarkup(WithJira, "jira:PROJ-123");

        markup.Should().Contain("[link=https://hall9k.atlassian.net/browse/PROJ-123]")
            .And.Contain("jira:PROJ-123");
    }

    [Fact]
    public void A_jira_reference_that_is_not_a_card_key_is_not_dressed_up_as_one()
    {
        // The canonical form is provider:key, and anything else under that provider is a
        // reference nobody can place — a link to /browse/<whatever this is> would be a 404
        // wearing the shape of an answer.
        string markup = TaskShowCommand.ExternalMarkup(WithJira, "jira:not a key");

        markup.Should().Be("jira:not a key");
    }

    [Fact]
    public void An_adopted_body_cannot_repaint_the_terminal_it_is_shown_in()
    {
        // Since adoption, agent context can be an issue body written by anyone who can file an
        // issue. Printed raw, an escape sequence in it is executed by the terminal rather than
        // read by the human, so an issue could be authored to make 'h9k task show' show
        // something other than the task.
        string body = "Imported from github:o/r#1.\n\u001b[2JHidden\u0007 text\u001b[31m";

        string rendered = ExternalText.ForTerminal(body);

        rendered.Should().NotContain("\u001b").And.NotContain("\u0007");
        rendered.Should().Contain("Imported from github:o/r#1.")
            .And.Contain("[2JHidden")
            .And.Contain(" text[31m", "the characters that were never control characters still read as themselves");
    }

    [Fact]
    public void A_carriage_return_that_is_not_a_line_break_cannot_paint_over_the_line_above()
    {
        // A lone CR is not layout. It returns the cursor to column zero, so a body carrying one
        // can overwrite what 'h9k task show' already printed and hide what it replaced. Half of
        // a Windows line break is a different thing and survives.
        string body = "Objective: adopt issue 42\rEverything is fine\r\nNext line";

        ExternalText.ForTerminal(body)
            .Should().Be("Objective: adopt issue 42Everything is fine\r\nNext line");
    }

    [Fact]
    public void The_layout_of_a_markdown_body_survives_being_made_safe_to_print()
    {
        // Tabs and newlines are the only control characters a Markdown body means: dropping them
        // would collapse an indented code block into one line, which is the same damage as
        // trimming it at import.
        string body = "Steps:\r\n\n\tcargo build\n    indented code\n";

        ExternalText.ForTerminal(body).Should().Be(body);
    }

    /// <summary>
    /// The status h9k task link-jira prints back, and the one outside string on that surface that
    /// no gate has been through. A Jira status assigned to no category matches no rule the
    /// provider has, so it survives as the tenant's own text (WorkItemStatus.Parse, deliberately)
    /// — and this command reads the card directly rather than through the adoption gate that
    /// quotes a refused status safely. Origin incident (2026-08-22): the pre-PR review of this
    /// branch found the line escaped for Spectre's markup and never sanitised, which neutralises
    /// brackets and leaves control characters alone.
    /// </summary>
    [Fact]
    public void A_status_a_tenant_named_cannot_repaint_the_terminal_the_link_is_confirmed_in()
    {
        ImportedWorkItem card = new(
            new ExternalReference(WorkItemProvider.Jira, "PROJ-123"),
            "Publish me",
            null,
            WorkItemStatus.Parse("Awaiting\u001b[2Jtriage\nLinked task 8a3f: nothing to see"),
            null,
            new DateTimeOffset(2026, 8, 22, 9, 30, 0, TimeSpan.Zero));

        string markup = TaskLinkJiraCommand.ObservationMarkup(card);

        markup.Should().NotContain("\u001b").And.NotContain("\n");
        markup.Should().Contain("[2Jtriage", "the characters that were never control characters still read");
        markup.Should().Contain("2026-08-22 09:30:00Z", "a status with no stamp reads as the card's state now");
    }
}
