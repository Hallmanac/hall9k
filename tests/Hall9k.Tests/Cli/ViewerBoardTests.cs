using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The board a viewer sees: their own work in the bands and counts, and every other owner's task
/// composed into a Teammates group that no other count includes (the 2026-10-01 incident, where a
/// teammate's pane put two review rows in its Needs you band, worded as its own, and offered
/// <c>h9k task abandon</c> as the exit). The ownership question is
/// <see cref="Hall9k.Domain.Features.Tasks.Handlers.TaskViewerRule"/>, which is the task commands'
/// own rule, so these tests state the board's answers for each shape of task and the integration
/// parity test proves the commands agree.
/// </summary>
public sealed class ViewerBoardTests
{
    private const string ViewerRoot = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TeammateRoot = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OtherTeammateRoot = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static readonly Guid ProjectId = DomainId.New();

    [Fact]
    public void A_board_of_every_shape_of_task_counts_only_the_viewers_own_and_holds_the_rest_apart()
    {
        Guid siblingOwnerId = DomainId.New();
        Guid runId = DomainId.New();
        RunDetails run = StatusFixtures.Run(runId, RunState.Running);

        TaskListItem own = Owned(TaskState.Queued, assignedRoot: ViewerRoot);
        // A fleet sibling: a second owner id on the same root, recorded by id alone, which this
        // node resolves because its own owner table holds that owner's root.
        TaskListItem sibling = Owned(TaskState.Queued, assignedOwnerId: siblingOwnerId);
        TaskListItem ownHeld = Owned(TaskState.Claimed, runId: runId);
        ownHeld.ClaimedByNodeId = run.NodeId;
        ownHeld.HolderOwnerRootFingerprint = ViewerRoot;
        ownHeld.AssignedOwnerFingerprint = TeammateRoot;

        TaskListItem teammateHeld = Owned(TaskState.Claimed, runId: runId);
        teammateHeld.HolderOwnerRootFingerprint = TeammateRoot;
        TaskListItem teammateAssigned = Owned(TaskState.Queued, assignedRoot: TeammateRoot);
        TaskListItem teammateCreatorOnly = Owned(TaskState.Published);
        TaskListItem unknownOwner = Owned(TaskState.Queued, assignedOwnerId: DomainId.New());

        TaskListItem[] board =
            [own, sibling, ownHeld, teammateHeld, teammateAssigned, teammateCreatorOnly, unknownOwner];
        TaskStatusContext context = Viewer(run, siblingOwnerId, silentSince: StatusFixtures.Now.AddHours(-3)) with
        {
            CreatorFacts = new Dictionary<Guid, OwnerRootFact> { [teammateCreatorOnly.Id] = OwnerRootFact.Known(TeammateRoot) },
        };
        TaskStatusRow[] rows = [.. board.Select(task => TaskStatusComposer.Compose(task, context, StatusFixtures.Now))];

        rows.Where(row => row.IsTeammates).Select(row => row.TaskId).Should().BeEquivalentTo(
            [teammateHeld.Id, teammateAssigned.Id, teammateCreatorOnly.Id, unknownOwner.Id]);
        rows.Where(row => !row.IsTeammates).Select(row => row.TaskId).Should().BeEquivalentTo(
            [own.Id, sibling.Id, ownHeld.Id]);

        (IReadOnlyList<TaskStatusRow> visible, int hidden) = TeammateRows.Apply(rows, everyone: false);
        hidden.Should().Be(4);
        TaskRollup mine = TaskRollup.From(visible);
        mine.Teammates.Should().Be(0);
        mine.Total.Should().Be(3);
        mine.Queued.Should().Be(2, "the viewer's own and the fleet sibling's, both Queued");
        // The run behind both held tasks went silent three hours ago: it is the viewer's own stall,
        // and the teammate's identical one is counted nowhere on this board.
        mine.Stalled.Should().Be(1);
        mine.Working.Should().Be(0);
        mine.NeedsYou.Should().Be(0);
        TeammateRows.HiddenNote(hidden, "h9k status").Should().Be(
            "4 tasks belonging to teammates are not shown: h9k status --everyone shows them");

        (IReadOnlyList<TaskStatusRow> everyone, int hiddenWhenAsked) = TeammateRows.Apply(rows, everyone: true);
        hiddenWhenAsked.Should().Be(0);
        TaskRollup all = TaskRollup.From(everyone);
        all.Teammates.Should().Be(4);
        all.Total.Should().Be(7, "the columns still sum to the tasks shown");
        all.Queued.Should().Be(2, "a teammate's queued task is not a queued count");
        TeammateRows.HiddenNote(0, "h9k status").Should().BeEmpty();
        TeammateRows.HiddenNote(1, "h9k status").Should().Be(
            "1 task belonging to a teammate is not shown: h9k status --everyone shows it");
    }

