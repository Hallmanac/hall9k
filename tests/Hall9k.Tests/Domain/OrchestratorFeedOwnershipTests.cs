using FluentAssertions;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// What the orchestrator feed does about whose task an event is about (task: an orchestrator's feed
/// carries a teammate's activity only when it concerns that orchestrator's owner). The facts a scope
/// carries are read off a board row the way the viewer's board reads them
/// (<see cref="TaskListItemOwnerFacts"/>), so each case names a row shape the board already tests:
/// the viewer's own, a fleet sibling's, and a teammate's held, assigned and creator-only tasks, plus
/// an owner this node cannot resolve. Database-free; the reader that supplies the facts is covered
/// against the real store in the integration tier.
/// </summary>
public sealed class OrchestratorFeedOwnershipTests
{
    private const string Mine = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Theirs = "2222222222222222222222222222222222222222222222222222222222222222";
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Settled = At.AddDays(1);
    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TaskId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ThisNode = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ThisOwner = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid SiblingOwner = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid TeammateOwner = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid TeammateNode = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid UnknownOwner = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    private static readonly OrchestratorFeedViewer Viewer = new(Mine, ThisOwner, ThisNode);

    private static string? OwnerRoot(Guid ownerId) => ownerId == SiblingOwner
        ? Mine
        : ownerId == TeammateOwner
            ? Theirs
            : null;

    private static TaskOwnerFacts Own() => Facts(new TaskListItem { AssignedOwnerFingerprint = Mine });

    private static TaskOwnerFacts Facts(TaskListItem row, OwnerRootFact? creator = null) =>
        TaskListItemOwnerFacts.From(row, OwnerRoot, creator);

    /// <summary>The board shapes the card names, each with whether the viewer's root may act on it.</summary>
    public static TheoryData<string, TaskOwnerFacts, bool> Board => new()
    {
        { "the viewer's own task", Own(), true },
        {
            "a fleet sibling's task, by a second owner id on the same root",
            Facts(new TaskListItem { AssignedOwnerId = SiblingOwner }),
            true
        },
        { "a task the viewer holds", Facts(new TaskListItem { HolderOwnerRootFingerprint = Mine }), true },
        {
            "a task whose only fact is the viewer as creator",
            Facts(new TaskListItem(), OwnerRootFact.Known(Mine)),
            true
        },
        { "a teammate's held task", Facts(new TaskListItem { HolderOwnerRootFingerprint = Theirs }), false },
        { "a teammate's assigned task", Facts(new TaskListItem { AssignedOwnerFingerprint = Theirs }), false },
        {
            "a teammate's assigned task known only by owner id",
            Facts(new TaskListItem { AssignedOwnerId = TeammateOwner }),
            false
        },
        {
            "a teammate's creator-only task",
            Facts(new TaskListItem(), OwnerRootFact.Known(Theirs)),
            false
        },
        {
            "a task whose creator cannot be resolved",
            Facts(new TaskListItem(), OwnerRootFact.Unresolved),
            false
        },
        {
            "a task assigned to an owner this node has never heard of",
            Facts(new TaskListItem { AssignedOwnerId = UnknownOwner }),
            false
        },
    };

    [Theory]
    [MemberData(nameof(Board))]
    public async Task A_replicated_item_is_kept_only_for_a_task_the_viewers_root_may_act_on_at_every_band(
        string board, TaskOwnerFacts facts, bool viewers)
    {
        object[] onePerBand =
        [
            new RunFailed(TaskId, "the gate never finished", At),
            new TaskPublished(TaskId, At, Guid.NewGuid()),
            new RunResumed(TaskId, 1, At, At),
        ];

        foreach (object data in onePerBand)
        {
            OrchestratorFeedRead read = await Read(
                [Candidate(5, data, isReplicated: true)], OrchestratorFeedLevel.Everything, ScopeWith(facts));

            read.Items.Should().HaveCount(viewers ? 1 : 0, $"{board}, {data.GetType().Name}");
        }
    }

