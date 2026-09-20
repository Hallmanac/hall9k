using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The <c>h9k project show</c> row for design-review driving (idea b9b09779, piece 3). Its whole
/// job is the distinction Decisions Log #161 established for the other default-on setting: a
/// project that never chose and a project that chose the default value are different facts, and
/// a row that renders them identically is how a setting goes unnoticed.
/// </summary>
public sealed class DesignReviewDriveRowTests
{
    [Fact]
    public void The_row_names_the_effective_value_and_its_origin_at_the_default()
    {
        string row = ProjectShowCommand.DesignReviewDriveRow(
            Project(), ReviewDriveSetting.UnrecordedFor(ReviewPersona.Designer));

        row.Should().Contain("on");
        row.Should().Contain("default");
        row.Should().Contain("h9k project set arx-platform --design-review-drive off");
    }

    [Fact]
    public void The_row_names_an_explicit_opt_out_as_explicit_and_says_what_it_costs()
    {
        string row = ProjectShowCommand.DesignReviewDriveRow(
            Project(), new ReviewDriveSetting(ReviewPersona.Designer, Enabled: false, Recorded: true));

        row.Should().Contain("off");
        row.Should().Contain("explicit");
        row.Should().Contain("code-and-design-file only");
        row.Should().Contain("h9k project set arx-platform --design-review-drive on");
    }

    /// <summary>
    /// Rendered through a real console rather than only inspected as a string: markup that reads
    /// correctly can still throw the moment it is rendered, and <c>h9k project show</c> would
    /// take the whole settings pane down with it.
    /// </summary>
    [Fact]
    public void Both_shapes_render_through_a_real_console()
    {
        foreach (bool enabled in new[] { true, false })
        {
            foreach (bool recorded in new[] { true, false })
            {
                string rendered = Rendered(ProjectShowCommand.DesignReviewDriveRow(
                    Project(), new ReviewDriveSetting(ReviewPersona.Designer, enabled, recorded)));

                rendered.Should().Contain("design-review-drive")
                    .And.NotContain("[dim]", "the markup was rendered, not printed as text");
            }
        }
    }

    private static ProjectDetails Project() => new() { Id = DomainId.New(), Name = "arx-platform" };

    private static string Rendered(string markup)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 500;
        console.MarkupLine(markup);
        return writer.ToString();
    }
}
