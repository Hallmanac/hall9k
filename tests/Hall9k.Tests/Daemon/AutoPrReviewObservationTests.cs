using FluentAssertions;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The once-per-pull-request log line of Decisions Log #161: a line is owed the first time a
/// request is seen and whenever what became of it actually changes, and never again for a
/// standing request whose answer has not moved — at a three-minute poll interval, a line per
/// tick would bury every other line in the log an orchestrator window tails.
/// </summary>
public sealed class AutoPrReviewObservationTests
{
    [Fact]
    public void A_request_nothing_has_recorded_yet_is_reportable()
    {
        AutoPrReviewObservation.IsReportable(null, ReviewRequestOutcome.HeldSettingOff, null).Should().BeTrue();
    }

    [Fact]
    public void The_same_outcome_on_a_standing_request_is_not_reported_again()
    {
        ObservedReviewRequest recorded = new() { Outcome = ReviewRequestOutcome.HeldSettingOff };

        AutoPrReviewObservation.IsReportable(recorded, ReviewRequestOutcome.HeldSettingOff, null)
            .Should().BeFalse();
    }

    [Fact]
    public void A_changed_outcome_is_reported_because_that_is_the_transition_being_watched_for()
    {
        ObservedReviewRequest recorded = new() { Outcome = ReviewRequestOutcome.HeldSettingOff };

        AutoPrReviewObservation.IsReportable(recorded, ReviewRequestOutcome.TaskCreated, DomainId.New())
            .Should().BeTrue("an operator who just turned the setting on is watching for exactly this line");
    }

    /// <summary>
    /// A re-review is a second task under the identical <c>TaskCreated</c> outcome (independent
    /// pre-PR review, cycle 1, adversarial lens): the reviewer re-requested, the first review was
    /// already Done, and the mint that answers it is the transition the log exists to show —
    /// suppressing it because the outcome word had not changed would hide the one line about it.
    /// </summary>
    [Fact]
    public void A_second_task_minted_for_a_re_review_is_reported_though_the_outcome_word_is_the_same()
    {
        ObservedReviewRequest recorded = new()
        {
            Outcome = ReviewRequestOutcome.TaskCreated,
            TaskId = DomainId.New(),
        };

        AutoPrReviewObservation.IsReportable(recorded, ReviewRequestOutcome.TaskCreated, DomainId.New())
            .Should().BeTrue();
    }

    [Fact]
    public void Rediscovering_the_task_this_install_minted_keeps_the_recorded_outcome_and_owes_no_second_line()
    {
        Guid taskId = DomainId.New();
        ObservedReviewRequest recorded = new() { Outcome = ReviewRequestOutcome.TaskCreated, TaskId = taskId };

        ReviewRequestOutcome settled = AutoPrReviewObservation.Settle(
            recorded, ReviewRequestOutcome.AlreadyCovered, taskId);

        settled.Should().Be(ReviewRequestOutcome.TaskCreated,
            "the next sweep's fast path rediscovering the same task is the same fact restated");
        AutoPrReviewObservation.IsReportable(recorded, settled, taskId).Should().BeFalse();
    }

    /// <summary>
    /// The identical rediscovery case, for a parked mint (independent pre-PR review, cycle 1,
    /// adversarial lens, low): a non-member's request mints TaskCreatedParked, and the next
    /// sweep's fast path rediscovers that same still-Published task as AlreadyCovered. Without a
    /// matching Settle arm, AlreadyCovered would overwrite the parked outcome and spend an extra
    /// Info line saying nothing new happened.
    /// </summary>
    [Fact]
    public void Rediscovering_a_parked_mint_keeps_the_recorded_outcome_and_owes_no_second_line()
    {
        Guid taskId = DomainId.New();
        ObservedReviewRequest recorded = new() { Outcome = ReviewRequestOutcome.TaskCreatedParked, TaskId = taskId };

        ReviewRequestOutcome settled = AutoPrReviewObservation.Settle(
            recorded, ReviewRequestOutcome.AlreadyCovered, taskId);

        settled.Should().Be(ReviewRequestOutcome.TaskCreatedParked,
            "the next sweep's fast path rediscovering the same still-parked task is the same fact restated");
        AutoPrReviewObservation.IsReportable(recorded, settled, taskId).Should().BeFalse();
    }

