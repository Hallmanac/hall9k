using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// A scoped `dotnet test` filter that intersects with the gate's own already-configured filter
/// to nothing must never stand an empty run in for a passed one (task: a fix cycle's verification
/// gate; independent pre-PR review, cycle 1) — but the per-source "No test matches" warning VSTest
/// prints is emitted once per SOURCE, not once per run, so a multi-project solution where the
/// filter matched in one project and missed another must not be called vacuous just because the
/// warning appears somewhere in the combined output (cycle-3 finding).
/// <see cref="VerificationRunner.ScopedRunExecutedNoTests"/> is checked here at the string level
/// rather than by spawning a real gate process.
/// </summary>
public sealed class VerificationRunnerScopedGateTests
{
    [Theory]
    [InlineData(
        "A total of 1 test files matched the specified pattern.\n" +
        "No test matches the given testcase filter `FullyQualifiedName~WidgetTests` in " +
        "/repo/tests/Hall9k.Tests/bin/Debug/net10.0/Hall9k.Tests.dll\n")]
    [InlineData("no test matches the given testcase filter `(Category!=RequiresDocker)&(FullyQualifiedName~WidgetTests)`")]
    [InlineData("")]
    public void An_empty_filter_intersection_is_recognized(string gateOutput) =>
        VerificationRunner.ScopedRunExecutedNoTests(gateOutput).Should().BeTrue();

    [Theory]
    [InlineData("Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 1 s")]
    [InlineData("Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 1 s")]
    public void A_genuine_test_run_is_never_mistaken_for_an_empty_one(string gateOutput) =>
        VerificationRunner.ScopedRunExecutedNoTests(gateOutput).Should().BeFalse();

    /// <summary>
    /// `\d+` matches any Unicode decimal digit and any digit run length, so a localized VSTest
    /// build's non-ASCII digits or a pathologically wide count must never fault the whole run with
    /// an unhandled `FormatException`/`OverflowException` — the class degrades honestly everywhere
    /// else it cannot read a signal confidently, and this is no exception (adversarial review
    /// finding).
    /// </summary>
    [Fact]
    public void An_unparseable_total_count_never_faults_the_gate() =>
        VerificationRunner.ScopedRunExecutedNoTests("Passed!  - Total:     99999999999999999999")
            .Should().Be(true, "an unparseable count is never confirmed nonzero, so it degrades the same honest direction as a missing summary");

    [Fact]
    public void A_multi_project_run_where_one_source_matched_and_one_did_not_is_never_mistaken_for_vacuous() =>
        VerificationRunner.ScopedRunExecutedNoTests(
            "No test matches the given testcase filter `FullyQualifiedName~WidgetTests` in " +
            "/repo/tests/Hall9k.Tests/bin/Debug/net10.0/Hall9k.Tests.dll\n" +
            "Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 1 s - " +
            "Hall9k.Tests.Integration.dll")
            .Should().BeFalse();

    [Fact]
    public void The_filter_is_appended_to_a_plain_dotnet_test_command()
    {
        VerificationRunner.ApplyTestFilter("dotnet test --no-build", "FullyQualifiedName~WidgetTests")
            .Should().Be("""dotnet test --no-build --filter "FullyQualifiedName~WidgetTests" """.Trim());
    }

    [Fact]
    public void The_filter_combines_with_an_existing_filter_on_a_plain_command()
    {
        VerificationRunner.ApplyTestFilter(
                """dotnet test --filter "Category!=RequiresDocker" """.Trim(), "FullyQualifiedName~WidgetTests")
            .Should().Be("""dotnet test --filter "(Category!=RequiresDocker)&(FullyQualifiedName~WidgetTests)" """.Trim());
    }

    /// <summary>
    /// A single-quoted existing filter (a natural spelling since the gate string is handed to
    /// `/bin/sh -c`) must be recognized the same as a double-quoted one, with the quote characters
    /// excluded from the captured value — the unquoted `\S+` alternative previously captured the
    /// quote marks themselves, so `dotnet test --filter 'Category!=RequiresDocker'` rewrote the
    /// merged filter to `('Category!=RequiresDocker')&(...)`, which VSTest evaluates as a
    /// comparison against the literal string `'RequiresDocker'` (with quotes), silently
    /// neutralizing the project's own exclusion (adversarial review finding).
    /// </summary>
    [Fact]
    public void The_filter_combines_with_an_existing_filter_written_with_single_quotes()
    {
        VerificationRunner.ApplyTestFilter(
                "dotnet test --filter 'Category!=RequiresDocker'", "FullyQualifiedName~WidgetTests")
            .Should().Be("""dotnet test --filter "(Category!=RequiresDocker)&(FullyQualifiedName~WidgetTests)" """.Trim());
    }

    /// <summary>
    /// `--filter=` is VSTest's own accepted alternate spelling of `--filter ` (cycle-3 finding) —
    /// an existing filter written this way must still be found and merged, never duplicated into
    /// a second `--filter` flag `dotnet test` does not accept.
    /// </summary>
    [Fact]
    public void The_filter_combines_with_an_existing_filter_written_with_an_equals_sign()
    {
        VerificationRunner.ApplyTestFilter(
                """dotnet test --filter="Category!=RequiresDocker" """.Trim(), "FullyQualifiedName~WidgetTests")
            .Should().Be("""dotnet test --filter "(Category!=RequiresDocker)&(FullyQualifiedName~WidgetTests)" """.Trim());
    }

    /// <summary>`--filter:` is VSTest's other accepted alternate spelling (cycle-3 finding), unquoted here since that form is common without one.</summary>
    [Fact]
    public void The_filter_combines_with_an_existing_filter_written_with_a_colon()
    {
        VerificationRunner.ApplyTestFilter("dotnet test --filter:Category!=RequiresDocker", "FullyQualifiedName~WidgetTests")
            .Should().Be("""dotnet test --filter "(Category!=RequiresDocker)&(FullyQualifiedName~WidgetTests)" """.Trim());
    }

    /// <summary>
    /// A gate's own command is free-form shell, so `dotnet test` can be chained with `&amp;&amp;`
    /// into something else entirely — the filter must land inside the `dotnet test` invocation,
    /// never appended to the end of the whole compound command (independent pre-PR review, cycle
    /// 2: appending at the end ran the suite unscoped and handed the trailing program an option it
    /// does not accept).
    /// </summary>
    [Fact]
    public void The_filter_lands_inside_a_compound_commands_dotnet_test_segment_not_at_the_end()
    {
        VerificationRunner.ApplyTestFilter(
                "dotnet test --no-build && dotnet format --verify-no-changes", "FullyQualifiedName~WidgetTests")
            .Should().Be(
                """dotnet test --no-build --filter "FullyQualifiedName~WidgetTests" && dotnet format --verify-no-changes""");
    }

    [Fact]
    public void The_filter_lands_before_a_pipe_and_ignores_file_descriptor_duplication()
    {
        VerificationRunner.ApplyTestFilter("dotnet test 2>&1 | tail -200", "FullyQualifiedName~WidgetTests")
            .Should().Be("""dotnet test 2>&1 --filter "FullyQualifiedName~WidgetTests" | tail -200""");
    }

    [Fact]
    public void An_existing_filter_after_the_dotnet_test_segment_is_left_untouched()
    {
        VerificationRunner.ApplyTestFilter(
                """dotnet test && dotnet format --filter "SomethingUnrelated" """.Trim(), "FullyQualifiedName~WidgetTests")
            .Should().Be(
                """dotnet test --filter "FullyQualifiedName~WidgetTests" && dotnet format --filter "SomethingUnrelated" """
                    .Trim());
    }
}