    [Fact]
    public async Task A_dropped_item_still_lets_the_cursor_advance_past_it()
    {
        OrchestratorFeedRead read = await Read(
            [
                Candidate(5, new RunFailed(TaskId, "a teammate's failure", At), isReplicated: true),
                Candidate(9, new TaskPublished(TaskId, At, Guid.NewGuid()), isReplicated: true),
            ],
            OrchestratorFeedLevel.Everything,
            ScopeWith(Facts(new TaskListItem { HolderOwnerRootFingerprint = Theirs })));

        read.Items.Should().BeEmpty();
        read.DrainableThroughSequence.Should().Be(9);
    }

    [Fact]
    public async Task A_local_item_on_a_teammates_task_is_unchanged_because_it_is_this_nodes_own_work()
    {
        OrchestratorFeedRead read = await Read(
            [Candidate(5, new RunFailed(TaskId, "this node's own run", At), isReplicated: false)],
            OrchestratorFeedLevel.Actionable,
            ScopeWith(Facts(new TaskListItem { HolderOwnerRootFingerprint = Theirs })));

        read.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Items_with_no_task_and_a_message_about_a_teammates_task_are_unchanged()
    {
        MessageReceived message = new(
            TeammateNode, 1, At, Theirs, "project", TaskId.ToString(), MessageKind.Note.Value, "hello", At, Project);
        RootRotationObserved rotation = new(Project, Theirs, TeammateNode, At);
        IdeaCaptured idea = new(Guid.NewGuid(), TeammateOwner, "a thought", Project, At);
        TaskOwnerFacts teammates = Facts(new TaskListItem { HolderOwnerRootFingerprint = Theirs });

        // Messages and rotations are scoped with no ownership facts (the reader supplies none), and an
        // idea resolves to a project with no task, so none of them is a task this rule could judge.
        OrchestratorFeedRead withoutFacts = await Read(
            [
                Candidate(5, message, isReplicated: true),
                Candidate(6, rotation, isReplicated: true),
            ],
            OrchestratorFeedLevel.Actionable,
            _ => new OrchestratorFeedScope(Project, TaskId));
        OrchestratorFeedRead withoutATask = await Read(
            [Candidate(7, idea, isReplicated: true)],
            OrchestratorFeedLevel.Transitions,
            _ => new OrchestratorFeedScope(Project, null, teammates));

        withoutFacts.Items.Should().HaveCount(2);
        withoutATask.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task A_take_request_from_the_viewers_root_is_kept_on_a_teammates_task_and_one_from_anyone_else_is_not()
    {
        TaskOwnerFacts teammates = Facts(new TaskListItem { HolderOwnerRootFingerprint = Theirs });

        OrchestratorFeedRead read = await Read(
            [
                Candidate(5, new TaskTakeRequested(TaskId, ThisNode, ThisOwner, Mine, "I need it", At), isReplicated: true),
                Candidate(6, new TaskTakeRequested(TaskId, TeammateNode, TeammateOwner, Theirs, "me too", At), isReplicated: true),
            ],
            OrchestratorFeedLevel.Transitions,
            ScopeWith(teammates));

        read.Items.Should().ContainSingle().Which.Sequence.Should().Be(5);
    }

    [Fact]
    public async Task A_take_refusal_naming_this_node_or_this_owner_is_kept_and_one_naming_neither_is_not()
    {
        TaskOwnerFacts teammates = Facts(new TaskListItem { HolderOwnerRootFingerprint = Theirs });

        OrchestratorFeedRead read = await Read(
            [
                Candidate(5, new TaskTakeRefused(TaskId, ThisNode, TeammateOwner, "a run is live", At), isReplicated: true),
                Candidate(6, new TaskTakeRefused(TaskId, TeammateNode, ThisOwner, "a run is live", At), isReplicated: true),
                Candidate(7, new TaskTakeRefused(TaskId, TeammateNode, TeammateOwner, "a run is live", At), isReplicated: true),
            ],
            OrchestratorFeedLevel.Transitions,
            ScopeWith(teammates));

        read.Items.Select(item => item.Sequence).Should().Equal(5, 6);
    }

    [Fact]
    public async Task A_holder_release_granted_to_the_viewers_root_is_kept_and_one_granted_elsewhere_or_to_nobody_is_not()
    {
        TaskOwnerFacts teammates = Facts(new TaskListItem { HolderOwnerRootFingerprint = Theirs });

        OrchestratorFeedRead read = await Read(
            [
                Candidate(5, new TaskHolderReleased(TaskId, At, ThisNode, ThisOwner, Mine), isReplicated: true),
                Candidate(6, new TaskHolderReleased(TaskId, At, TeammateNode, TeammateOwner, Theirs), isReplicated: true),
                Candidate(7, new TaskHolderReleased(TaskId, At), isReplicated: true),
            ],
            OrchestratorFeedLevel.Transitions,
            ScopeWith(teammates));

        read.Items.Should().ContainSingle().Which.Sequence.Should().Be(5);
    }

    [Fact]
    public async Task A_forced_takeover_of_a_task_this_node_held_is_kept_and_one_from_another_holder_is_not()
    {
        // The takeover has already moved the board row to the taker by the time the feed reads it.
        TaskOwnerFacts taken = Facts(new TaskListItem { HolderOwnerRootFingerprint = Theirs });

        OrchestratorFeedRead read = await Read(
            [
                Candidate(5, new TaskHolderTakenOver(TaskId, ThisNode, TeammateNode, TeammateOwner, Theirs, "away", TeammateOwner, At), isReplicated: true),
                Candidate(6, new TaskHolderTakenOver(TaskId, Guid.NewGuid(), TeammateNode, TeammateOwner, Theirs, "away", TeammateOwner, At), isReplicated: true),
                Candidate(7, new TaskHolderTakenOver(TaskId, null, TeammateNode, TeammateOwner, Theirs, "away", TeammateOwner, At), isReplicated: true),
            ],
            OrchestratorFeedLevel.Transitions,
            ScopeWith(taken));

        read.Items.Should().ContainSingle().Which.Sequence.Should().Be(5);
    }

    [Fact]
    public async Task A_release_recorded_by_one_of_the_viewers_own_nodes_that_granted_the_task_away_is_kept()
    {
        TaskOwnerFacts granted = Facts(new TaskListItem { HolderOwnerRootFingerprint = Theirs });

        OrchestratorFeedRead read = await Read(
            [
                Candidate(5, new TaskHolderReleased(TaskId, At, TeammateNode, TeammateOwner, Theirs), isReplicated: true, origin: Mine),
                Candidate(6, new TaskHolderReleased(TaskId, At), isReplicated: true, origin: Mine),
                Candidate(7, new TaskHolderReleased(TaskId, At, TeammateNode, TeammateOwner, Theirs), isReplicated: true, origin: Theirs),
            ],
            OrchestratorFeedLevel.Transitions,
            ScopeWith(granted));

        read.Items.Should().ContainSingle().Which.Sequence.Should().Be(5);
    }

    [Fact]
    public async Task An_end_of_a_teammates_task_the_viewers_root_has_a_take_request_pending_on_is_kept_as_the_board_keeps_it()
    {
        TaskOwnerFacts teammates = Facts(new TaskListItem { HolderOwnerRootFingerprint = Theirs });
        OrchestratorFeedCandidate end = Candidate(
            5, new TaskAbandoned(TaskId, "gone", At, TeammateOwner), isReplicated: true, origin: Theirs);

        OrchestratorFeedRead waiting = await Read(
            [end], OrchestratorFeedLevel.Transitions, _ => new OrchestratorFeedScope(Project, TaskId, teammates, Mine));
        OrchestratorFeedRead someoneElsesRequest = await Read(
            [end], OrchestratorFeedLevel.Transitions, _ => new OrchestratorFeedScope(Project, TaskId, teammates, Theirs));

        waiting.Items.Should().ContainSingle();
        someoneElsesRequest.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("actionable")]
    [InlineData("transitions")]
    [InlineData("everything")]
    public async Task Another_root_abandoning_the_viewers_task_is_an_urgent_item_at_every_level_naming_who_and_why(string level)
    {
        TaskAbandoned abandoned = new(TaskId, "superseded by the rewrite", At, TeammateOwner);

        OrchestratorFeedRead read = await Read(
            [Candidate(5, abandoned, isReplicated: true, origin: Theirs)],
            OrchestratorFeedLevel.FromInput(level),
            ScopeWith(Own()),
            Labelled("Ryan"));

        OrchestratorFeedItem item = read.Items.Should().ContainSingle().Subject;
        item.IsUrgent.Should().BeTrue();
        item.Description.Should().Be("Ryan abandoned this task: superseded by the rewrite");
        read.HasUrgentItem.Should().BeTrue();
    }

    [Fact]
    public async Task Another_root_resolving_the_viewers_task_is_urgent_and_says_so_when_it_gave_no_reason()
    {
        TaskResolved resolved = new(TaskId, string.Empty, null, At, TeammateOwner);

        OrchestratorFeedRead read = await Read(
            [Candidate(5, resolved, isReplicated: true, origin: Theirs)],
            OrchestratorFeedLevel.Actionable,
            ScopeWith(Own()),
            Labelled("Ryan"));

        OrchestratorFeedItem item = read.Items.Should().ContainSingle().Subject;
        item.IsUrgent.Should().BeTrue();
        item.Description.Should().Be("Ryan closed this task as done by hand; no reason was recorded");
    }

    [Fact]
    public async Task An_end_from_an_unlabelled_root_names_it_by_its_short_fingerprint()
    {
        OrchestratorFeedRead read = await Read(
            [Candidate(5, new TaskAbandoned(TaskId, "no longer needed", At, TeammateOwner), isReplicated: true, origin: Theirs)],
            OrchestratorFeedLevel.Actionable,
            ScopeWith(Own()));

        read.Items.Should().ContainSingle().Which.Description
            .Should().Be($"{Theirs[..12]} abandoned this task: no longer needed");
    }

    [Fact]
    public async Task The_same_end_from_one_of_the_owners_own_fleet_nodes_is_not_actionable()
    {
        // Same root, different owner id: the sibling's own act on the viewer's own task.
        TaskAbandoned byFleetSibling = new(TaskId, "done with it", At, SiblingOwner);
        OrchestratorFeedCandidate[] candidates = [Candidate(5, byFleetSibling, isReplicated: true, origin: Mine)];

        OrchestratorFeedRead atActionable = await Read(candidates, OrchestratorFeedLevel.Actionable, ScopeWith(Own()));
        OrchestratorFeedRead atTransitions = await Read(candidates, OrchestratorFeedLevel.Transitions, ScopeWith(Own()));

        atActionable.Items.Should().BeEmpty("a window at the Actionable level is not told of its own fleet's act");
        OrchestratorFeedItem line = atTransitions.Items.Should().ContainSingle().Subject;
        line.IsUrgent.Should().BeFalse("the fleet's own end is still the Transitions line it always was");
        line.Description.Should().Be("abandoned: done with it");
    }

    [Fact]
    public async Task An_end_with_no_recorded_origin_root_or_recorded_locally_is_not_another_roots()
    {
        TaskAbandoned abandoned = new(TaskId, "gone", At, TeammateOwner);

        OrchestratorFeedRead noOrigin = await Read(
            [Candidate(5, abandoned, isReplicated: true, origin: null)], OrchestratorFeedLevel.Actionable, ScopeWith(Own()));
        OrchestratorFeedRead local = await Read(
            [Candidate(6, abandoned, isReplicated: false, origin: Theirs)], OrchestratorFeedLevel.Actionable, ScopeWith(Own()));

        noOrigin.Items.Should().BeEmpty();
        local.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_root_ending_a_teammates_task_is_nothing_to_this_viewer_at_any_level()
    {
        TaskOwnerFacts teammates = Facts(new TaskListItem { HolderOwnerRootFingerprint = Theirs });
        OrchestratorFeedCandidate[] candidates =
        [
            Candidate(5, new TaskAbandoned(TaskId, "gone", At, TeammateOwner), isReplicated: true, origin: Theirs),
        ];

        foreach (OrchestratorFeedLevel level in OrchestratorFeedLevel.All)
        {
            OrchestratorFeedRead read = await Read(candidates, level, ScopeWith(teammates));

            read.Items.Should().BeEmpty(level.Value);
        }
    }

    [Fact]
    public async Task An_end_is_never_paged_for_a_viewer_with_no_root_to_compare()
    {
        OrchestratorFeedRead read = await OrchestratorFeedSelection.SelectAsync(
            [Candidate(5, new TaskAbandoned(TaskId, "gone", At, TeammateOwner), isReplicated: true, origin: Theirs)],
            Project,
            OrchestratorFeedLevel.Actionable,
            startedFrom: 0,
            settledThrough: Settled,
            scanWasCapped: false,
            (_, _) => ValueTask.FromResult<OrchestratorFeedScope?>(new OrchestratorFeedScope(Project, TaskId, Own())),
            labels: null,
            viewer: new OrchestratorFeedViewer(null, ThisOwner, ThisNode),
            CancellationToken.None);

        read.Items.Should().BeEmpty();
    }

    [Fact]
    public void Urgency_takes_the_selections_answer_for_an_end_and_never_makes_an_ordinary_one_urgent()
    {
        TaskAbandoned abandoned = new(TaskId, "gone", At, TeammateOwner);

        OrchestratorFeedUrgency.IsUrgent(typeof(TaskAbandoned), abandoned, endsViewersTask: true).Should().BeTrue();
        OrchestratorFeedUrgency.IsUrgent(typeof(TaskAbandoned), abandoned, endsViewersTask: false).Should().BeFalse();
        OrchestratorFeedUrgency.IsUrgent(typeof(TaskAbandoned), abandoned).Should().BeFalse();
        OrchestratorFeedUrgency.IsUrgent(typeof(TaskFailed), new TaskFailed(TaskId, Guid.NewGuid(), "x", At), endsViewersTask: true)
            .Should().BeFalse("only an end is made urgent by it");
    }

    private static MemberLabelLookup Labelled(string displayName) =>
        new(new ProjectMemberLabels
        {
            Id = Project,
            Labels = [new ProjectMemberLabel(Theirs, [TeammateNode], DisplayName.Parse(displayName), null)],
        });

    private static Func<OrchestratorFeedCandidate, OrchestratorFeedScope?> ScopeWith(TaskOwnerFacts facts) =>
        _ => new OrchestratorFeedScope(Project, TaskId, facts);

    private static OrchestratorFeedCandidate Candidate(
        long sequence, object data, bool isReplicated, string? origin = null) =>
        new(sequence, At.AddMinutes(sequence), data.GetType(), data, TaskId, isReplicated, origin);

    private static Task<OrchestratorFeedRead> Read(
        IReadOnlyList<OrchestratorFeedCandidate> candidates,
        OrchestratorFeedLevel level,
        Func<OrchestratorFeedCandidate, OrchestratorFeedScope?> scopeOf,
        MemberLabelLookup? labels = null) =>
        OrchestratorFeedSelection.SelectAsync(
            candidates,
            Project,
            level,
            startedFrom: 0,
            settledThrough: Settled,
            scanWasCapped: false,
            (candidate, _) => ValueTask.FromResult(scopeOf(candidate)),
            labels,
            Viewer,
            CancellationToken.None);
}