    [Fact]
    public void A_different_task_covering_the_request_is_a_genuinely_different_answer()
    {
        ObservedReviewRequest recorded = new()
        {
            Outcome = ReviewRequestOutcome.TaskCreated,
            TaskId = DomainId.New(),
        };
        Guid adoptedByHand = DomainId.New();

        ReviewRequestOutcome settled = AutoPrReviewObservation.Settle(
            recorded, ReviewRequestOutcome.AlreadyCovered, adoptedByHand);

        settled.Should().Be(ReviewRequestOutcome.AlreadyCovered,
            "a human's own --from-pr adoption after this one was abandoned is not the task this install minted");
        AutoPrReviewObservation.IsReportable(recorded, settled, adoptedByHand).Should().BeTrue();
    }

    [Fact]
    public void A_held_request_that_finally_mints_is_not_settled_away()
    {
        ObservedReviewRequest recorded = new() { Outcome = ReviewRequestOutcome.HeldSettingOff };

        AutoPrReviewObservation.Settle(recorded, ReviewRequestOutcome.TaskCreated, DomainId.New())
            .Should().Be(ReviewRequestOutcome.TaskCreated);
    }

    /// <summary>
    /// One flaky <c>gh api graphql</c> call against a standing request does not rewrite what an
    /// earlier sweep actually observed (independent pre-PR review, cycle 1, adversarial lens):
    /// the timeline read answers a transient failure with the same null-field actor it answers an
    /// unresolvable pull request with, so without this the row's verdict would flip to
    /// "requested-at time could not be read" and back, spending two Info lines and a misleading
    /// status row on a failure that changed nothing about the request.
    /// </summary>
    [Fact]
    public void A_failed_timeline_read_keeps_the_verdict_a_row_with_an_observed_time_already_carries()
    {
        ObservedReviewRequest recorded = new()
        {
            Outcome = ReviewRequestOutcome.HeldBeforeCutoff,
            RequestedAt = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
        };

        ReviewRequestOutcome settled = AutoPrReviewObservation.Settle(
            recorded, ReviewRequestOutcome.HeldRequestTimeUnknown, null);

        settled.Should().Be(ReviewRequestOutcome.HeldBeforeCutoff);
        AutoPrReviewObservation.IsReportable(recorded, settled, null).Should().BeFalse(
            "a read that failed is not news about the request");
    }

    [Fact]
    public void A_request_with_no_observed_time_at_all_is_still_recorded_as_time_unknown()
    {
        ObservedReviewRequest recorded = new() { Outcome = ReviewRequestOutcome.HeldRequestTimeUnknown };

        AutoPrReviewObservation.Settle(recorded, ReviewRequestOutcome.HeldRequestTimeUnknown, null)
            .Should().Be(ReviewRequestOutcome.HeldRequestTimeUnknown,
                "nothing was ever observed here, so there is no earlier verdict to keep");
        AutoPrReviewObservation.Settle(null, ReviewRequestOutcome.HeldRequestTimeUnknown, null)
            .Should().Be(ReviewRequestOutcome.HeldRequestTimeUnknown,
                "a request first seen during the hiccup is honestly unknown");
    }

    [Fact]
    public void The_created_outcome_names_the_task_that_is_reviewing()
    {
        Guid taskId = DomainId.New();

        string described = AutoPrReviewObservation.Describe(ReviewRequestOutcome.TaskCreated, null, taskId);

        described.Should().Be($"task {DomainId.Short(taskId)} is created and reviewing");
    }

