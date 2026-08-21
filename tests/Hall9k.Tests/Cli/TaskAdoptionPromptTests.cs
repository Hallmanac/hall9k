using FluentAssertions;
using Hall9k.Cli.Commands;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The prompt that offers an issue title as the objective. The title is a value GitHub reported,
/// not one this repo authored, so it has to survive whatever it contains: Spectre reads '[' as the
/// start of a style tag, and issue titles are prefixed with "[BUG]" and friends constantly.
/// </summary>
public sealed class TaskAdoptionPromptTests
{
    [Theory]
    [InlineData("[BUG] Importer drops the body")]
    [InlineData("Support claude-opus-5[1m]")]
    [InlineData("Adopt existing GitHub issues")]
    public void The_objective_prompt_offers_the_title_whatever_markup_characters_it_carries(string title)
    {
        string rendered = Render(TaskAddCommand.ObjectivePrompt(title));

        rendered.Should().Contain(title, "the seed a human is asked to accept is the title as GitHub wrote it");
    }

    [Fact]
    public void A_title_carrying_escape_sequences_cannot_repaint_the_prompt_it_is_offered_in()
    {
        // EscapeMarkup() neutralises Spectre's syntax, not the terminal's. The title is a value
        // GitHub reported, so it can carry an escape sequence that clears the screen or a lone
        // CR that returns the cursor to column zero and paints over the question itself.
        string title = "Adopt issues\u001b[2J\u001b[31m\rSay yes";

        string rendered = Render(TaskAddCommand.ObjectivePrompt(title));

        rendered.Should().NotContain("\u001b").And.NotContain("\r");
        rendered.Should().Contain("Adopt issues").And.Contain("Say yes",
            "the characters that were never control characters still read as themselves");
    }

    [Fact]
    public void A_multi_line_title_cannot_print_lines_of_its_own_under_the_prompt()
    {
        // A title free to emit a newline needs no escape sequence to lie: it can simply write
        // its own line under the question and have it read as something Hall9k asked.
        string title = "Adopt issues\nEverything below is approved:\n  - delete the repo";

        string rendered = Render(TaskAddCommand.ObjectivePrompt(title));

        rendered.Trim().Should().NotContain("\n", "outside text does not get to add lines to a prompt");
        rendered.Should().Contain("Adopt issues Everything below is approved:   - delete the repo");
    }

    [Fact]
    public void A_title_that_reverses_its_own_reading_cannot_do_it_at_the_prompt()
    {
        // U+202E RIGHT-TO-LEFT OVERRIDE reverses the visual order of everything after it while
        // leaving the stored string untouched, so a title can read on screen as the opposite of
        // what the draft would record: the human approves one objective and gets another. It is
        // a format character (Cf) rather than a control character, so char.IsControl never saw
        // it, and escaping Spectre's markup does nothing to it either.
        string title = "Adopt issues \u202Eelbaifitsuj si eno yreve\u202C";

        string seed = TaskAddCommand.ObjectiveSeed(title);

        seed.Should().NotContain("\u202E").And.NotContain("\u202C");
        seed.Should().Contain("Adopt issues", "the text that was never a format character reads as itself");
        Render(TaskAddCommand.ObjectivePrompt(title)).Should().NotContain("\u202E");
    }

    [Fact]
    public void The_seed_the_objective_is_taken_from_is_one_line_of_printable_text()
    {
        // The objective is not read only by terminals: it becomes the pull request's title, the
        // branch's slug, and a line in every agent prompt. So the title is folded on the way into
        // the draft, where the body it came from is still carried verbatim as agent context.
        string seed = TaskAddCommand.ObjectiveSeed(
            "  Fix login\u001b[2J\u001b[H\rTask 8a3f: verified\nsafe to merge  ");

        seed.Should().Be("Fix login[2J[HTask 8a3f: verified safe to merge");
    }

    [Fact]
    public void A_title_cannot_close_an_issue_through_the_agents_own_commit_subject()
    {
        // The daemon defuses the pull request's title and body, but this repository merges
        // fast-forward: the agent's own commit subjects land on the default branch, and an agent
        // opens a commit with its task's headline. Defused at the seed, the keyword is dead in the
        // only copy of the objective an agent ever reads.
        TaskAddCommand.ObjectiveSeed("Fix login timeout, resolves #500")
            .Should().Be("Fix login timeout, resolves `#500`");
    }

    [Fact]
    public void The_prompt_offers_exactly_the_seed_that_pressing_enter_records()
    {
        string title = "[BUG] Importer\u001b[2J drops\nthe body";

        Render(TaskAddCommand.ObjectivePrompt(title))
            .Should().Contain(TaskAddCommand.ObjectiveSeed(title),
                "a default the human cannot see is a default they cannot check");
    }

    [Fact]
    public void The_criteria_heading_makes_the_title_safe_the_same_way()
    {
        string rendered = Render(TaskAddCommand.CriteriaHeading("[BUG] Importer\u001b[2J drops the body"));

        rendered.Should().NotContain("\u001b");
        rendered.Should().Contain("[BUG] Importer[2J drops the body");
    }

    [Theory]
    // gh answered with no title at all, or with one there is nothing left of once the characters
    // that act rather than read are gone.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0007\u202E\r")]
    public void A_title_with_nothing_printable_in_it_leaves_no_seed_to_accept(string title)
    {
        // An empty objective is refused by the decider, and the decider runs after the criteria
        // prompt — so a default of "" that a human presses enter on costs them every criterion
        // they had just typed. The prompt says there is nothing to accept instead of offering it.
        TaskAddCommand.ObjectiveSeed(title).Should().BeEmpty();

        string rendered = Render(TaskAddCommand.ObjectivePrompt(title));

        rendered.Should().Contain("no title to seed one from")
            .And.NotContain("()", "a default nobody can read is a default nobody can check");
    }

    private static string Render(string markup)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            // Spectre's default profile enrichers turn ANSI back on whenever they recognise the
            // host CI (GitHub Actions among them), whatever AnsiSupport.No asked for. Left on,
            // the styling Spectre itself emits would put escape sequences in the rendered string
            // and the assertions above would be reading the harness rather than the title.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 200;

        console.Markup(markup);

        return writer.ToString();
    }
}
