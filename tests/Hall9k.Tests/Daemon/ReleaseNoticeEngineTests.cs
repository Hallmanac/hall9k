using FluentAssertions;
using Hall9k.Connectors.Releases;
using Hall9k.Daemon.Releases;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// One release check against a fake lookup and an in-memory node stream (house rule: nothing here
/// touches GitHub, git, or a database): what it records, what it leaves alone, and how often it
/// complains. The cadence rule itself is <see cref="Domain.ReleaseNoticeCadenceTests"/>'s; this is
/// the engine that applies it and the failure handling around it.
/// </summary>
public sealed class ReleaseNoticeEngineTests
{
    private const string Running = "0.13.0";
    private static readonly Guid NodeId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly ReleaseChannelResolution Channel = new(ReleaseChannel.Cleared, null);

    private readonly ManualTimeProvider clock = new(Start);
    private readonly FakeNodeStream stream = new();
    private readonly ListLogger<ReleaseNoticeEngine> logger = new();
    private LatestReleaseResult answer = LatestReleaseResult.Found(Channel, "v0.13.1");

    private ReleaseNoticeEngine Engine(string running = Running) =>
        new(_ => Task.FromResult(answer), stream, running, clock, logger);

    [Fact]
    public async Task A_newer_release_is_recorded_as_a_first_notice_with_everything_the_card_names()
    {
        DateTimeOffset published = Start.AddHours(-3);
        answer = LatestReleaseResult.Found(Channel, "v0.13.1", published);

        ReleaseCheckResult result = await Engine().CheckOnceAsync(NodeId, CancellationToken.None);

        result.Should().Be(ReleaseCheckResult.NoticeRecorded);
        stream.Events.Should().ContainSingle().Which.Should().Be(
            new ReleaseNoticeIssued(NodeId, "v0.13.1", published, Start, Running, IsReminder: false));
    }

    [Fact]
    public async Task The_same_release_is_not_repeated_within_a_day_and_is_a_reminder_on_the_first_check_past_it()
    {
        ReleaseNoticeEngine engine = Engine();
        await engine.CheckOnceAsync(NodeId, CancellationToken.None);

        await AdvanceAsync(TimeSpan.FromHours(6));
        (await engine.CheckOnceAsync(NodeId, CancellationToken.None)).Should().Be(ReleaseCheckResult.NothingNew);
        await AdvanceAsync(TimeSpan.FromHours(6));
        await AdvanceAsync(TimeSpan.FromHours(6));
        (await engine.CheckOnceAsync(NodeId, CancellationToken.None)).Should().Be(ReleaseCheckResult.NothingNew, "eighteen hours in");
        await AdvanceAsync(TimeSpan.FromHours(6));
        (await engine.CheckOnceAsync(NodeId, CancellationToken.None)).Should().Be(ReleaseCheckResult.ReminderRecorded);

        stream.Events.Select(notice => notice.IsReminder).Should().Equal(false, true);
        stream.Events[1].ObservedAt.Should().Be(Start.AddHours(24));
    }

    [Fact]
    public async Task A_newer_release_is_announced_at_the_next_check_whatever_the_clock_says()
    {
        ReleaseNoticeEngine engine = Engine();
        await engine.CheckOnceAsync(NodeId, CancellationToken.None);

        answer = LatestReleaseResult.Found(Channel, "v0.13.2");
        await AdvanceAsync(TimeSpan.FromHours(6));
        (await engine.CheckOnceAsync(NodeId, CancellationToken.None)).Should().Be(ReleaseCheckResult.NoticeRecorded);

        stream.Events.Select(notice => (notice.Tag, notice.IsReminder)).Should().Equal(("v0.13.1", false), ("v0.13.2", false));
    }

