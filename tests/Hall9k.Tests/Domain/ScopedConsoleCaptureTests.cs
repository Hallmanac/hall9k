using FluentAssertions;
using Hall9k.Tests.TestSupport;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The guarantee <see cref="ScopedConsoleCapture"/> and <see cref="ScopedAnsiConsoleCapture"/>
/// exist for, stated as tests rather than assumed: a capture holds what the capturing flow wrote
/// and nothing else, whatever any other flow in the process is writing at the same moment.
/// <para>
/// Each cross-flow case nests an inner capture inside an outer one and starts the other flow
/// between them. That is the shape of the incidents these helpers were written for — one test's
/// output arriving inside another's assertion — and nesting is what makes the case deterministic
/// and quiet: the write is proved absent from the inner buffer <em>and</em> present in the outer
/// one, so the test can tell "correctly routed elsewhere" from "never happened", and the real
/// console never sees a line of it either way.
/// </para>
/// </summary>
public sealed class ScopedConsoleCaptureTests
{
    [Fact]
    public void A_capture_holds_what_this_flow_wrote_to_standard_error()
    {
        using ScopedConsoleCapture captured = ScopedConsoleCapture.StandardError();

        Console.Error.WriteLine("a warning this test's own code under test wrote");

        captured.Text.Should().Contain("a warning this test's own code under test wrote");
    }

    [Fact]
    public void A_standard_error_capture_never_holds_what_went_to_standard_output()
    {
        using ScopedConsoleCapture error = ScopedConsoleCapture.StandardError();
        using ScopedConsoleCapture output = ScopedConsoleCapture.StandardOutput();

        Console.Out.WriteLine("an ordinary line");
        Console.Error.WriteLine("a warning");

        error.Text.Should().Be("a warning" + Environment.NewLine);
        output.Text.Should().Be("an ordinary line" + Environment.NewLine);
    }

    [Fact]
    public async Task Another_flows_write_to_standard_error_never_enters_this_flows_capture()
    {
        using ScopedConsoleCapture elsewhere = ScopedConsoleCapture.StandardError();

        // Started while only `elsewhere` is in scope, so the execution context this flow carries
        // is the one a sibling test's flow carries: it has never seen the inner scope below.
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task otherFlow = Task.Run(async () =>
        {
            await release.Task;
            Console.Error.WriteLine("another flow's line");
        });

        using (ScopedConsoleCapture mine = ScopedConsoleCapture.StandardError())
        {
            release.SetResult();
            await otherFlow;

            mine.Text.Should().BeEmpty(
                "this is the exact failure both helpers exist for: another flow's output landing inside " +
                "a capture whose owner is about to assert the buffer is empty");
        }

        elsewhere.Text.Should().Contain("another flow's line",
            "the write is routed to where that flow's own scope points, never dropped");
    }

    [Fact]
    public void A_write_after_the_scope_ends_is_no_longer_captured()
    {
        using ScopedConsoleCapture outer = ScopedConsoleCapture.StandardError();

        using (ScopedConsoleCapture inner = ScopedConsoleCapture.StandardError())
        {
            Console.Error.Write("inside");
        }

        Console.Error.Write("after");

        outer.Text.Should().Be("after", "disposing the inner scope restores the one it nested inside");
    }

    [Fact]
    public async Task Another_flows_render_never_enters_this_flows_ansi_capture()
    {
        using ScopedAnsiConsoleCapture elsewhere = ScopedAnsiConsoleCapture.Begin();

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task otherFlow = Task.Run(async () =>
        {
            await release.Task;
            AnsiConsole.MarkupLine("[red]another flow's rendered row[/]");
        });

        using (ScopedAnsiConsoleCapture mine = ScopedAnsiConsoleCapture.Begin())
        {
            release.SetResult();
            await otherFlow;

            mine.Text.Should().BeEmpty(
                "origin incident (2026-09-17 11:15 EDT, run 01a0af3c): ToolDoctorTests asserted on a buffer " +
                "holding StatusCommandMergedWithoutCopilotReviewTests' rendered rows");
        }

        elsewhere.Text.Should().Contain("another flow's rendered row");
    }

    [Fact]
    public void An_ansi_capture_holds_the_rendered_text_rather_than_the_markup_that_styled_it()
    {
        string rendered = ScopedAnsiConsoleCapture.Capture(() => AnsiConsole.MarkupLine("[red]a styled warning[/]"));

        rendered.Should().Contain("a styled warning");
        rendered.Should().NotContain("[red]", "the captured console emits no color and no ANSI, so a style tag is consumed, never printed");
    }
}
