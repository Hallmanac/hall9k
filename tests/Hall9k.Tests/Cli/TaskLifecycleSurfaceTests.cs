using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The lifecycle states on the surfaces a human reads (Decisions Log #34): Draft, Published
/// and Blocked each count in their own bucket rather than disappearing into "closed", a
/// blocked row says what it is waiting on, and every row carries its assignee.
/// </summary>
public sealed class TaskLifecycleSurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_lifecycle_state_reads_as_itself_rather_than_as_closed()
    {
        // The bucket is internal, so the cases live here rather than in [InlineData].
        (string State, AttentionBucket Expected)[] cases =
        [
            ("Draft", AttentionBucket.Draft),
            ("Published", AttentionBucket.Ready),
            ("Blocked", AttentionBucket.Blocked),
        ];

        foreach ((string state, AttentionBucket expected) in cases)
        {
            TaskStatusRow row = Compose(Task(state));

            row.Bucket.Should().Be(state);
            row.Attention.Should().Be(expected);
            row.StatusMarkup.Should().Contain(state, "the Status column is where a human reads the state");
        }
    }

    [Fact]
    public void A_blocked_row_says_what_it_is_waiting_on()
    {
        Guid dependencyId = DomainId.New();
        TaskListItem blocked = Task("Blocked");
        blocked.BlockedBy = [dependencyId];
        blocked.UnmetDependencies = [dependencyId];

        TaskStatusRow row = Compose(blocked);

        row.Activity.Should().Be($"blocked by {TaskListCommand.ShortId(dependencyId)}",
            "a wait with no visible cause sends the reader hunting");
        row.UnmetDependencies.Should().Equal(dependencyId);
    }

    [Fact]
    public void A_blocked_task_whose_blocker_died_reads_as_needing_a_human()
    {
        TaskListItem blocked = Task("Blocked");
        blocked.UnmetDependencies = [DomainId.New()];
        blocked.DependencyFailureReason = "Dependency 3f2a91b2 will never close out on its own.";

        TaskStatusRow row = Compose(blocked);

        row.Bucket.Should().Be("NeedsHuman", "it cannot unblock itself and must not be forgotten");
        row.Attention.Should().Be(AttentionBucket.NeedsYou);
        row.Priority.Should().Be(0);
    }

    [Fact]
    public void A_blocked_task_whose_blocker_was_retried_goes_back_to_waiting_rather_than_shouting()
    {
        // What the resolver's recovery event leaves behind (Decisions Log #61): still Blocked,
        // dead record gone. A board that says "act now" about a handled situation trains its
        // reader to ignore it.
        Guid dependencyId = DomainId.New();
        TaskListItem blocked = Task("Blocked");
        blocked.UnmetDependencies = [dependencyId];
        blocked.DependencyFailureReason = null;

        TaskStatusRow row = Compose(blocked);

        row.Bucket.Should().Be("Blocked");
        row.Attention.Should().NotBe(AttentionBucket.NeedsYou);
        row.Activity.Should().Be($"blocked by {TaskListCommand.ShortId(dependencyId)}");
    }

    [Fact]
    public void Every_row_carries_its_assignee_and_an_unassigned_one_says_so()
    {
        Guid ownerId = DomainId.New();
        TaskListItem assigned = Task("Queued");
        assigned.AssignedOwnerId = ownerId;

        Compose(assigned, owners: new Dictionary<Guid, string> { [ownerId] = "Brian" })
            .Assignee.Should().Be("Brian");
        Compose(Task("Draft")).AssigneeMarkup.Should().Be("[dim]—[/]", "an empty cell would read as a gap");
    }

    [Fact]
    public void The_rollup_counts_the_lifecycle_states_and_still_sums_to_the_task_count()
    {
        TaskStatusRow[] rows =
        [
            Compose(Task("Draft")),
            Compose(Task("Draft")),
            Compose(Task("Published")),
            Compose(Task("Blocked")),
            Compose(Task("Queued")),
        ];

        TaskRollup rollup = TaskRollup.From(rows);

        rollup.Draft.Should().Be(2);
        rollup.Ready.Should().Be(1);
        rollup.Blocked.Should().Be(1);
        rollup.Queued.Should().Be(1);
        rollup.Total.Should().Be(rows.Length, "the buckets stay single-assignment");
        rollup.Summary().Should().Contain("2 draft").And.Contain("1 ready to assign").And.Contain("1 blocked");
    }

    [Theory]
    [InlineData("draft", "Draft")]
    [InlineData("ready", "Published")]
    [InlineData("published", "Published")]
    [InlineData("Blocked", "Blocked")]
    public void The_state_filter_selects_the_lifecycle_states_by_the_words_a_reader_just_read(
        string filter, string bucket)
    {
        TaskStateFilter.Validate(filter);

        TaskStateFilter.Matches(Compose(Task(bucket)), filter).Should().BeTrue();
        TaskStateFilter.Matches(Compose(Task("Queued")), filter).Should().BeFalse();
    }

    private static TaskListItem Task(string state) => new()
    {
        Id = DomainId.New(),
        ProjectId = DomainId.New(),
        Objective = "x",
        Type = TaskType.Chore,
        State = state,
        AddedAt = Now,
    };

    private static TaskStatusRow Compose(TaskListItem task, IReadOnlyDictionary<Guid, string>? owners = null) =>
        TaskStatusComposer.Compose(
            task,
            new Dictionary<Guid, RunListItem>(),
            new Dictionary<Guid, RunActivity>(),
            new Dictionary<Guid, string>(),
            owners ?? new Dictionary<Guid, string>(),
            Now);
}
