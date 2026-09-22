using FluentAssertions;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// What <c>h9k orchestrator feed</c> actually prints (idea 89471598, piece 2), pinned as a
/// golden: oldest first, grouped by task, one plain line each, every line naming what happened
/// rather than an id. A change to any description or to the grouping shows up here as a diff a
/// reviewer can read, which is the point of pinning it.
/// </summary>
public sealed class OrchestratorFeedRendererTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 19, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid FeedTask = Guid.Parse("01a0bc05-a960-7657-b708-1aed37b5ec69");
    private static readonly Guid PresenceTask = Guid.Parse("01a0bc05-a960-7657-b708-1aed579dcd44");

    // Fixed rather than fresh, because a note's own feed line now opens with its stream id and
    // MessageStreamId.ForMessage derives that from exactly these two plus the seq — a Guid.NewGuid()
    // here would make the golden below a different string on every run.
    private static readonly Guid NoteSender = Guid.Parse("01a0bc05-a960-7657-b708-1aed4a1b2c3d");
    private static readonly Guid NoteProject = Guid.Parse("01a0bc05-a960-7657-b708-1aed9f8e7d6c");

    [Fact]
    public void The_grouped_output_reads_oldest_first_under_one_heading_per_task()
    {
        OrchestratorFeedItem[] items =
        [
            Item(10, FeedTask, new TaskPublished(FeedTask, At, Guid.NewGuid())),
            Item(11, PresenceTask, new TaskClaimed(
                PresenceTask, Guid.Parse("44444444-4444-4444-4444-444444444444"),
                Guid.NewGuid(), 1, Guid.NewGuid(), At)),
            Item(12, null, new IdeaCaptured(
                Guid.NewGuid(), Guid.NewGuid(), "The platform owns the orchestrator's watching", null, At)),
            Item(13, FeedTask, new ReviewParked(
                FeedTask, "the adversarial lens and the fix session disagree about finding 2", At)),
            Item(14, null, Note("are you still on the stacked pair?")),
            Item(15, PresenceTask, new RunFailed(PresenceTask, "the verification gate never finished", At)),
        ];

        // UTC deliberately, not TimeZoneInfo.Local: the CLI prints local time so the feed reads
        // on the same clock as the rest of the board, and a golden pinned to the machine's own
        // zone would pass here and fail on the next machine.
        IReadOnlyList<string> lines = OrchestratorFeedRenderer.Render(
            OrchestratorFeedRenderer.Group(items, Heading), TimeZoneInfo.Utc);

        lines.Should().Equal(
            "37b5ec69  The daemon keeps a per-project orchestrator feed",
            "  2026-09-19 14:10  published and ready to assign",
            "  2026-09-19 14:13  the review loop parked for a human: the adversarial lens and the fix session disagree about finding 2",
            "579dcd44  An orchestrator window announces itself as live",
            "  2026-09-19 14:11  claimed by node 44444444; a run is starting",
            "  2026-09-19 14:15  the run failed: the verification gate never finished",
            "Not about one task",
            "  2026-09-19 14:12  idea logged: The platform owns the orchestrator's watching",
            "  2026-09-19 14:14  1d077852 a message from abcdef012345: are you still on the stacked pair?");
    }

    [Fact]
    public void A_group_is_ordered_by_its_own_oldest_item_so_the_whole_output_still_reads_in_order()
    {
        OrchestratorFeedItem[] items =
        [
            Item(30, PresenceTask, new TaskPublished(PresenceTask, At, Guid.NewGuid())),
            Item(31, FeedTask, new TaskPublished(FeedTask, At, Guid.NewGuid())),
            Item(32, PresenceTask, new RunCompleted(PresenceTask, At)),
        ];

        IReadOnlyList<OrchestratorFeedGroup> groups = OrchestratorFeedRenderer.Group(items, Heading);

        groups.Select(group => group.TaskId).Should().Equal(PresenceTask, FeedTask);
    }

    [Fact]
    public void A_timestamp_is_written_in_the_clock_the_caller_names()
    {
        TimeZoneInfo fourHoursBehind = TimeZoneInfo.CreateCustomTimeZone(
            "feed-test", TimeSpan.FromHours(-4), "feed-test", "feed-test");
        OrchestratorFeedGroup[] groups = OrchestratorFeedRenderer
            .Group([Item(10, FeedTask, new TaskPublished(FeedTask, At, Guid.NewGuid()))], Heading)
            .ToArray();

        OrchestratorFeedRenderer.Render(groups, TimeZoneInfo.Utc)[1]
            .Should().StartWith("  2026-09-19 14:10");
        OrchestratorFeedRenderer.Render(groups, fourHoursBehind)[1]
            .Should().StartWith("  2026-09-19 10:10");
    }

    [Fact]
    public void Prose_a_person_wrote_is_flattened_onto_one_line_and_clipped()
    {
        string sprawling = "first line\r\nsecond line\tthird\n\n" + new string('x', 400);

        string? described = OrchestratorFeedDescription.Of(new RunFailed(FeedTask, sprawling, At));

        described.Should().NotBeNull();
        described!.Should().NotContain("\n").And.NotContain("\r").And.NotContain("\t");
        described.Should().EndWith("…");
        described.Length.Should().BeLessThan(200);
    }

    [Fact]
    public void A_claim_that_names_no_node_is_never_described_as_node_00000000()
    {
        // h9k task work and h9k task start both record Guid.Empty as the node id, deliberately
        // naming no node; shortening that sentinel would assert a node nothing observed.
        TaskClaimed byHand = new(
            FeedTask, Guid.Empty, Guid.NewGuid(), 1, Guid.NewGuid(), At,
            InteractiveMode: true, OwnerRootFingerprint: "abcdef0123456789");
        TaskClaimed byHandWithNoFingerprint = new(
            FeedTask, Guid.Empty, Guid.NewGuid(), 1, Guid.NewGuid(), At, InteractiveMode: false);

        OrchestratorFeedDescription.Of(byHand)
            .Should().Be("claimed interactively by abcdef012345; a human has the wheel");
        OrchestratorFeedDescription.Of(byHandWithNoFingerprint).Should()
            .Be("claimed by somebody the claim does not name as a deliberate kick-off; a run is starting");
        OrchestratorFeedDescription.Of(byHand).Should().NotContain("00000000");
        OrchestratorFeedDescription.Of(byHandWithNoFingerprint).Should().NotContain("00000000");
    }

    [Fact]
    public void A_record_with_no_reason_says_so_rather_than_ending_in_a_colon()
    {
        OrchestratorFeedDescription.Of(new RunFailed(FeedTask, "   ", At))
            .Should().Be("the run failed: no reason recorded");
    }

    [Fact]
    public void Every_type_the_interest_table_names_has_a_description_arm()
    {
        // The two tables are separate on purpose — one decides membership, the other decides
        // wording — so nothing but a test keeps them in step. A type in the filter with no arm
        // here would be silently dropped at read time.
        Type[] withoutAnArm = [.. OrchestratorFeedInterest.InterestingEventTypes.Where(type => !HasAnArm(type))];

        withoutAnArm.Should().BeEmpty(
            "every event type the orchestrator feed admits must have a sentence — "
            + $"found without one: {string.Join(", ", withoutAnArm.Select(t => t.Name))}");
    }

    /// <summary>
    /// Whether <see cref="OrchestratorFeedDescription.Of"/> has an arm for this type. The probe
    /// is an instance with every field left at its own zero, built through the
    /// uninitialized-object door because these are positional records with no parameterless
    /// constructor and the alternative is naming sixty-odd constructors by hand.
    /// <para>
    /// Only the arm's existence is under test, never what it says, so a null reference counts as
    /// a hit: an arm that reached into a list or a value object this probe left null has already
    /// proved it matched. The one answer that means "no arm" is the null return, which is what
    /// the switch's own default produces and nothing else does.
    /// </para>
    /// </summary>
    private static bool HasAnArm(Type type)
    {
        object blank = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        try
        {
            return OrchestratorFeedDescription.Of(blank) is not null;
        }
        catch (NullReferenceException)
        {
            return true;
        }
    }

    private static string Heading(Guid taskId) => taskId == FeedTask
        ? "37b5ec69  The daemon keeps a per-project orchestrator feed"
        : "579dcd44  An orchestrator window announces itself as live";

    private static OrchestratorFeedItem Item(long sequence, Guid? taskId, object data) =>
        new(sequence, At.AddMinutes(sequence), taskId, OrchestratorFeedDescription.Of(data)!);

    private static MessageReceived Note(string body) => new(
        FromNodeId: NoteSender,
        Seq: 1,
        SentAt: At,
        FromOwnerFingerprint: "abcdef0123456789",
        To: "project",
        About: null,
        Kind: MessageKind.Note.Value,
        Body: body,
        ReceivedAt: At,
        ProjectId: NoteProject);
}
