using FluentAssertions;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The feed's cursor behaviour (idea 89471598, piece 2), database-free: what one read yields,
/// how far it says it got, and what a drain does with that — including the property the whole
/// design rests on, that a drain never moves past an event a lower, still-uncommitted sequence
/// could be hiding behind. The other half, that a read without a drain is repeatable and that a
/// drain makes the same read come back empty, is the store's, pinned against the real seam in
/// <see cref="Integration.OrchestratorFeedTests"/>.
/// </summary>
public sealed class OrchestratorFeedSelectionTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 19, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TaskId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherProject = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task A_read_reports_the_highest_sequence_it_inspected_including_what_it_rejected()
    {
        // Sequence 7 is a type the feed has no interest in. The cursor must still move past it:
        // otherwise every later sweep re-reads and re-classifies it forever.
        OrchestratorFeedCandidate[] candidates =
        [
            Candidate(5, new TaskPublished(TaskId, At, Guid.NewGuid())),
            Candidate(7, new TokensRecorded(TaskId, 1_000, 200, null, At)),
        ];

        OrchestratorFeedRead read = await Read(candidates, OrchestratorFeedLevel.Transitions, startedFrom: 0);

        read.Items.Should().ContainSingle();
        read.DrainableThroughSequence.Should().Be(7);
    }

    [Fact]
    public async Task A_read_that_inspects_nothing_leaves_the_cursor_exactly_where_it_was()
    {
        OrchestratorFeedRead read = await Read([], OrchestratorFeedLevel.Transitions, startedFrom: 42);

        read.Items.Should().BeEmpty();
        read.DrainableThroughSequence.Should().Be(42, "a drain on an empty read must not rewind the cursor");
    }

    /// <summary>
    /// Task: a run stream whose first event is a reconstruction rather than a dispatch. A peer's
    /// own daemon rebuilt nothing, so a replicated copy of a teammate's reconstruction raises no
    /// item here — but this node's own reconstruction still does, at the Actionable band it always
    /// has.
    /// </summary>
    [Fact]
    public async Task A_replicated_reconstruction_raises_no_item_but_a_local_one_does()
    {
        object reconstructed = new RunRecordReconstructed(
            Guid.NewGuid(), TaskId, Guid.NewGuid(), Guid.NewGuid(), null, null, At);
        OrchestratorFeedCandidate[] candidates =
        [
            Candidate(5, reconstructed, isReplicated: true),
            Candidate(6, reconstructed, isReplicated: false),
        ];

        OrchestratorFeedRead read = await Read(candidates, OrchestratorFeedLevel.Actionable, startedFrom: 0);

        read.Items.Should().ContainSingle().Which.Sequence.Should().Be(6);
    }

    [Fact]
    public async Task An_event_too_new_to_have_settled_is_shown_but_not_drained_past()
    {
        // Sequence 6 was written inside the settling window. A global sequence is taken inside
        // the writing transaction, so a lower one can still be uncommitted behind it; draining
        // past 6 would leave that write invisible to this feed for good.
        OrchestratorFeedCandidate[] candidates =
        [
            Candidate(5, new TaskPublished(TaskId, At, Guid.NewGuid()), at: At),
            Candidate(6, new RunFailed(TaskId, "the gate never finished", At), at: At.AddSeconds(2)),
        ];

        OrchestratorFeedRead read = await OrchestratorFeedSelection.SelectAsync(
            candidates,
            Project,
            OrchestratorFeedLevel.Transitions,
            startedFrom: 0,
            settledThrough: At.AddSeconds(1),
            scanWasCapped: false,
            (_, _) => ValueTask.FromResult<OrchestratorFeedScope?>(new OrchestratorFeedScope(Project, TaskId)),
            CancellationToken.None);

        read.Items.Should().HaveCount(2, "an item too new to drain past is still news worth printing");
        read.DrainableThroughSequence.Should().Be(5);
    }

    [Fact]
    public async Task The_drain_frontier_stops_at_the_first_unsettled_event_rather_than_stepping_over_it()
    {
        // 6 is unsettled and 7, stamped a moment earlier, is not: an uncommitted write sits
        // between sequences rather than after them, so the frontier stops at 5 rather than
        // skipping to 7 and stranding whatever is still in flight below it.
        OrchestratorFeedCandidate[] candidates =
        [
            Candidate(5, new TaskPublished(TaskId, At, Guid.NewGuid()), at: At),
            Candidate(6, new RunFailed(TaskId, "the gate never finished", At), at: At.AddSeconds(2)),
            Candidate(7, new RunFailed(TaskId, "and again", At), at: At.AddSeconds(-5)),
        ];

        OrchestratorFeedRead read = await OrchestratorFeedSelection.SelectAsync(
            candidates,
            Project,
            OrchestratorFeedLevel.Transitions,
            startedFrom: 0,
            settledThrough: At.AddSeconds(1),
            scanWasCapped: false,
            (_, _) => ValueTask.FromResult<OrchestratorFeedScope?>(new OrchestratorFeedScope(Project, TaskId)),
            CancellationToken.None);

        read.DrainableThroughSequence.Should().Be(5);
    }

    [Fact]
    public void A_drain_that_would_move_the_cursor_backwards_writes_nothing()
    {
        OrchestratorFeedCursor ahead = new()
        {
            Id = Project,
            LastDrainedGlobalSequence = 90,
            LastDrainedAt = At,
        };

        OrchestratorFeedCursor.Advanced(ahead, Project, 40, At.AddMinutes(1)).Should().BeNull();
        OrchestratorFeedCursor.Advanced(ahead, Project, 90, At.AddMinutes(1)).Should().BeNull();
        OrchestratorFeedCursor.Advanced(ahead, Project, 91, At.AddMinutes(1))!.LastDrainedGlobalSequence
            .Should().Be(91);
    }

    [Fact]
    public void A_first_drain_writes_the_cursor_under_the_projects_own_id()
    {
        OrchestratorFeedCursor first = OrchestratorFeedCursor.Advanced(null, Project, 12, At)!;

        first.Id.Should().Be(Project);
        first.LastDrainedGlobalSequence.Should().Be(12);
        first.LastDrainedAt.Should().Be(At);
    }

    [Fact]
    public async Task Another_projects_events_are_never_this_projects_items()
    {
        OrchestratorFeedRead read = await OrchestratorFeedSelection.SelectAsync(
            [Candidate(5, new TaskPublished(TaskId, At, Guid.NewGuid()))],
            Project,
            OrchestratorFeedLevel.Transitions,
            startedFrom: 0,
            settledThrough: Settled,
            scanWasCapped: false,
            (_, _) => ValueTask.FromResult<OrchestratorFeedScope?>(new OrchestratorFeedScope(OtherProject, TaskId)),
            CancellationToken.None);

        read.Items.Should().BeEmpty();
        read.DrainableThroughSequence.Should().Be(5, "the cursor still moves past what this project ignored");
    }

    [Fact]
    public async Task An_event_belonging_to_no_project_this_node_can_see_is_skipped()
    {
        OrchestratorFeedRead read = await OrchestratorFeedSelection.SelectAsync(
            [Candidate(5, new TaskPublished(TaskId, At, Guid.NewGuid()))],
            Project,
            OrchestratorFeedLevel.Transitions,
            startedFrom: 0,
            settledThrough: Settled,
            scanWasCapped: false,
            (_, _) => ValueTask.FromResult<OrchestratorFeedScope?>(null),
            CancellationToken.None);

        read.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task The_scope_lookup_is_never_asked_about_an_event_the_filter_rejected()
    {
        // Both ways an event is rejected: 6 is a type the table does not name at all, and 7 is a
        // named type the payload gate turns down — the daemon's own claim traffic, which is JSON
        // for a reactor rather than a message from a person. Scoping an event means reading
        // documents, so neither may reach the lookup.
        List<long> asked = [];
        MessageReceived claimRequest = new(
            Guid.NewGuid(), 1, At, "abcdef0123456789", "project", null,
            MessageKind.ClaimRequest.Value, "{}", At, Project);

        OrchestratorFeedRead read = await OrchestratorFeedSelection.SelectAsync(
            [
                Candidate(5, new TaskPublished(TaskId, At, Guid.NewGuid())),
                Candidate(6, new TokensRecorded(TaskId, 1_000, 200, null, At)),
                Candidate(7, claimRequest),
            ],
            Project,
            OrchestratorFeedLevel.Transitions,
            startedFrom: 0,
            settledThrough: Settled,
            scanWasCapped: false,
            (candidate, _) =>
            {
                asked.Add(candidate.Sequence);
                return ValueTask.FromResult<OrchestratorFeedScope?>(new OrchestratorFeedScope(Project, TaskId));
            },
            CancellationToken.None);

        asked.Should().Equal(5);
        read.Items.Should().ContainSingle();
    }

    /// <summary>Long enough after every candidate below that the settling window is out of the
    /// way: the tests that are about the window say so by passing their own instant.</summary>
    private static readonly DateTimeOffset Settled = At.AddDays(1);

    private static OrchestratorFeedCandidate Candidate(
        long sequence, object data, DateTimeOffset? at = null, bool isReplicated = false) =>
        new(sequence, at ?? At.AddMinutes(sequence), data.GetType(), data, TaskId, isReplicated);

    private static Task<OrchestratorFeedRead> Read(
        IReadOnlyList<OrchestratorFeedCandidate> candidates, OrchestratorFeedLevel level, long startedFrom) =>
        OrchestratorFeedSelection.SelectAsync(
            candidates,
            Project,
            level,
            startedFrom,
            settledThrough: Settled,
            scanWasCapped: false,
            (_, _) => ValueTask.FromResult<OrchestratorFeedScope?>(new OrchestratorFeedScope(Project, TaskId)),
            CancellationToken.None);
}