    [Fact]
    public void A_detail_rides_along_with_the_outcome_rather_than_on_a_second_line()
    {
        string described = AutoPrReviewObservation.Describe(
            ReviewRequestOutcome.TaskCreated, "started immediately, ceiling-exempt", DomainId.New());

        described.Should().EndWith("(started immediately, ceiling-exempt)");
    }

    [Fact]
    public void The_off_outcome_says_nothing_was_created_and_whose_move_it_is()
    {
        string described = AutoPrReviewObservation.Describe(ReviewRequestOutcome.HeldSettingOff, null, null);

        described.Should().Contain("nothing was created");
        described.Should().Contain("auto pr-review is off here");
        described.Should().Contain("by hand");
    }

    [Fact]
    public void The_no_backfill_outcome_names_the_guard_that_held_it()
    {
        string described = AutoPrReviewObservation.Describe(ReviewRequestOutcome.HeldBeforeCutoff, null, null);

        described.Should().Contain("predates");
        described.Should().Contain("cutoff");
        described.Should().Contain("never starts on its own");
    }

    [Fact]
    public void An_outcome_this_build_does_not_recognise_says_so_rather_than_guessing()
    {
        ReviewRequestOutcome fromANewerBuild = "SomethingElseEntirely";

        string described = AutoPrReviewObservation.Describe(fromANewerBuild, null, null);

        described.Should().Contain("does not recognise");
        described.Should().Contain("SomethingElseEntirely");
    }

    // The mint hold: on a fleet exactly one node mints on the first sweep and every other node
    // holds, measured off GitHub's own requested-at time so a node that wakes late mints at once.