    [Fact]
    public void A_teammates_task_the_owner_would_see_as_stalled_or_needing_them_is_neither_for_the_viewer()
    {
        Guid runId = DomainId.New();
        RunDetails run = StatusFixtures.Run(runId, RunState.ReviewParked);
        TaskListItem parked = Owned(TaskState.NeedsHuman, runId: runId, pullRequest: "https://github.com/acme/widgets/pull/9");
        parked.HolderOwnerRootFingerprint = TeammateRoot;
        TaskListItem silent = Owned(TaskState.Claimed, runId: runId);
        silent.HolderOwnerRootFingerprint = TeammateRoot;
        RunDetails runningRun = StatusFixtures.Run(runId, RunState.Running);

        TaskStatusRow parkedRow = TaskStatusComposer.Compose(parked, Viewer(run), StatusFixtures.Now);
        TaskStatusRow silentRow = TaskStatusComposer.Compose(
            silent, Viewer(runningRun, silentSince: StatusFixtures.Now.AddHours(-5)), StatusFixtures.Now);

        parkedRow.Group.Should().Be(AttentionBucket.Teammates);
        silentRow.Group.Should().Be(AttentionBucket.Teammates);
        silentRow.Stalled.Should().BeFalse();
        parkedRow.Attention.NeedsYou.Should().BeFalse();
        TaskRollup.From([parkedRow, silentRow]).NeedsYou.Should().Be(0);
        TaskRollup.From([parkedRow, silentRow]).Stalled.Should().Be(0);
    }

    [Fact]
    public void A_teammates_row_names_its_owner_and_carries_no_cause_phase_summary_or_lever()
    {
        Guid runId = DomainId.New();
        RunDetails run = StatusFixtures.Run(runId, RunState.ReviewParked);
        TaskListItem review = Owned(TaskState.NeedsHuman, runId: runId, objective: "Review acme/widgets#9");
        review.Type = TaskType.PrReview;
        review.HolderOwnerRootFingerprint = TeammateRoot;
        // The owner-voiced line PrReviewAuthorActivity.Describe stores once and replicates: on this
        // viewer's screen its "you" is somebody else.
        review.PrReviewAuthorActivitySummary = "the author replied on 2 of your threads; your review is requested again";

        TaskStatusContext context = Viewer(run) with
        {
            ProjectMemberLabelsById = new Dictionary<Guid, ProjectMemberLabels>
            {
                [ProjectId] = Labels(new ProjectMemberLabel(TeammateRoot, [], DisplayName.Parse("Ryan"), "ryan-gh")),
            },
        };
        TaskStatusRow row = TaskStatusComposer.Compose(review, context, StatusFixtures.Now);

        row.TeammateOwner.Should().Be("Ryan");
        row.Assignee.Should().Be("Ryan", "the Owner column names them too");
        row.State.Should().Be(LifecycleState.Working);
        row.Objective.Should().Be("Review acme/widgets#9");
        row.Phase.HasPhase.Should().BeFalse();
        row.Attention.Should().Be(TaskAttention.None);
        row.AttentionMarkup.Should().BeEmpty();
        row.Facts.Should().BeEmpty();
        row.DetailMarkup.Should().ContainSingle().Which.Should().Contain("a teammate's task, owned by Ryan");

        string everything = string.Join(' ', row.DetailMarkup) + row.AttentionMarkup + string.Join(' ', row.SummaryMarkup);
        everything.Should().NotContainAny("your review", "your threads", "needs you", "h9k task", "h9k review", "abandon");
    }

