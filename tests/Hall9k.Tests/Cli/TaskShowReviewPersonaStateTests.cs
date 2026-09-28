using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>TaskShowCommand.ReviewPersonaState</c>'s three skip reasons — extracted out of
/// <c>WriteReviewPersonas</c> the same way <see cref="TaskShowCommand.AssigneeMarkup"/> already
/// is, because before this the docs-only skip had no row of its own: <c>WriteReviewPersonas</c>
/// only told the fork skip apart from "no review prompt registered yet", and a docs-only-skipped
/// Security persona read as the latter — false, and contradicting the findings report's own
/// "Skipped: every path this pull request changed matched…" line for the same run (independent
/// pre-PR review, cycle 1, both lenses, medium).
/// </summary>
public sealed class TaskShowReviewPersonaStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fork_skipped_persona_reads_as_fork_head()
    {
        RunDetails run = new() { PrReviewForkSkippedPersonas = [ReviewPersona.Qa] };

        TaskShowCommand.ReviewPersonaState(ReviewPersona.Qa, run, skipped: true)
            .Should().Contain("skipped — fork head");
    }

    /// <summary>The defect this file exists to pin: a docs-only skip must read distinctly from "no prompt registered".</summary>
    [Fact]
    public void A_docs_only_skipped_persona_reads_distinctly_from_no_prompt_registered()
    {
        RunDetails run = new() { PrReviewDocsOnlySkippedPersonas = [ReviewPersona.Security] };

        string state = TaskShowCommand.ReviewPersonaState(ReviewPersona.Security, run, skipped: true);

        state.Should().Contain("docs-only");
        state.Should().NotContain("no review prompt registered yet",
            "a docs-only skip is not the same fact as an unregistered prompt, and the pane must not claim a "
            + "broken install for a persona the report already explained was skipped by design");
    }

    [Fact]
    public void A_persona_skipped_for_neither_reason_reads_as_no_prompt_registered()
    {
        RunDetails run = new();

        TaskShowCommand.ReviewPersonaState(ReviewPersona.Designer, run, skipped: true)
            .Should().Contain("no review prompt registered yet");
    }

    [Fact]
    public void A_failed_session_names_its_reason_rather_than_a_skip_state()
    {
        RunDetails run = new()
        {
            PrReviewPersonaSessionFailures = new Dictionary<string, ReviewPersonaSessionFailure>
            {
                ["security"] = new(ReviewPersona.Security, "the session's process died with no result", Now),
            },
        };

        TaskShowCommand.ReviewPersonaState(ReviewPersona.Security, run, skipped: false)
            .Should().Contain("failed").And.Contain("the session's process died with no result");
    }

    [Fact]
    public void A_reported_persona_reads_as_report_in()
    {
        RunDetails run = new() { PrReviewPersonasReported = [ReviewPersona.Engineer] };

        TaskShowCommand.ReviewPersonaState(ReviewPersona.Engineer, run, skipped: false)
            .Should().Contain("report in");
    }

    [Fact]
    public void A_still_running_persona_on_a_live_run_reads_as_running()
    {
        RunDetails run = new() { State = RunState.Running };

        TaskShowCommand.ReviewPersonaState(ReviewPersona.Engineer, run, skipped: false)
            .Should().Contain("running");
    }

    [Fact]
    public void A_persona_with_no_report_on_a_terminal_run_reads_as_no_report()
    {
        RunDetails run = new() { State = RunState.Failed };

        TaskShowCommand.ReviewPersonaState(ReviewPersona.Engineer, run, skipped: false)
            .Should().Contain("no report").And.Contain("the run ended first");
    }
}
