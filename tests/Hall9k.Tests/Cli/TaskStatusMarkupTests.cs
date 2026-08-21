using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The composed markup every browse and attention surface renders. A row carries values the
/// platform observed rather than values this repo authored, so the markup has to survive
/// whatever those values contain.
/// </summary>
public sealed class TaskStatusMarkupTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("https://github.com/x/y/pull/7", "7")]
    [InlineData("https://[::1]:8443/x/y/pull/7", "7")]
    [InlineData("https://example.com/x/y/pull/[7]", "[7]")]
    public void A_pull_request_url_renders_as_a_link_whatever_markup_characters_it_carries(
        string pullRequest, string number)
    {
        // Spectre reads '[' as the start of a tag, so an unescaped bracket anywhere in the URL
        // throws "malformed markup tag" and takes the whole table down. Escaping is what keeps
        // an observed value from being read as authored markup.
        string markup = Row(pullRequest).PullRequestMarkup;

        string rendered = Render(markup);

        rendered.Should().Contain(pullRequest, "the hyperlink still points where the URL was observed to point");
        rendered.Should().Contain($"#{number}", "the number reads off the URL, brackets and all");
    }

    [Fact]
    public void A_task_that_never_opened_a_pull_request_renders_nothing_at_all()
    {
        Row(pullRequest: null).PullRequestMarkup.Should().BeEmpty();
        Row(pullRequest: "  ").PullRequestMarkup.Should().BeEmpty("whitespace is not a pull request");
    }

    [Fact]
    public void An_objective_carrying_escape_sequences_cannot_repaint_the_table_it_is_listed_in()
    {
        // Since adoption (PLAN.md §3.1a) an objective can be seeded from an issue title, so this
        // column can be quoting anyone who can file an issue. EscapeMarkup() neutralises Spectre's
        // syntax, not the terminal's: an escape sequence left in the cell clears the screen and
        // repaints it, and a lone CR writes over the row above.
        string objective = "Fix login\u001b[2J\u001b[H\rTask 8a3f: verified, safe to merge";

        string rendered = RenderPlain(Row(pullRequest: null, objective).ObjectiveMarkup(72));

        rendered.Should().NotContain("\u001b").And.NotContain("\r");
        rendered.Should().Contain("Fix login[2J[HTask 8a3f: verified, safe to merge",
            "the characters that were never control characters still read as themselves");
    }

    [Fact]
    public void A_multi_line_objective_stays_on_the_one_line_its_row_is_given()
    {
        string rendered = RenderPlain(
            Row(pullRequest: null, "Adopt issues\nEverything below is approved").ObjectiveMarkup(72));

        rendered.Should().NotContain("\n").And.Contain("Adopt issues Everything below is approved");
    }

    /// <summary>
    /// A console that adds no escape sequences of its own, so the only ones a rendered cell could
    /// carry are the ones the value smuggled in.
    /// </summary>
    private static string RenderPlain(string markup)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 200;

        console.Markup(markup);

        return writer.ToString();
    }

    private static string Render(string markup)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Capabilities.Links = true;
        console.Profile.Width = 200;

        console.Markup(markup);

        return writer.ToString();
    }

    private static TaskStatusRow Row(string? pullRequest, string objective = "x") =>
        TaskStatusComposer.Compose(
            new TaskListItem
            {
                Id = DomainId.New(),
                ProjectId = DomainId.New(),
                Objective = objective,
                State = TaskState.Done,
                PullRequestUrl = pullRequest,
                AddedAt = Now,
            },
            new Dictionary<Guid, RunListItem>(),
            new Dictionary<Guid, RunActivity>(),
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            Now);
}
