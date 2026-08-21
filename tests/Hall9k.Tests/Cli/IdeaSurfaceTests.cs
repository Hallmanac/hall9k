using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The idea surfaces as a human reads them (Decisions Log #35): a browse list that teaches
/// promotion, an honest absence where a project would be, and a promotion that shows exactly
/// what it took from the note rather than asking to be trusted.
/// </summary>
public sealed class IdeaSurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<int> Widths => [110, 120, 160, 200];

    [Theory]
    [MemberData(nameof(Widths))]
    public void The_browse_table_fits_one_line_per_idea(int width)
    {
        IReadOnlyList<IdeaRow> rows = Rows();

        string[] lines = Render(
            IdeaListCommand.Rows(rows, showState: true, scoped: false, width, Now), width);

        // Top border, header, header rule, one line per idea, bottom border.
        lines.Should().HaveCount(rows.Count + 4, "a note that wraps stacks the list down the page");
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void The_browse_table_still_fits_when_the_state_and_project_columns_drop(int width)
    {
        IReadOnlyList<IdeaRow> rows = Rows();

        string[] lines = Render(
            IdeaListCommand.Rows(rows, showState: false, scoped: true, width, Now), width);

        lines.Should().HaveCount(rows.Count + 4, "dropping columns widens the note, it does not wrap it");
    }

    [Fact]
    public void An_idea_with_no_project_says_so_rather_than_showing_an_empty_cell()
    {
        IdeaRow row = Row("Ideas should own their discovery", projectName: null);

        row.ProjectMarkup.Should().Contain("none");
    }

    [Fact]
    public void The_footer_teaches_promotion_because_that_is_what_the_list_is_for()
    {
        string footer = IdeaListCommand.Footer(
            matched: 3, shown: 3, States(captured: 3), new IdeaListCommand.Settings(), project: null,
            state: IdeaState.Captured);

        footer.Should().Contain("h9k idea promote");
        footer.Should().Contain("refinement", "promotion is the hand-off between the two phases");
    }

    [Fact]
    public void The_footer_says_what_the_bound_held_back_and_how_to_see_it()
    {
        string footer = IdeaListCommand.Footer(
            matched: 42, shown: 20, States(captured: 42), new IdeaListCommand.Settings(), project: null,
            state: IdeaState.Captured);

        footer.Should().Contain("20 of 42");
        footer.Should().Contain("22 held back");
        footer.Should().Contain("h9k idea list --all");
    }

    /// <summary>Widening the bound must not narrow the state: --all alone means the default filter.</summary>
    [Fact]
    public void The_footer_keeps_the_state_the_reader_typed_when_it_suggests_seeing_the_rest()
    {
        string footer = IdeaListCommand.Footer(
            matched: 42, shown: 20, States(promoted: 42),
            new IdeaListCommand.Settings { State = "promoted" }, project: null, state: IdeaState.Promoted);

        footer.Should().Contain("h9k idea list --all --state promoted",
            "expanding a promoted view must show more promoted ideas, not the captured ones");
    }

    [Fact]
    public void The_footer_counts_what_the_default_state_filter_is_hiding()
    {
        string footer = IdeaListCommand.Footer(
            matched: 4, shown: 4, States(captured: 4, promoted: 5, discarded: 2),
            new IdeaListCommand.Settings(), project: null, state: IdeaState.Captured);

        footer.Should().Contain("5 promoted, 2 discarded");
        footer.Should().Contain("--state all", "a filtered view must never read as the whole truth");
    }

    [Fact]
    public void The_footer_names_the_states_it_is_hiding_rather_than_assuming_the_default_view()
    {
        string footer = IdeaListCommand.Footer(
            matched: 1, shown: 1, States(captured: 3, promoted: 1, discarded: 1),
            new IdeaListCommand.Settings(), project: null, state: IdeaState.Promoted);

        footer.Should().Contain("3 captured, 1 discarded",
            "asking for promoted ideas hides the ones still in discovery, not the promoted ones");
        footer.Should().NotContain("promoted or discarded");
    }

    [Fact]
    public void The_footer_counts_only_inside_the_project_the_reader_scoped_to()
    {
        ProjectDetails project = new() { Id = DomainId.New(), Name = "alpha" };

        string footer = IdeaListCommand.Footer(
            matched: 1, shown: 1, States(captured: 1),
            new IdeaListCommand.Settings { Project = "alpha" }, project, IdeaState.Captured);

        footer.Should().Contain("1 of 1 in alpha captured");
        footer.Should().NotContain("see them with",
            "ideas in other projects were excluded by a filter the reader typed, not hidden from them");
    }

    /// <summary>A state filter that hides nothing says nothing — the footer never invents a hole.</summary>
    [Fact]
    public void The_footer_stays_quiet_when_the_state_filter_is_hiding_nothing()
    {
        string footer = IdeaListCommand.Footer(
            matched: 3, shown: 3, States(captured: 3), new IdeaListCommand.Settings(), project: null,
            state: IdeaState.Captured);

        footer.Should().NotContain("--state all");
    }

    private static IReadOnlyList<IdeaState> States(int captured = 0, int promoted = 0, int discarded = 0) =>
    [
        .. Enumerable.Repeat(IdeaState.Captured, captured),
        .. Enumerable.Repeat(IdeaState.Promoted, promoted),
        .. Enumerable.Repeat(IdeaState.Discarded, discarded),
    ];

    [Theory]
    [InlineData(null, "Captured")]
    [InlineData("captured", "Captured")]
    [InlineData("Promoted", "Promoted")]
    [InlineData("discarded", "Discarded")]
    public void The_state_filter_speaks_the_idea_vocabulary(string? input, string expected)
    {
        IdeaListCommand.ParseState(input)!.Value.Should().Be(expected);
    }

    [Fact]
    public void Asking_for_every_state_means_no_filter_and_a_typo_is_refused_with_the_vocabulary()
    {
        IdeaListCommand.ParseState("all").Should().BeNull();

        Action act = () => IdeaListCommand.ParseState("parked");

        act.Should().Throw<DomainValidationException>().WithMessage("*captured, promoted, discarded, all*");
    }

    [Fact]
    public void Promotion_seeds_the_draft_from_the_note_and_says_so_by_showing_the_split()
    {
        IdeaSeed seed = IdeaPromoteCommand.Seed(
            "Give every idea a discovery workspace. Notes and prototypes need somewhere to live.",
            objective: null);

        seed.Objective.Should().Be("Give every idea a discovery workspace.");
        seed.Context.Should().Be("Notes and prototypes need somewhere to live.");
    }

    [Fact]
    public void An_explicit_objective_consumes_nothing_so_the_whole_note_rides_along_as_context()
    {
        IdeaSeed seed = IdeaPromoteCommand.Seed(
            "Give every idea a discovery workspace. Notes and prototypes need somewhere to live.",
            objective: "Give ideas a workspace directory");

        seed.Objective.Should().Be("Give ideas a workspace directory");
        seed.Context.Should().Be(
            "Give every idea a discovery workspace. Notes and prototypes need somewhere to live.",
            "nothing was taken out of the note, so none of it is dropped");
    }

    [Fact]
    public void A_long_first_sentence_is_called_a_long_first_sentence_not_a_missing_break()
    {
        IdeaSeed seed = IdeaPromoteCommand.Seed(
            "Make the idea a first-class concept with its own discovery phase so that capture costs "
            + "nothing at all and the funnel finally has a front door. Also it should teach promotion.",
            objective: null);

        string? nudge = IdeaPromoteCommand.SharpenNudge(seed.Objective, seed.Context, objectiveGiven: false);

        seed.Context.Should().Be("Also it should teach promotion.", "the note did break into two sentences");
        nudge.Should().Contain("first sentence runs long");
        nudge.Should().NotContain("no sentence break");
    }

    [Fact]
    public void A_note_that_never_breaks_is_the_one_told_the_whole_of_it_became_the_objective()
    {
        IdeaSeed seed = IdeaPromoteCommand.Seed(
            "Make the idea a first-class concept with its own discovery phase so that capture costs "
            + "nothing at all and the funnel finally has a front door",
            objective: null);

        string? nudge = IdeaPromoteCommand.SharpenNudge(seed.Objective, seed.Context, objectiveGiven: false);

        seed.Context.Should().BeNull();
        nudge.Should().Contain("single sentence");
    }

    [Fact]
    public void A_short_objective_and_an_objective_the_human_wrote_are_both_left_alone()
    {
        IdeaPromoteCommand.SharpenNudge("Give ideas a workspace", context: null, objectiveGiven: false)
            .Should().BeNull("a short objective needs no sharpening");

        IdeaPromoteCommand.SharpenNudge(
                new string('x', 200), context: null, objectiveGiven: true)
            .Should().BeNull("the human wrote it; the split had no hand in it");
    }

    [Fact]
    public void The_draft_carries_the_workspace_pointer_rather_than_the_files()
    {
        Guid ideaId = DomainId.New();

        string context = IdeaPromoteCommand.AgentContext(ideaId, "The rest of what the note said.");

        context.Should().Contain("The rest of what the note said.");
        context.Should().Contain(IdeaPaths.WorkspaceDirectory(ideaId), "the pointer is what promotion carries");
        context.Should().Contain("Discovery workspace");
    }

    [Fact]
    public void A_note_with_nothing_left_over_still_hands_the_workspace_to_the_agent()
    {
        Guid ideaId = DomainId.New();

        string context = IdeaPromoteCommand.AgentContext(ideaId, context: null);

        context.Should().StartWith("Discovery workspace");
        context.Should().Contain(IdeaPaths.WorkspaceDirectory(ideaId));
    }

    private static IReadOnlyList<IdeaRow> Rows() =>
    [
        Row("Make the idea a first-class concept with its own discovery phase, captured with zero friction "
            + "and promoted once it has intent", "hall9k"),
        Row("Stacked PRs for dependency chains", "hall9k-docs"),
        Row("Ideas captured from a phone once the P2P sync lands", projectName: null),
    ];

    private static IdeaRow Row(string text, string? projectName) =>
        new(DomainId.New(),
            text,
            projectName is null ? null : DomainId.New(),
            projectName,
            IdeaState.Captured,
            PromotedTaskId: null,
            Now.AddHours(-5));

    private static string[] Render(IRenderable renderable, int width)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = width;
        console.Write(renderable);

        return [.. writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)];
    }
}
