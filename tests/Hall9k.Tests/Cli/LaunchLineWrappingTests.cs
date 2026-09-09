using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Orchestrator;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The origin incident (Brian, 2026-09-08 17:20 EDT): pasting <c>h9k orchestrator project
/// hall9k</c>'s own output into a terminal produced a broken command, because
/// <c>AnsiConsole.WriteLine</c> word-wraps at the console's profile width regardless of whether
/// the output is a real terminal or a pipe — verified on the console width a pipe actually
/// measures (80), which still split the launch line into several lines. <see cref="LaunchLineWriter"/>
/// exists to write that one line straight to the console's own writer instead, bypassing Spectre's
/// wrapping renderable entirely; these tests render at a console width far narrower than any of
/// the three print paths that use it (<c>h9k orchestrator project</c>, <c>h9k orchestrator node</c>,
/// and <c>h9k orchestrator launch-text show</c>, via <see cref="OrchestratorReport.Print"/> and
/// <see cref="OrchestratorLaunchTextShowCommand.Print"/>) would ever plausibly see, to prove none
/// of them wrap the command it prints.
/// </summary>
// Capture (below) swaps the process-wide AnsiConsole.Console, the same static TaskWorkClaimTests
// and InstallCommandTests swap; sharing the collection serializes this class against them too.
[Collection("Hall9kHome")]
public sealed class LaunchLineWrappingTests
{
    private const int NarrowWidth = 10;

    private const string LongLaunchCommand =
        "claude --model claude-opus-5 --append-system-prompt-file "
        + "/Users/brianhallmanac/.hall9k/recipes/launch-anchor.md --settings "
        + "/Users/brianhallmanac/.hall9k/recipes/settings.json "
        + "\"You are the node orchestrator. Read AGENTS.md and begin.\"";

    [Fact]
    public void Write_emits_the_text_as_a_single_line_terminated_by_exactly_one_newline()
    {
        string output = Capture(() => LaunchLineWriter.Write(LongLaunchCommand), NarrowWidth);

        output.Should().Be(LongLaunchCommand + "\n");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\n\n")]
    public void Write_collapses_a_line_terminator_already_on_the_text_to_exactly_one_newline(string existingTerminator)
    {
        string output = Capture(() => LaunchLineWriter.Write(LongLaunchCommand + existingTerminator), NarrowWidth);

        output.Should().Be(LongLaunchCommand + "\n");
    }

    [Fact]
    public void Orchestrator_report_prints_the_launch_line_unwrapped_at_a_narrow_console_width()
    {
        LaunchText launchText = new(LaunchText.DefaultCli, LongLaunchCommand);

        string output = Capture(
            () => OrchestratorReport.Print(LaunchText.DefaultCli, launchText, "/tmp/recipe.md", "/tmp/journal.md"),
            NarrowWidth);

        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle(line => line == LongLaunchCommand);
    }

    [Fact]
    public void Launch_text_show_prints_the_launch_line_unwrapped_at_a_narrow_console_width()
    {
        LaunchText launchText = new(LaunchText.DefaultCli, LongLaunchCommand);

        string output = Capture(
            () => OrchestratorLaunchTextShowCommand.Print(LaunchText.DefaultCli, launchText, stored: true, string.Empty),
            NarrowWidth);

        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle(line => line == LongLaunchCommand);
    }

    /// <summary>The global console, swapped for a writer and put back — mirrors TaskWorkClaimTests's own capture.</summary>
    private static string Capture(Action action, int width)
    {
        IAnsiConsole original = AnsiConsole.Console;
        StringWriter writer = new();
        IAnsiConsole captured = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        captured.Profile.Width = width;
        AnsiConsole.Console = captured;
        try
        {
            action();
        }
        finally
        {
            AnsiConsole.Console = original;
        }

        return writer.ToString();
    }
}
