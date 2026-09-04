using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// LogsCommand.SelectRun's own newest-run pick (independent pre-PR review, cycle 1,
/// conformance): a task whose newest run is a CloseoutEngine reconstruction stub must still
/// resolve to the run that actually produced a transcript, not the stub's own unreadable
/// directory.
/// </summary>
public sealed class LogsCommandRunSelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static RunListItem Run(DateTimeOffset dispatchedAt, bool isReconstructed = false) => new()
    {
        Id = DomainId.New(),
        DispatchedAt = dispatchedAt,
        State = RunState.Dispatched,
        IsReconstructed = isReconstructed,
    };

    [Fact]
    public void With_no_run_option_the_newest_run_that_actually_dispatched_is_picked_over_a_newer_stub()
    {
        RunListItem dispatched = Run(Now.AddHours(-1));
        RunListItem stub = Run(Now, isReconstructed: true);

        // Caller orders newest first, exactly as LogsCommand's own query does.
        RunListItem? selected = LogsCommand.SelectRun([stub, dispatched], runOption: null);

        selected.Should().BeSameAs(dispatched,
            "the stub never dispatched and so never wrote a transcript — it must not shadow the run that did");
    }

    [Fact]
    public void With_no_run_option_and_no_transcript_bearing_run_the_stub_is_still_returned()
    {
        RunListItem stub = Run(Now, isReconstructed: true);

        RunListItem? selected = LogsCommand.SelectRun([stub], runOption: null);

        selected.Should().BeSameAs(stub,
            "a stub-only task still needs a run handed back, so the caller can give its usual honest 404");
    }

    [Fact]
    public void With_no_run_option_and_no_stub_at_all_the_newest_run_is_picked_as_before()
    {
        RunListItem older = Run(Now.AddHours(-1));
        RunListItem newest = Run(Now);

        RunListItem? selected = LogsCommand.SelectRun([newest, older], runOption: null);

        selected.Should().BeSameAs(newest);
    }

    [Fact]
    public void An_explicit_run_option_matches_by_trailing_id_fragment_even_when_it_is_a_stub()
    {
        RunListItem dispatched = Run(Now.AddHours(-1));
        RunListItem stub = Run(Now, isReconstructed: true);
        string fragment = stub.Id.ToString("N")[^6..];

        RunListItem? selected = LogsCommand.SelectRun([stub, dispatched], fragment);

        selected.Should().BeSameAs(stub, "an explicit --run always overrides the transcript-favoring default");
    }

    [Fact]
    public void An_explicit_run_option_matching_nothing_returns_null()
    {
        RunListItem dispatched = Run(Now);

        LogsCommand.SelectRun([dispatched], "does-not-exist").Should().BeNull();
    }
}