    [Fact]
    public void A_teammate_with_no_declared_name_keeps_their_fingerprint_label_and_an_unknown_owner_is_said_to_be_unknown()
    {
        TaskListItem named = Owned(TaskState.Queued, assignedRoot: TeammateRoot);
        TaskListItem unknown = Owned(TaskState.Queued, assignedOwnerId: DomainId.New());
        TaskStatusContext context = Viewer() with
        {
            ProjectMemberLabelsById = new Dictionary<Guid, ProjectMemberLabels>
            {
                [ProjectId] = Labels(new ProjectMemberLabel(TeammateRoot, [], DisplayName.None, null)),
            },
        };

        TaskStatusRow fingerprintRow = TaskStatusComposer.Compose(named, context, StatusFixtures.Now);
        TaskStatusRow unknownRow = TaskStatusComposer.Compose(unknown, context, StatusFixtures.Now);

        fingerprintRow.TeammateOwner.Should().Be(TeammateRoot[..12]);
        fingerprintRow.DetailMarkup.Should().ContainSingle().Which.Should().Contain(TeammateRoot[..12]);
        unknownRow.IsTeammates.Should().BeTrue("an owner nobody can name is not the viewer");
        unknownRow.TeammateOwner.Should().BeEmpty();
        unknownRow.DetailMarkup.Should().ContainSingle().Which.Should().Contain("its owner is not known on this node");
    }

    [Fact]
    public void An_unassigned_task_is_judged_by_its_creator_fact_and_an_unresolved_creator_is_a_teammates()
    {
        TaskListItem fleetSiblings = Owned(TaskState.Published);
        TaskListItem unverified = Owned(TaskState.Published);
        TaskListItem teammates = Owned(TaskState.Published);
        TaskListItem unread = Owned(TaskState.Published);
        TaskStatusContext context = Viewer() with
        {
            CreatorFacts = new Dictionary<Guid, OwnerRootFact>
            {
                [fleetSiblings.Id] = OwnerRootFact.Known(ViewerRoot),
                [unverified.Id] = OwnerRootFact.Unresolved,
                [teammates.Id] = OwnerRootFact.Known(TeammateRoot),
            },
        };

        TaskStatusComposer.Compose(fleetSiblings, context, StatusFixtures.Now).IsTeammates.Should().BeFalse();
        TaskStatusComposer.Compose(unverified, context, StatusFixtures.Now).IsTeammates
            .Should().BeTrue("a creator no direct act has confirmed is unknown, and unknown is a teammate's");
        TaskStatusComposer.Compose(teammates, context, StatusFixtures.Now).IsTeammates.Should().BeTrue();
        TaskStatusComposer.Compose(unread, context, StatusFixtures.Now).IsTeammates
            .Should().BeTrue("a task the creator fact was never read for is unknown, and unknown is a teammate's");
    }

    [Fact]
    public void A_node_whose_owner_has_claimed_no_root_has_no_teammates()
    {
        TaskListItem foreign = Owned(TaskState.Queued, assignedRoot: TeammateRoot);
        TaskStatusContext noRoot = StatusFixtures.Context() with { ViewerRoot = null };

        TaskStatusComposer.Compose(foreign, noRoot, StatusFixtures.Now).IsTeammates.Should().BeFalse();
    }

