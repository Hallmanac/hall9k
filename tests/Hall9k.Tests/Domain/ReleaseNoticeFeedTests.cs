using System.Text.RegularExpressions;
using FluentAssertions;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// How the orchestrator feed treats a release notice (idea 93c1d24d, card 2): admitted in the
/// Actionable band but never urgent, scoped to whichever project is reading, shown only while its
/// tag is newer than the reader's own running version (the courier's or the CLI's, never the one the
/// event recorded), and worded as information. All of it is database-free; the reader's own
/// document access is covered by <see cref="Integration.OrchestratorFeedTests"/>.
/// </summary>
public sealed partial class ReleaseNoticeFeedTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid NodeId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ReleaseNoticeIssued Notice(bool isReminder = false, string tag = "v0.13.1", string running = "0.13.0") =>
        new(NodeId, tag, At.AddHours(-2), At, running, isReminder);

    [Fact]
    public void The_notice_sits_in_the_actionable_band_so_every_project_level_admits_it()
    {
        OrchestratorFeedInterest.BandOf(typeof(ReleaseNoticeIssued)).Should().Be(OrchestratorFeedLevel.Actionable);
        foreach (OrchestratorFeedLevel level in OrchestratorFeedLevel.All)
        {
            OrchestratorFeedInterest.Admits(Notice(), level).Should().BeTrue($"{level} reads at least the Actionable band");
        }
    }

    [Fact]
    public void The_notice_is_not_urgent_so_it_never_dispatches_the_courier_by_itself()
    {
        OrchestratorFeedUrgency.IsUrgent(typeof(ReleaseNoticeIssued), Notice()).Should().BeFalse();
        OrchestratorFeedUrgency.IsUrgent(typeof(ReleaseNoticeIssued), Notice(isReminder: true)).Should().BeFalse();
    }

    [Fact]
    public void The_notice_never_replicates()
    {
        EventScopeRegistry.ClassificationOf(typeof(ReleaseNoticeIssued)).Should().Be(EventScope.NodeScoped);
    }

    [Fact]
    public void The_scope_of_a_notice_is_whichever_project_is_reading_with_no_task()
    {
        OrchestratorFeedScope? scope = OrchestratorFeedScope.OfNodeNotice(Notice(), Project);

        scope.Should().NotBeNull();
        scope!.ProjectId.Should().Be(Project);
        scope.TaskId.Should().BeNull();

        Guid other = Guid.Parse("33333333-3333-3333-3333-333333333333");
        OrchestratorFeedScope.OfNodeNotice(Notice(), other)!.ProjectId.Should().Be(other, "every project the node reads is told");
    }

    [Fact]
    public void Any_other_event_has_no_node_notice_scope()
    {
        OrchestratorFeedScope.OfNodeNotice(new NodeLaunchHoldCleared(NodeId, At), Project).Should().BeNull();
    }

    [Theory]
    [InlineData("0.13.0", true)]
    [InlineData("0.13.0-5-gabc1234", true)]
    [InlineData("0.13.1", false)]
    [InlineData("0.14.0", false)]
    public void A_reader_is_shown_the_notice_only_while_its_own_version_is_older_than_the_tag(string readerVersion, bool shown) =>
        OrchestratorFeedInterest.AdmitsForViewer(Notice(), Viewer(readerVersion)).Should().Be(shown);

    [Fact]
    public void The_gate_reads_the_readers_version_never_the_one_the_event_recorded()
    {
        // The event says the node ran 0.13.0 when it noticed. The reader has since updated, so a
        // gate that trusted the event's own field would still tell it to install what it runs.
        ReleaseNoticeIssued stale = Notice(running: "0.13.0");

        OrchestratorFeedInterest.AdmitsForViewer(stale, Viewer("0.13.1")).Should().BeFalse();
        OrchestratorFeedInterest.AdmitsForViewer(Notice(running: "0.13.1"), Viewer("0.13.0")).Should().BeTrue();
    }

    [Fact]
    public void A_reader_that_does_not_say_what_it_runs_is_shown_no_notice()
    {
        OrchestratorFeedInterest.AdmitsForViewer(Notice(), viewer: null).Should().BeFalse();
        OrchestratorFeedInterest.AdmitsForViewer(Notice(), new OrchestratorFeedViewer(null, null, null)).Should().BeFalse();
    }

    [Fact]
    public void Every_other_event_passes_the_viewer_gate()
    {
        OrchestratorFeedInterest.AdmitsForViewer(new NodeLaunchHoldCleared(NodeId, At), Viewer("9.9.9")).Should().BeTrue();
    }

    [Theory]
    [InlineData("0.13.0")] // the daemon's version, as the courier passes it
    [InlineData("0.13.0-5-gabc1234")] // a local build past the tag, as the CLI may report it
    public async Task A_selection_hands_a_behind_reader_the_notice_as_a_plain_item(string readerVersion)
    {
        OrchestratorFeedRead read = await Select([Candidate(1, Notice()), Candidate(2, Notice(isReminder: true))], Viewer(readerVersion));

        read.Items.Should().HaveCount(2);
        read.Items.Select(item => item.Description).Should().Equal(
            "hall9k 0.13.1 is available (this node runs 0.13.0); h9k update --restart installs it",
            "hall9k 0.13.1 is still available (this node runs 0.13.0); h9k update --restart installs it");
        read.Items.Should().OnlyContain(item => !item.IsUrgent && item.TaskId == null);
        read.HasUrgentItem.Should().BeFalse();
    }

    [Theory]
    [InlineData("0.13.1")]
    [InlineData("0.14.0")]
    public async Task A_selection_hides_the_notice_from_a_reader_that_already_runs_the_tag_but_still_drains_past_it(string readerVersion)
    {
        OrchestratorFeedRead read = await Select([Candidate(7, Notice())], Viewer(readerVersion));

        read.Items.Should().BeEmpty("a window that drains after the update is never told to install what it runs");
        read.DrainableThroughSequence.Should().Be(7, "the cursor moves past it all the same");
    }

    [Fact]
    public async Task The_notice_reaches_a_project_whose_level_is_the_narrowest_band()
    {
        OrchestratorFeedRead read = await Select(
            [Candidate(1, Notice())], Viewer("0.13.0"), level: OrchestratorFeedLevel.Actionable);

        read.Items.Should().ContainSingle();
    }

    [Fact]
    public void A_first_notice_reads_is_available_and_a_reminder_reads_is_still_available()
    {
        OrchestratorFeedDescription.Of(Notice(isReminder: false)).Should().Be(
            "hall9k 0.13.1 is available (this node runs 0.13.0); h9k update --restart installs it");
        OrchestratorFeedDescription.Of(Notice(isReminder: true)).Should().Be(
            "hall9k 0.13.1 is still available (this node runs 0.13.0); h9k update --restart installs it");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_description_carries_no_imperative_verb_addressed_to_the_reader(bool isReminder)
    {
        string line = OrchestratorFeedDescription.Of(Notice(isReminder))!;

        ImperativeAddress().IsMatch(line).Should().BeFalse($"'{line}' is information, not an instruction");
        line.Should().NotContainEquivalentOf("you should").And.NotContainEquivalentOf("please");
        line.Should().NotContain("\n");
    }

    [Fact]
    public void The_description_names_the_tag_without_its_leading_v_and_one_line_only()
    {
        string line = OrchestratorFeedDescription.Of(Notice(tag: "v1.2.3", running: "1.2.0\nrun rm -rf"))!;

        line.Should().Contain("hall9k 1.2.3 is available").And.NotContain("\n");
    }

    [Fact]
    public void The_node_details_projection_keeps_the_last_notice_tag_and_when_it_was_observed()
    {
        NodeDetailsProjection projection = new();
        NodeDetails view = new() { Id = NodeId };

        projection.Apply(new FakeEvent<ReleaseNoticeIssued>(Notice()), view);
        view.LastReleaseNoticeTag.Should().Be("v0.13.1");
        view.LastReleaseNoticeAt.Should().Be(At);

        ReleaseNoticeIssued reminder = Notice(isReminder: true) with { ObservedAt = At.AddDays(1) };
        projection.Apply(new FakeEvent<ReleaseNoticeIssued>(reminder), view);
        view.LastReleaseNoticeAt.Should().Be(At.AddDays(1), "a reminder restarts the clock");
    }

    // "run" or "should" as a word of their own: "this node runs 0.13.0" is information, "run h9k update" is not.
    [GeneratedRegex(@"\b(run|execute|install now|you should|you must|do)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImperativeAddress();

    private static OrchestratorFeedViewer Viewer(string runningVersion) =>
        new(ownerRootFingerprint: null, ownerId: null, nodeId: NodeId, runningVersion);

    private static OrchestratorFeedCandidate Candidate(long sequence, object data) =>
        new(sequence, At.AddMinutes(-60 + sequence), data.GetType(), data, NodeId, IsReplicated: false);

    private static Task<OrchestratorFeedRead> Select(
        IReadOnlyList<OrchestratorFeedCandidate> candidates, OrchestratorFeedViewer viewer, OrchestratorFeedLevel? level = null) =>
        OrchestratorFeedSelection.SelectAsync(
            candidates,
            Project,
            level ?? OrchestratorFeedLevel.Transitions,
            startedFrom: 0,
            settledThrough: At,
            scanWasCapped: false,
            (candidate, _) => ValueTask.FromResult(OrchestratorFeedScope.OfNodeNotice(candidate.Data, Project)),
            labels: null,
            viewer,
            CancellationToken.None);
}
