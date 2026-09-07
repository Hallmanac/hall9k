using FluentAssertions;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The cross-project ordering rule (Decisions Log #141) where it lives: which project receives the
/// next free slot, and why. Pure, so every tie-break the ruling documents can be stated as one
/// assertion rather than acted out through a database — the discipline
/// <see cref="NodeLoadTests"/> and <see cref="ProjectRunCeilingTests"/> already follow.
/// <see cref="Hall9k.Tests.Integration.CrossProjectRotationDispatchTests"/> is where the same
/// rules are acted out end to end against a real dispatcher.
/// </summary>
public sealed class ProjectRotationTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Alpha = DomainId.New();
    private static readonly Guid Beta = DomainId.New();
    private static readonly Guid Gamma = DomainId.New();

    [Fact]
    public void One_project_is_served_oldest_first_with_no_setting_at_all()
    {
        // The single-project install, which is every install until it is not: the rotation has one
        // member, so it is plain oldest-first and no tier ever has to be set.
        QueuedCandidate[] queue = [Queued(Alpha), Queued(Alpha)];

        RotationSlot slot = Next(queue, Load(Uncapped(Alpha)))!;

        slot.Candidate.Should().Be(queue[0]);
        slot.Reason.Should().Be(SlotReason.OnlyEligibleProject);
        slot.EligibleProjects.Should().Be(1);
    }

    [Fact]
    public void A_cold_start_serves_the_queues_own_order_because_nothing_has_been_served_yet()
    {
        // The documented first-dispatch tie-break: with no project served, the rotation cannot
        // order by last dispatch, so the queue's own order decides — which makes the first claim
        // after a start (or a restart) exactly the claim the platform made before any of this.
        QueuedCandidate[] queue = [Queued(Beta), Queued(Alpha)];

        RotationSlot slot = Next(queue, Load(Uncapped(Alpha), Uncapped(Beta)))!;

        slot.Candidate.Should().Be(queue[0], "the head of the queue belongs to Beta, so Beta goes first");
        slot.Reason.Should().Be(SlotReason.LongestUnserved);
    }

    [Fact]
    public void The_longest_unserved_project_wins_and_a_never_served_one_outranks_every_served_one()
    {
        QueuedCandidate[] queue = [Queued(Alpha), Queued(Beta), Queued(Gamma)];
        Dictionary<Guid, DateTimeOffset> served = new()
        {
            [Alpha] = Noon,
            [Beta] = Noon.AddMinutes(-5),
        };

        // Gamma has never been dispatched for, which outranks Beta's five-minute-old dispatch.
        Next(queue, Load(Uncapped(Alpha), Uncapped(Beta), Uncapped(Gamma)), served)!
            .Candidate.ProjectId.Should().Be(Gamma);

        // With Gamma gone from the queue, the older of the two remaining dispatches wins.
        RotationSlot slot = Next([queue[0], queue[1]], Load(Uncapped(Alpha), Uncapped(Beta)), served)!;
        slot.Candidate.ProjectId.Should().Be(Beta);
        slot.LastServedAt.Should().Be(Noon.AddMinutes(-5), "the line says when, so the decision reads back");
    }

    [Fact]
    public void A_project_at_its_cap_is_skipped_without_consuming_a_turn()
    {
        // Eligibility is "ready work under every applicable limit". A project at its cap, paused
        // at 0, or with nothing queued is not a rotation member this time round — so the rotation
        // never idles on a project that could not start work anyway, and being skipped costs it
        // nothing in the order once it is eligible again.
        QueuedCandidate[] queue = [Queued(Alpha), Queued(Beta), Queued(Gamma)];
        DispatchLoad load = Load(
            new ProjectLoad(Alpha, "alpha", new ProjectRunCeiling(LiveRuns: 1, Cap: 1), ProjectPriority.Normal),
            new ProjectLoad(Beta, "beta", new ProjectRunCeiling(LiveRuns: 0, Cap: 0), ProjectPriority.Normal),
            Uncapped(Gamma));

        RotationSlot slot = Next(queue, load, new Dictionary<Guid, DateTimeOffset> { [Gamma] = Noon })!;

        slot.Candidate.ProjectId.Should().Be(Gamma, "it is the only project under every limit");
        slot.Reason.Should().Be(SlotReason.OnlyEligibleProject,
            "an ineligible project is not something the rotation chose between");
        slot.EligibleProjects.Should().Be(1);
    }

    [Fact]
    public void A_sweeps_own_claims_fill_a_cap_as_it_goes()
    {
        // Asked once per slot, not once per sweep: the caller passes what it has already claimed,
        // so a project capped at 1 that took this sweep's first slot is out of the running for
        // the second one even though nothing has finished.
        QueuedCandidate[] queue = [Queued(Alpha), Queued(Beta)];
        DispatchLoad load = Load(
            new ProjectLoad(Alpha, "alpha", new ProjectRunCeiling(LiveRuns: 0, Cap: 1), ProjectPriority.Normal),
            Uncapped(Beta));

        Next([queue[1]], load, claimed: new Dictionary<Guid, int> { [Alpha] = 1 })!
            .Candidate.ProjectId.Should().Be(Beta);
    }

    [Fact]
    public void A_higher_tier_outranks_the_rotation_however_long_the_others_have_waited()
    {
        // Focus: the tier decides the slot, not the rotation, and the reason says so — otherwise
        // an operator reading the log would take a focused claim for a fairness decision.
        QueuedCandidate[] queue = [Queued(Alpha), Queued(Beta)];
        DispatchLoad load = Load(High(Alpha), Uncapped(Beta));
        Dictionary<Guid, DateTimeOffset> served = new()
        {
            [Alpha] = Noon,
            [Beta] = Noon.AddHours(-3),
        };

        RotationSlot slot = Next(queue, load, served)!;

        slot.Candidate.ProjectId.Should().Be(Alpha, "a tier is not a tie-break; it is decided before age");
        slot.Reason.Should().Be(SlotReason.PriorityTier);
        slot.Priority.Should().Be(ProjectPriority.High);
    }

    [Fact]
    public void Focus_releases_itself_the_moment_the_higher_tiers_queue_drains()
    {
        // The whole distinction from a cap of 0: nothing is remembered and no command is needed —
        // the tier stops mattering because the project stops having eligible work.
        DispatchLoad load = Load(High(Alpha), Uncapped(Beta));

        Next([Queued(Alpha), Queued(Beta)], load)!.Candidate.ProjectId.Should().Be(Alpha);
        Next([Queued(Beta)], load)!.Candidate.ProjectId.Should().Be(Beta,
            "with nothing of the focused project's queued, the lower tier resumes on its own");
    }

    [Fact]
    public void A_focused_project_at_its_own_cap_does_not_hold_the_lower_tier_back()
    {
        // A tier orders who receives a free slot; it never overrides a limit. A high-tier project
        // that cannot take a run is not eligible, so the slot goes on down the tiers rather than
        // being held open for it — nothing is ever reserved.
        DispatchLoad load = Load(
            new ProjectLoad(Alpha, "alpha", new ProjectRunCeiling(LiveRuns: 1, Cap: 1), ProjectPriority.High),
            Uncapped(Beta));

        Next([Queued(Alpha), Queued(Beta)], load)!.Candidate.ProjectId.Should().Be(Beta);
    }

    [Fact]
    public void Rotation_applies_within_a_tier()
    {
        QueuedCandidate[] queue = [Queued(Alpha), Queued(Beta), Queued(Gamma)];
        DispatchLoad load = Load(High(Alpha), High(Beta), Uncapped(Gamma));
        Dictionary<Guid, DateTimeOffset> served = new() { [Alpha] = Noon, [Beta] = Noon.AddMinutes(-1) };

        RotationSlot slot = Next(queue, load, served)!;

        slot.Candidate.ProjectId.Should().Be(Beta, "both are focused, so the rotation decides between them");
        slot.Reason.Should().Be(SlotReason.PriorityTier, "a lower tier is still being outranked by the pair");
    }

    [Fact]
    public void A_low_tier_takes_a_slot_only_when_no_higher_tier_has_ready_work()
    {
        DispatchLoad load = Load(Low(Alpha), Uncapped(Beta));

        Next([Queued(Alpha), Queued(Beta)], load)!.Candidate.ProjectId.Should().Be(Beta);
        Next([Queued(Alpha)], load)!.Reason.Should().Be(SlotReason.OnlyEligibleProject);
    }

    [Fact]
    public void An_unrecognized_recorded_tier_rotates_as_the_default_rather_than_jumping_the_queue()
    {
        // A tier this build does not know reads as Unknown, which shares the default tier's own
        // number: it can neither starve a project nor silently promote one.
        DispatchLoad load = Load(
            new ProjectLoad(Alpha, "alpha", ProjectRunCeiling.Uncapped(0), ProjectPriority.FromInput("weird")),
            Uncapped(Beta));

        RotationSlot slot = Next([Queued(Beta), Queued(Alpha)], load)!;

        slot.Candidate.ProjectId.Should().Be(Beta, "the queue's order decides, exactly as between two normals");
        slot.Reason.Should().Be(SlotReason.LongestUnserved);
    }

    [Fact]
    public void A_queue_first_marked_task_takes_the_next_free_slot_ahead_of_the_rotation_and_of_every_tier()
    {
        // Decisions Log #127's promise is the next free slot regardless of assignment age; the
        // marker is a human's per-task instruction and clears itself as the claim commits, so it
        // outranks both the rotation and a focus rather than being reordered behind them.
        QueuedCandidate[] queue = [Queued(Alpha), Queued(Beta, queueFirst: true)];
        DispatchLoad load = Load(High(Alpha), Uncapped(Beta));

        RotationSlot slot = Next(queue, load)!;

        slot.Candidate.Should().Be(queue[1]);
        slot.Reason.Should().Be(SlotReason.QueueFirstMarker);
    }

    [Fact]
    public void A_queue_first_marked_task_whose_project_cannot_claim_does_not_hold_the_slot_open()
    {
        // The marker buys a place in the order, never an exemption from a limit — a paused
        // project's marked task waits like everything else of that project's.
        QueuedCandidate[] queue = [Queued(Alpha, queueFirst: true), Queued(Beta)];
        DispatchLoad load = Load(
            new ProjectLoad(Alpha, "alpha", new ProjectRunCeiling(LiveRuns: 0, Cap: 0), ProjectPriority.Normal),
            Uncapped(Beta));

        Next(queue, load)!.Candidate.ProjectId.Should().Be(Beta);
    }

    [Fact]
    public void Nothing_is_offered_when_no_project_can_admit_a_claim()
    {
        DispatchLoad load = Load(
            new ProjectLoad(Alpha, "alpha", new ProjectRunCeiling(LiveRuns: 0, Cap: 0), ProjectPriority.High));

        Next([Queued(Alpha)], load).Should().BeNull("a paused project claims nothing, tier or no tier");
        Next([], Load()).Should().BeNull();
    }

    [Fact]
    public void A_project_no_document_answered_for_rotates_in_the_default_tier_uncapped()
    {
        // The measurement's own honest fallback, seen from here: an unmeasured project is neither
        // capped nor focused, so it takes its turn like anything else.
        RotationSlot slot = Next([Queued(Alpha)], Load())!;

        slot.Candidate.ProjectId.Should().Be(Alpha);
        slot.Priority.Should().Be(ProjectPriority.Normal);
    }

    private static RotationSlot? Next(
        IReadOnlyList<QueuedCandidate> queue,
        DispatchLoad load,
        IReadOnlyDictionary<Guid, DateTimeOffset>? served = null,
        IReadOnlyDictionary<Guid, int>? claimed = null) =>
        ProjectRotation.NextSlot(queue, load, claimed ?? new Dictionary<Guid, int>(), served ?? new Dictionary<Guid, DateTimeOffset>());

    private static QueuedCandidate Queued(Guid projectId, bool queueFirst = false) =>
        new(DomainId.New(), projectId, queueFirst);

    private static DispatchLoad Load(params ProjectLoad[] projects) =>
        new(new NodeLoad(LiveRuns: 0, ConfiguredMaxConcurrentRuns: 2),
            projects.ToDictionary(project => project.ProjectId));

    private static ProjectLoad Uncapped(Guid projectId) =>
        new(projectId, projectId.ToString(), ProjectRunCeiling.Uncapped(0), ProjectPriority.Normal);

    private static ProjectLoad High(Guid projectId) =>
        new(projectId, projectId.ToString(), ProjectRunCeiling.Uncapped(0), ProjectPriority.High);

    private static ProjectLoad Low(Guid projectId) =>
        new(projectId, projectId.ToString(), ProjectRunCeiling.Uncapped(0), ProjectPriority.Low);
}