    [Fact]
    public void The_summary_names_teammates_only_when_the_rollup_holds_some()
    {
        TaskRollup.Empty.Summary().Should().NotContain("teammates");
        (TaskRollup.Empty with { Teammates = 3 }).Summary().Should().Contain("3 teammates'");
        TaskRollup.Columns.Should().NotContain(TaskRollup.TeammatesColumn, "the column is opt-in");
        TaskRollup.Columns.Should().HaveCount(TaskRollup.Empty.Cells.Length);
    }

    [Fact]
    public void The_task_list_says_how_many_teammates_rows_it_held_back_and_the_flag_that_shows_them()
    {
        TaskListCommand.Settings settings = new();

        string footer = TaskListCommand.Footer(2, 2, 0, 0, settings, project: null, hiddenTeammates: 3);
        string empty = TaskListCommand.EmptyResultMessage(0, 0, settings, project: null, hiddenTeammates: 3);

        footer.Should().Contain("3 tasks belonging to teammates are not shown: h9k task list --everyone shows them");
        empty.Should().Contain("Nothing of yours matches").And.Contain("h9k task list --everyone shows them");
        TaskListCommand.EmptyResultMessage(1, 0, settings, project: null, hiddenTeammates: 3)
            .Should().Contain("Every task is archived").And.Contain("3 tasks belonging to teammates are not shown");
        TaskListCommand.Footer(2, 2, 0, 0, settings, project: null).Should().NotContain("teammates");
        TaskListCommand.Footer(2, 2, 0, 0, new TaskListCommand.Settings { Everyone = true }, project: null)
            .Should().NotContain("teammates", "nothing was hidden when they were asked for");
    }

    [Fact]
    public void The_teammates_group_is_a_state_word_and_matches_only_teammates_rows()
    {
        TaskStatusRow teammate = TaskStatusComposer.Compose(
            Owned(TaskState.Queued, assignedRoot: TeammateRoot), Viewer(), StatusFixtures.Now);
        TaskStatusRow mine = TaskStatusComposer.Compose(
            Owned(TaskState.Queued, assignedRoot: ViewerRoot), Viewer(), StatusFixtures.Now);

        TaskStateFilter.Validate("teammates");
        TaskStateFilter.Matches(teammate, "teammates").Should().BeTrue();
        TaskStateFilter.Matches(mine, "teammates").Should().BeFalse();
        TaskStateFilter.Matches(teammate, "queued").Should().BeFalse("the Queued group counts the viewer's own queue");
    }

    [Fact]
    public void An_owner_with_neither_a_name_nor_a_login_is_told_teammates_see_a_fingerprint_when_the_project_has_another_member()
    {
        ProjectMemberLabels unnamed = Labels(
            new ProjectMemberLabel(ViewerRoot, [], DisplayName.None, null),
            new ProjectMemberLabel(TeammateRoot, [], DisplayName.Parse("Ryan"), null));

        IReadOnlyList<string> lines = StatusCommand.OwnerLabelNudgeLines([("hall9k", unnamed)], ViewerRoot);

        lines.Should().ContainSingle().Which.Should()
            .Contain("hall9k")
            .And.Contain("teammates see this owner as a fingerprint")
            .And.Contain(ViewerRoot[..12])
            .And.Contain("h9k owner set --display-name");
    }