    private static readonly Guid Lowest = Guid.Parse("01a00d41-0000-7000-8000-000000000001");
    private static readonly Guid Middle = Guid.Parse("01a04de3-0000-7000-8000-000000000002");
    private static readonly Guid Highest = Guid.Parse("01a09999-0000-7000-8000-000000000003");
    private static readonly DateTimeOffset RequestedAt = new(2026, 9, 24, 20, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(300);

    [Fact]
    public void The_lowest_ranked_node_mints_at_once_on_the_first_sweep_that_sees_the_request()
    {
        PeerHold? hold = AutoPrReviewObservation.DecideMintHold(
            Lowest, [Lowest, Middle, Highest], RequestedAt, RequestedAt.AddSeconds(5), Hold);

        hold.Should().BeNull("the leader mints exactly as a single-node install always has");
    }

    [Fact]
    public void A_follower_holds_for_the_leader_until_the_requested_at_time_is_older_than_the_hold()
    {
        PeerHold? hold = AutoPrReviewObservation.DecideMintHold(
            Middle, [Lowest, Middle, Highest], RequestedAt, RequestedAt.AddSeconds(5), Hold);

        hold.Should().Be(new PeerHold(Lowest, RequestedAt.AddSeconds(300)));
    }

    [Fact]
    public void A_follower_still_holds_on_the_last_second_of_the_hold()
    {
        AutoPrReviewObservation.DecideMintHold(
            Middle, [Lowest, Middle], RequestedAt, RequestedAt.AddSeconds(299), Hold)
            .Should().NotBeNull();
    }

    [Fact]
    public void A_follower_mints_once_the_requested_at_time_is_older_than_the_hold()
    {
        AutoPrReviewObservation.DecideMintHold(
            Middle, [Lowest, Middle], RequestedAt, RequestedAt.AddSeconds(300), Hold)
            .Should().BeNull("the leader had the whole hold to replicate a task and nothing covers the request");
    }

    [Fact]
    public void A_follower_that_wakes_late_mints_at_once_because_the_hold_is_measured_off_the_request_and_not_the_sighting()
    {
        AutoPrReviewObservation.DecideMintHold(
            Highest, [Lowest, Middle, Highest], RequestedAt, RequestedAt.AddHours(9), Hold)
            .Should().BeNull();
    }

    [Fact]
    public void An_explicit_zero_hold_means_this_node_never_defers_whatever_its_rank()
    {
        AutoPrReviewObservation.DecideMintHold(
            Highest, [Lowest, Middle, Highest], RequestedAt, RequestedAt, TimeSpan.Zero)
            .Should().BeNull();
    }

    [Fact]
    public void A_single_node_owner_is_its_own_leader()
    {
        AutoPrReviewObservation.DecideMintHold(Highest, [Highest], RequestedAt, RequestedAt, Hold).Should().BeNull();
    }

    [Fact]
    public void A_chain_that_names_no_node_at_all_reads_as_leader()
    {
        AutoPrReviewObservation.DecideMintHold(Highest, [], RequestedAt, RequestedAt, Hold).Should().BeNull();
    }

    [Fact]
    public void A_chain_nobody_could_read_reads_as_leader()
    {
        AutoPrReviewObservation.DecideMintHold(Highest, null, RequestedAt, RequestedAt, Hold)
            .Should().BeNull("no chain computed yet is today's behaviour, which is to mint");
    }

    [Fact]
    public void A_node_the_chain_does_not_list_still_ranks_among_the_nodes_it_does()
    {
        // This node is not in the enrolled set (revoked, or not yet vouched) and is lower than the
        // rest, so it is the leader by its own reading rather than deferring to a peer it outranks.
        AutoPrReviewObservation.DecideMintHold(Lowest, [Middle, Highest], RequestedAt, RequestedAt, Hold)
            .Should().BeNull();
    }

    [Fact]
    public void A_held_request_is_reported_when_it_starts_and_again_only_when_it_ends_in_a_mint_or_a_covering_task()
    {
        ObservedReviewRequest held = new() { Outcome = ReviewRequestOutcome.HeldForPeer };
        Guid task = DomainId.New();

        AutoPrReviewObservation.IsReportable(null, ReviewRequestOutcome.HeldForPeer, null).Should().BeTrue();
        AutoPrReviewObservation.IsReportable(held, ReviewRequestOutcome.HeldForPeer, null).Should().BeFalse();
        AutoPrReviewObservation.IsReportable(held, ReviewRequestOutcome.TaskCreated, task).Should().BeTrue();
        AutoPrReviewObservation.IsReportable(held, ReviewRequestOutcome.AlreadyCovered, task).Should().BeTrue();
    }

    [Fact]
    public void A_tick_that_cannot_read_the_time_keeps_a_recorded_hold_rather_than_flipping_to_needs_you()
    {
        ObservedReviewRequest held = new()
        {
            Outcome = ReviewRequestOutcome.HeldForPeer,
            RequestedAt = RequestedAt,
        };

        AutoPrReviewObservation.Settle(held, ReviewRequestOutcome.HeldRequestTimeUnknown, null)
            .Should().Be(ReviewRequestOutcome.HeldForPeer);
    }

    [Fact]
    public void The_held_outcome_survives_a_round_trip_through_a_stored_value_and_says_a_peer_mints_first()
    {
        ReviewRequestOutcome.FromInput("HeldForPeer").Should().Be(ReviewRequestOutcome.HeldForPeer);

        string described = AutoPrReviewObservation.Describe(ReviewRequestOutcome.HeldForPeer, "detail", null);

        described.Should().Contain("fleet peer ranks first");
        described.Should().Contain("(detail)");
    }

    // The membership gate (security review idea 6be68ee2, finding 1): a public repository needs
    // hall9k team membership before a review request or mention runs unattended; a private or
    // internal one keeps today's collaborator behaviour and needs no membership at all.

    private const long Member = 111;
    private const long OtherMember = 222;
    private const long Stranger = 999;

    [Fact]
    public void A_public_repositorys_member_runs()
    {
        // Also covers "account ids compare numerically so a renamed login still matches": the pure
        // function never sees a login at all, only the numeric id, so this is the identical
        // assertion for a member whose GitHub account was renamed between declaration and request.
        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: false, explicitSetting: null, authorAccountId: Member, memberAccountIds: [Member])
            .Should().Be(MembershipGateDecision.Run);
    }

