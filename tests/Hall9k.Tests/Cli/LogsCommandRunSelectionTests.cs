using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
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
        // A real RunDispatched's own LeaseGeneration is never 0 (TaskAggregate.LeaseGeneration
        // starts at 0 and every claim records +1) — only a reconstruction ever leaves it at the
        // int default, so a dispatched test row must carry a real one to behave like production.
        LeaseGeneration = isReconstructed ? 0 : 1,
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
    public void With_no_run_option_a_stub_written_before_IsReconstructed_shipped_is_still_skipped()
    {
        // A reconstruction committed before this field existed carries IsReconstructed = false
        // forever (this projection is Inline with no backfill) but never carries a real
        // LeaseGeneration either, so LooksReconstructed still catches it (independent pre-PR
        // review, cycle 1, both lenses).
        RunListItem dispatched = Run(Now.AddHours(-1));
        RunListItem preExistingStub = new()
        {
            Id = DomainId.New(),
            DispatchedAt = Now,
            State = RunState.Dispatched,
            IsReconstructed = false,
            LeaseGeneration = 0,
        };

        RunListItem? selected = LogsCommand.SelectRun([preExistingStub, dispatched], runOption: null);

        selected.Should().BeSameAs(dispatched,
            "a document written before IsReconstructed existed still never carries a real LeaseGeneration");
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

    [Fact]
    public void A_dashes_only_run_option_never_vacuously_matches_the_newest_run()
    {
        // Same shape as the TaskIdResolver/IdeaIdResolver hole: stripping dashes from "-" leaves
        // an empty fragment, and EndsWith("") is true for every run, so this must be refused
        // rather than silently resolving to runsNewestFirst[0].
        RunListItem dispatched = Run(Now);

        Action selectDashesOnly = () => LogsCommand.SelectRun([dispatched], "-");

        selectDashesOnly.Should().Throw<DomainValidationException>()
            .WithMessage("*no characters to match*");
    }
}

/// <summary>
/// LogsCommand.ResolveStreamFile's own delegation-vs-run-level preference (adversarial review,
/// cycle 1, ride-along): a delegated contractor's own transcript wins by default, but a run-level
/// transcript that already exists is never hidden behind an honest-looking "no stream file" just
/// because the run was later delegated and that contractor's own file turned out unusable.
/// </summary>
public sealed class LogsCommandStreamFileSelectionTests : IDisposable
{
    private readonly string _runDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-logs-{Guid.NewGuid():N}");

    public LogsCommandStreamFileSelectionTests() => Directory.CreateDirectory(_runDirectory);

    public void Dispose() => Directory.Delete(_runDirectory, recursive: true);

    [Fact]
    public void With_no_delegations_the_run_level_file_is_selected()
    {
        File.WriteAllText(RunPaths.StreamFile(_runDirectory), "{}\n");

        string selected = LogsCommand.ResolveStreamFile(_runDirectory, runDetails: null);

        selected.Should().Be(RunPaths.StreamFile(_runDirectory));
    }

    [Fact]
    public void With_a_usable_delegation_file_the_latest_delegations_own_file_wins()
    {
        File.WriteAllText(RunPaths.StreamFile(_runDirectory), "{}\n");
        string delegationFile = RunPaths.SessionStreamFile(_runDirectory, "delegation-2");
        File.WriteAllText(delegationFile, "{}\n");
        RunDetails runDetails = WithDelegations("delegation-1", "delegation-2");

        string selected = LogsCommand.ResolveStreamFile(_runDirectory, runDetails);

        selected.Should().Be(delegationFile, "the newest delegation's own transcript is preferred over the run-level one");
    }

    [Fact]
    public void When_the_latest_delegations_file_does_not_exist_it_falls_back_to_the_run_level_file()
    {
        string runLevelFile = RunPaths.StreamFile(_runDirectory);
        File.WriteAllText(runLevelFile, "{}\n");
        RunDetails runDetails = WithDelegations("delegation-1");
        // Deliberately never write RunPaths.SessionStreamFile(_runDirectory, "delegation-1"):
        // the contractor was killed before it ever produced output.

        string selected = LogsCommand.ResolveStreamFile(_runDirectory, runDetails);

        selected.Should().Be(runLevelFile,
            "a missing delegation transcript must not hide a run-level one that does exist");
    }

    [Fact]
    public void When_the_latest_delegations_file_is_empty_it_falls_back_to_the_run_level_file()
    {
        string runLevelFile = RunPaths.StreamFile(_runDirectory);
        File.WriteAllText(runLevelFile, "{}\n");
        File.WriteAllText(RunPaths.SessionStreamFile(_runDirectory, "delegation-1"), string.Empty);
        RunDetails runDetails = WithDelegations("delegation-1");

        string selected = LogsCommand.ResolveStreamFile(_runDirectory, runDetails);

        selected.Should().Be(runLevelFile,
            "a delegation file that was created but never written to is not a usable transcript");
    }

    [Fact]
    public void When_neither_file_exists_the_run_level_path_is_still_returned_for_the_honest_404()
    {
        RunDetails runDetails = WithDelegations("delegation-1");

        string selected = LogsCommand.ResolveStreamFile(_runDirectory, runDetails);

        selected.Should().Be(RunPaths.StreamFile(_runDirectory));
    }

    private RunDetails WithDelegations(params string[] sessionFileKeys)
    {
        RunDetails runDetails = new() { Id = DomainId.New() };
        foreach (string key in sessionFileKeys)
        {
            runDetails.PhaseDelegations.Add(new PhaseDelegation(
                DateTimeOffset.UtcNow, "note", Guid.NewGuid(), "session-name", key, AgentModel.Unknown));
        }

        return runDetails;
    }
}