    [Fact]
    public void The_fingerprint_line_stays_silent_unless_the_owner_is_unnamed_and_has_a_teammate_to_be_seen_by()
    {
        ProjectMemberLabels alone = Labels(new ProjectMemberLabel(ViewerRoot, [], DisplayName.None, null));
        ProjectMemberLabels named = Labels(
            new ProjectMemberLabel(ViewerRoot, [], DisplayName.Parse("Brian"), null),
            new ProjectMemberLabel(TeammateRoot, [], DisplayName.None, null));
        ProjectMemberLabels declaredLogin = Labels(
            new ProjectMemberLabel(ViewerRoot, [], DisplayName.None, "brianhallmanac"),
            new ProjectMemberLabel(TeammateRoot, [], DisplayName.None, null));
        ProjectMemberLabels notListed = Labels(new ProjectMemberLabel(TeammateRoot, [], DisplayName.None, null));

        StatusCommand.OwnerLabelNudgeLines(
            [("a", alone), ("b", named), ("c", declaredLogin), ("d", notListed), ("e", null)], ViewerRoot)
            .Should().BeEmpty();
        StatusCommand.OwnerLabelNudgeLines(
            [("a", Labels(
                new ProjectMemberLabel(ViewerRoot, [], DisplayName.None, null),
                new ProjectMemberLabel(TeammateRoot, [], DisplayName.None, null)))],
            ViewerRoot).Should().ContainSingle("a teammate with no declared name is no reason to stay silent");
    }

    [Fact]
    public void A_review_request_row_names_a_covering_task_only_when_it_is_the_viewers_own()
    {
        Guid taskId = DomainId.New();
        string shortId = DomainId.Short(taskId);
        ObservedReviewRequest request = new()
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = ProjectId,
            Repository = "acme/widgets",
            Number = 9,
            ReviewerLogin = "brian",
            RequestedAt = StatusFixtures.Now.AddMinutes(-5),
            Outcome = ReviewRequestOutcome.AlreadyCovered,
        };

        ReviewRequestRow own = ReviewRequestPane.Compose(
            request, "hall9k", AutoPrReviewSetting.Unrecorded,
            new CoveringReview(taskId, Live: true, "Working", AutoCreated: true, GateParked: false), StatusFixtures.Now);
        ReviewRequestRow teammates = ReviewRequestPane.Compose(
            request, "hall9k", AutoPrReviewSetting.Unrecorded,
            new CoveringReview(taskId, Live: true, "Working", AutoCreated: true, GateParked: false, Own: false),
            StatusFixtures.Now);