    [Fact]
    public void A_public_repositorys_non_member_parks()
    {
        // Also covers a Bot author (an account no member declared parks exactly the same way,
        // whatever the size of the declared-member set) and a stranger's own COMMENT gating the
        // identical way a stranger's own pull request does — AttachMentionAsync's own dispatch
        // gate is the same pure function with the comment's author in place of the pull request's,
        // and neither reaches a seam or boundary this case does not already reach.
        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: false, explicitSetting: null, authorAccountId: Stranger, memberAccountIds: [Member])
            .Should().Be(MembershipGateDecision.Park);
    }

    [Fact]
    public void A_private_repositorys_non_member_runs_because_a_private_repository_needs_no_membership()
    {
        // Also covers an INTERNAL repository: gh repo view --json isPrivate reads true for
        // INTERNAL exactly as it does for PRIVATE (the caller's own visibility read never tells
        // the two apart), so the gate cannot either — the identical isPrivate: true input.
        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: true, explicitSetting: null, authorAccountId: Stranger, memberAccountIds: [Member])
            .Should().Be(MembershipGateDecision.Run);
    }

    [Fact]
    public void An_explicit_on_setting_overrides_a_private_repositorys_own_default_off()
    {
        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: true, explicitSetting: true, authorAccountId: Stranger, memberAccountIds: [Member])
            .Should().Be(MembershipGateDecision.Park);
    }

    [Fact]
    public void An_explicit_off_setting_overrides_a_public_repositorys_own_default_on()
    {
        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: false, explicitSetting: false, authorAccountId: Stranger, memberAccountIds: [Member])
            .Should().Be(MembershipGateDecision.Run);
    }

    [Fact]
    public void A_visibility_flip_flips_the_unset_default_because_it_is_computed_fresh_every_sweep()
    {
        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: false, explicitSetting: null, authorAccountId: Stranger, memberAccountIds: [Member])
            .Should().Be(MembershipGateDecision.Park, "public defaults the gate on");

        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: true, explicitSetting: null, authorAccountId: Stranger, memberAccountIds: [Member])
            .Should().Be(MembershipGateDecision.Run, "the same repository turned private defaults the gate off");
    }

    [Fact]
    public void A_failed_visibility_read_gates_on_and_parks_a_non_member_fail_closed_never_open()
    {
        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: null, explicitSetting: null, authorAccountId: Stranger, memberAccountIds: [Member])
            .Should().Be(MembershipGateDecision.Park);
    }

    [Fact]
    public void Unknown_membership_answers_unknown_rather_than_guessing_either_way()
    {
        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: false, explicitSetting: null, authorAccountId: Member, memberAccountIds: null)
            .Should().Be(MembershipGateDecision.Unknown,
                "this node has not yet computed the fleet's declared accounts; the engine skips and retries");
    }

    [Fact]
    public void A_private_repository_needs_no_membership_data_even_when_it_is_not_yet_known()
    {
        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: true, explicitSetting: null, authorAccountId: Member, memberAccountIds: null)
            .Should().Be(MembershipGateDecision.Run, "the gate is off here, so unknown membership is never asked about");
    }

    [Fact]
    public void A_member_with_no_declared_account_parks_because_nothing_proves_the_request_is_theirs()
    {
        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: false, explicitSetting: null, authorAccountId: Member, memberAccountIds: [])
            .Should().Be(MembershipGateDecision.Park);
    }

    [Fact]
    public void Two_accounts_declared_across_one_members_own_nodes_both_count()
    {
        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: false, explicitSetting: null, authorAccountId: Member, memberAccountIds: [Member, OtherMember])
            .Should().Be(MembershipGateDecision.Run);

        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: false, explicitSetting: null, authorAccountId: OtherMember, memberAccountIds: [Member, OtherMember])
            .Should().Be(MembershipGateDecision.Run);
    }
}