    [Fact]
    public async Task Once_the_node_runs_the_newest_release_nothing_more_is_recorded()
    {
        await Engine().CheckOnceAsync(NodeId, CancellationToken.None);

        await AdvanceAsync(TimeSpan.FromDays(3));
        (await Engine(running: "0.13.1").CheckOnceAsync(NodeId, CancellationToken.None)).Should().Be(ReleaseCheckResult.NothingNew);

        stream.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task A_restart_neither_repeats_a_notice_nor_resets_the_clock()
    {
        await Engine().CheckOnceAsync(NodeId, CancellationToken.None);

        // A new engine over the same recorded stream is a restarted daemon: nothing is carried but what the node recorded.
        await AdvanceAsync(TimeSpan.FromHours(10));
        (await Engine().CheckOnceAsync(NodeId, CancellationToken.None)).Should().Be(ReleaseCheckResult.NothingNew);
        await AdvanceAsync(TimeSpan.FromHours(14));
        (await Engine().CheckOnceAsync(NodeId, CancellationToken.None)).Should().Be(ReleaseCheckResult.ReminderRecorded);

        stream.Events.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_release_still_uploading_its_assets_records_nothing_and_warns_nobody()
    {
        answer = LatestReleaseResult.NotYetComplete(Channel, "v0.13.1", ["checksums.txt"]);

        (await Engine().CheckOnceAsync(NodeId, CancellationToken.None)).Should().Be(ReleaseCheckResult.NothingNew);

        stream.Events.Should().BeEmpty();
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task No_published_release_records_nothing_and_warns_nobody()
    {
        answer = LatestReleaseResult.Failed(LatestReleaseOutcome.NoRelease, Channel, "gh release list", "no published release");

        (await Engine().CheckOnceAsync(NodeId, CancellationToken.None)).Should().Be(ReleaseCheckResult.NothingNew);

        stream.Events.Should().BeEmpty();
        logger.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(LatestReleaseOutcome.GhMissing)]
    [InlineData(LatestReleaseOutcome.GhFailed)]
    [InlineData(LatestReleaseOutcome.TimedOut)]
    [InlineData(LatestReleaseOutcome.Unparseable)]
    [InlineData(LatestReleaseOutcome.UnsupportedPlatform)]
    public async Task A_failed_lookup_records_nothing_and_logs_one_warning(LatestReleaseOutcome outcome)
    {
        answer = LatestReleaseResult.Failed(outcome, Channel, "gh release view", "it broke");

        (await Engine().CheckOnceAsync(NodeId, CancellationToken.None)).Should().Be(ReleaseCheckResult.LookupFailed);

        stream.Events.Should().BeEmpty();
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Message.Should().Contain(outcome.ToString()).And.Contain("it broke");
    }

    [Fact]
    public async Task A_run_of_failures_logs_one_warning_and_a_success_ends_the_episode()
    {
        ReleaseNoticeEngine engine = Engine();
        answer = LatestReleaseResult.Failed(LatestReleaseOutcome.GhFailed, Channel, "gh release view", "signed out");
        for (int check = 0; check < 5; check++)
        {
            await engine.CheckOnceAsync(NodeId, CancellationToken.None);
        }

        logger.Entries.Should().ContainSingle("one warning for the whole episode, not one per check");

        answer = LatestReleaseResult.Found(Channel, "v0.13.0");
        await engine.CheckOnceAsync(NodeId, CancellationToken.None);
        answer = LatestReleaseResult.Failed(LatestReleaseOutcome.TimedOut, Channel, "gh release view", "too slow");
        await engine.CheckOnceAsync(NodeId, CancellationToken.None);
        await engine.CheckOnceAsync(NodeId, CancellationToken.None);

        logger.Entries.Where(entry => entry.Level == LogLevel.Warning).Should().HaveCount(2, "a new episode warns once more");
    }

    [Fact]
    public async Task A_release_still_uploading_also_ends_a_failure_episode_because_gh_answered()
    {
        ReleaseNoticeEngine engine = Engine();
        answer = LatestReleaseResult.Failed(LatestReleaseOutcome.GhFailed, Channel, "gh release view", "signed out");
        await engine.CheckOnceAsync(NodeId, CancellationToken.None);

        answer = LatestReleaseResult.NotYetComplete(Channel, "v0.13.1", ["checksums.txt"]);
        await engine.CheckOnceAsync(NodeId, CancellationToken.None);
        answer = LatestReleaseResult.Failed(LatestReleaseOutcome.GhFailed, Channel, "gh release view", "signed out again");
        await engine.CheckOnceAsync(NodeId, CancellationToken.None);

        logger.Entries.Should().HaveCount(2);
    }

    private async Task AdvanceAsync(TimeSpan by)
    {
        // The manual clock moves by firing a timer, the one way it ever moves.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task delay = Task.Delay(by, clock, cts.Token);
        await clock.FireNextAsync(cts.Token);
        await delay;
    }

    /// <summary>The node's own stream as the engine sees it: events appended, state read back from the last one.</summary>
    private sealed class FakeNodeStream : IReleaseNoticeStore
    {
        public List<ReleaseNoticeIssued> Events { get; } = [];

        public Task<ReleaseNoticeIssued?> RecordIfDueAsync(
            Guid nodeId, Func<ReleaseNoticeState, ReleaseNoticeIssued?> decide, CancellationToken cancellationToken)
        {
            ReleaseNoticeIssued? last = Events.LastOrDefault();
            ReleaseNoticeIssued? notice = decide(new ReleaseNoticeState(last?.Tag, last?.ObservedAt));
            if (notice is not null)
            {
                Events.Add(notice);
            }

            return Task.FromResult(notice);
        }
    }
}