        own.Markup.Should().Contain(shortId);
        teammates.NeedsYou.Should().BeFalse();
        teammates.Markup.Should().Contain("a task this install cannot yet tell from a teammate's already covers it").And.NotContain(shortId).And.NotContain("Working");
    }

    [Fact]
    public void A_mention_attached_to_a_teammates_task_still_asks_the_viewer_and_names_no_task_of_theirs()
    {
        Guid theirs = DomainId.New();
        ObservedReviewMention mention = Mention("AttachedNoFollowUp", theirs);
        CoveringReview viewersOwn = new(DomainId.New(), Live: true, "Working", AutoCreated: true, GateParked: false);

        ReviewRequestRow row = ReviewRequestPane.ComposeMentionRow(
            mention, "hall9k", viewersOwn, attachedToTeammate: true);
        ReviewRequestRow unrecorded = ReviewRequestPane.ComposeMentionRow(
            Mention("AttachedNoFollowUp", taskId: null), "hall9k",
            new CoveringReview(DomainId.New(), Live: true, "Working", AutoCreated: true, GateParked: false, Own: false));

        row.NeedsYou.Should().BeTrue("the comment mentioned the viewer, and no follow-up was dispatched to answer it");
        row.Markup.Should().Contain("attached to a teammate's task").And.NotContain(DomainId.Short(theirs))
            .And.Contain("h9k pr review acme/widgets#9 --since-my-review");
        unrecorded.NeedsYou.Should().BeTrue();
        unrecorded.Markup.Should().Contain("attached to a teammate's task");
    }

    [Fact]
    public void A_live_teammates_task_outranks_the_viewers_closed_one_and_is_never_named()
    {
        TaskListItem closedOwn = Owned(TaskState.Done, assignedRoot: ViewerRoot);
        TaskListItem liveTheirs = Owned(TaskState.Queued, assignedRoot: TeammateRoot);
        TaskListItem closedTheirs = Owned(TaskState.Abandoned, assignedRoot: TeammateRoot);
        TaskListItem[] all = [closedOwn, liveTheirs, closedTheirs];
        foreach (TaskListItem task in all)
        {
            task.ExternalReference = "github-pr:acme/widgets#9";
        }

        Dictionary<Guid, TaskStatusRow> rows = all
            .ToDictionary(task => task.Id, task => TaskStatusComposer.Compose(task, Viewer(), StatusFixtures.Now));

        CoveringReview? mixed = ReviewRequestPane.Covering("acme/widgets", 9, [closedOwn, liveTheirs], rows);
        CoveringReview? onlyClosedTheirs = ReviewRequestPane.Covering("acme/widgets", 9, [closedTheirs], rows);

        mixed.Should().Match<CoveringReview?>(
            covering => covering != null && !covering.Own && covering.Live,
            "a live review of the pull request is what covers it, and it is not the viewer's to name");
        onlyClosedTheirs.Should().Match<CoveringReview?>(covering => covering != null && !covering.Live);
        ReviewRequestPane.ComposeMentionRow(Mention("HeldSettingOff", null), "hall9k", onlyClosedTheirs).Markup
            .Should().Contain("a teammate's task already covered it and is closed");
    }

    [Fact]
    public void A_teammates_closed_auto_created_task_does_not_make_the_viewers_row_promise_a_hold()
    {
        TaskListItem closedOwn = Owned(TaskState.Done, assignedRoot: ViewerRoot);
        TaskListItem closedTheirs = Owned(TaskState.Done, assignedRoot: TeammateRoot);
        closedTheirs.WasAutoPrReviewCreated = true;
        TaskListItem[] all = [closedOwn, closedTheirs];
        foreach (TaskListItem task in all)
        {
            task.ExternalReference = "github-pr:acme/widgets#9";
        }

        Dictionary<Guid, TaskStatusRow> rows = all
            .ToDictionary(task => task.Id, task => TaskStatusComposer.Compose(task, Viewer(), StatusFixtures.Now));

        CoveringReview? covering = ReviewRequestPane.Covering("acme/widgets", 9, all, rows);

        covering.Should().Match<CoveringReview?>(
            review => review != null && review.Own && !review.AutoCreated,
            "the engine's re-mint guard counts only this owner's auto-created tasks, so the pane must too");
    }

    private static ObservedReviewMention Mention(string outcome, Guid? taskId) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = ProjectId,
            Repository = "acme/widgets",
            Number = 9,
            MentionedLogin = "brian",
            CommentAuthorLogin = "ryan",
            TaskId = taskId,
            Outcome = outcome,
        };

    private static TaskListItem Owned(
        TaskState state, string? assignedRoot = null, Guid? assignedOwnerId = null, Guid? runId = null,
        string? pullRequest = null, string objective = "x")
    {
        TaskListItem task = StatusFixtures.Task(
            state, runId: runId, pullRequest: pullRequest, objective: objective, projectId: ProjectId,
            claimedByNodeId: state == TaskState.Claimed ? DomainId.New() : null);
        task.AssignedOwnerFingerprint = assignedRoot;
        task.AssignedOwnerId = assignedOwnerId ?? (assignedRoot is null ? null : DomainId.New());
        return task;
    }

    private static TaskStatusContext Viewer(
        RunDetails? run = null, Guid? siblingOwnerId = null, DateTimeOffset? silentSince = null) =>
        StatusFixtures.Context(run, silentSince) with
        {
            ViewerRoot = ViewerRoot,
            OwnerRootsById = siblingOwnerId is { } sibling
                ? new Dictionary<Guid, string> { [sibling] = ViewerRoot }
                : new Dictionary<Guid, string>(),
            CreatorFacts = new Dictionary<Guid, OwnerRootFact>(),
            Projects = new Dictionary<Guid, string> { [ProjectId] = "hall9k" },
        };

    private static ProjectMemberLabels Labels(params ProjectMemberLabel[] labels) =>
        new() { Id = ProjectId, Labels = labels };
}
