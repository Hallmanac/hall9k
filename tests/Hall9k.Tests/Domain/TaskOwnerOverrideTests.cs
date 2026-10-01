using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using System.Text.Json;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="TaskOwnerOverride.Decide"/>: owner match, <c>--holder</c> match, the Owner-role
/// result and the reason, all as plain arguments, so none of it needs a database or a ledger.
/// </summary>
public sealed class TaskOwnerOverrideTests
{
    private const string Owner = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private static readonly Guid TaskId = DomainId.New();

    private static readonly TaskOwnerCheck NotMine = new(TaskOwnerOutcome.NotOwner, Owner, null, null);
    private static readonly TaskOwnerCheck UnknownOwner = new(TaskOwnerOutcome.Unknown, null, null, "creator");

    private static TaskOwnerOverrideDecision Decide(
        TaskOwnerCheck check, string? holder, string? reason, OwnerRoleCheck? role = null, string? label = "Taylor") =>
        TaskOwnerOverride.Decide(
            TaskId, "abandon", check, check.Outcome == TaskOwnerOutcome.Unknown ? null : label, null, holder, reason,
            role ?? OwnerRoleCheck.Passed);

    [Fact]
    public void An_owners_own_act_needs_no_override_and_records_none()
    {
        TaskOwnerOverrideDecision decision = TaskOwnerOverride.Decide(
            TaskId, "abandon", TaskOwnerCheck.Permitted(Owner), null, null, null, null, OwnerRoleCheck.NotChecked);

        decision.Outcome.Should().Be(TaskOwnerOverrideOutcome.OwnAct);
        decision.OnBehalfOfRootFingerprint.Should().BeNull();
        decision.Reason.Should().BeNull();
    }

    [Fact]
    public void A_plain_command_against_another_owners_task_refuses_naming_the_owner_and_the_override()
    {
        TaskOwnerOverrideDecision decision = Decide(NotMine, holder: null, reason: null);

        decision.Outcome.Should().Be(TaskOwnerOverrideOutcome.Refused);
        decision.Message.Should().Contain("Taylor").And.Contain("only that owner's nodes may act on it")
            .And.Contain("--holder").And.Contain("--reason");
    }

    [Theory]
    [InlineData("Taylor", null)]
    [InlineData(null, "closing it")]
    [InlineData("  ", "closing it")]
    public void The_holder_and_the_reason_are_required_together(string? holder, string? reason)
    {
        TaskOwnerOverrideDecision decision = Decide(NotMine, holder, reason);

        decision.Outcome.Should().Be(TaskOwnerOverrideOutcome.Refused);
        decision.Message.Should().Contain("required together");
    }

    [Theory]
    [InlineData("Taylor")]
    [InlineData("taylor")]
    [InlineData("  TAYLOR  ")]
    [InlineData("abcdef01")]
    [InlineData("ABCDEF0123456789")]
    public void The_holder_matches_the_owners_label_or_a_hex_prefix_of_the_root_of_at_least_eight_characters(string holder)
    {
        TaskOwnerOverrideDecision decision = Decide(NotMine, holder, "closing it", OwnerRoleCheck.NotChecked);

        decision.Outcome.Should().Be(TaskOwnerOverrideOutcome.NeedsRoleCheck);
    }

    [Theory]
    [InlineData("Morgan")]
    [InlineData("abcdef0")]
    [InlineData("abcdefgh")]
    [InlineData("bcdef012")]
    [InlineData("unknown")]
    public void The_holder_is_compared_only_against_this_tasks_owner(string holder)
    {
        TaskOwnerOverrideDecision decision = Decide(NotMine, holder, "closing it");

        decision.Outcome.Should().Be(TaskOwnerOverrideOutcome.Refused);
        decision.Message.Should().Contain("does not name it");
    }

    [Fact]
    public void An_unknown_owner_takes_only_the_word_unknown()
    {
        Decide(UnknownOwner, "unknown", "closing it", OwnerRoleCheck.NotChecked).Outcome
            .Should().Be(TaskOwnerOverrideOutcome.NeedsRoleCheck);

        TaskOwnerOverrideDecision wrong = Decide(UnknownOwner, "Taylor", "closing it");
        wrong.Outcome.Should().Be(TaskOwnerOverrideOutcome.Refused);
        wrong.Message.Should().Contain("creator").And.Contain("must be the word unknown");
    }

    [Fact]
    public void A_failed_role_check_refuses_and_carries_why()
    {
        TaskOwnerOverrideDecision decision = Decide(NotMine, "Taylor", "closing it", OwnerRoleCheck.Failed("no Owner role here"));

        decision.Outcome.Should().Be(TaskOwnerOverrideOutcome.Refused);
        decision.Message.Should().Contain("no Owner role here");
    }

    [Fact]
    public void A_passed_role_check_grants_the_override_recording_the_root_and_the_trimmed_reason()
    {
        TaskOwnerOverrideDecision decision = Decide(NotMine, "Taylor", "  their node is gone  ");

        decision.Outcome.Should().Be(TaskOwnerOverrideOutcome.Override);
        decision.OnBehalfOfRootFingerprint.Should().Be(Owner);
        decision.Reason.Should().Be("their node is gone");
    }

    [Fact]
    public void An_override_of_an_unknown_owner_records_no_root()
    {
        TaskOwnerOverrideDecision decision = Decide(UnknownOwner, "unknown", "closing it");

        decision.Outcome.Should().Be(TaskOwnerOverrideOutcome.Override);
        decision.OnBehalfOfRootFingerprint.Should().BeNull();
    }

    [Fact]
    public void The_role_check_is_not_asked_for_until_everything_cheaper_agrees()
    {
        Decide(NotMine, "Morgan", "closing it", OwnerRoleCheck.NotChecked).Outcome.Should().Be(TaskOwnerOverrideOutcome.Refused);
        Decide(NotMine, "Taylor", null, OwnerRoleCheck.NotChecked).Outcome.Should().Be(TaskOwnerOverrideOutcome.Refused);
    }

    /// <summary>An event written before the two fields existed must read back as an owner's own act.</summary>
    [Fact]
    public void An_abandon_resolve_or_unassign_written_without_the_override_fields_replays_unchanged()
    {
        Guid id = DomainId.New();
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        string abandoned = $$"""{"id":"{{id}}","reason":"old","abandonedAt":"2026-09-01T00:00:00+00:00","abandonedByOwnerId":"{{id}}"}""";
        string resolved = $$"""{"id":"{{id}}","reason":"old","pullRequestUrl":null,"resolvedAt":"2026-09-01T00:00:00+00:00","resolvedByOwnerId":"{{id}}"}""";
        string unassigned = $$"""{"id":"{{id}}","reason":"old","unassignedAt":"2026-09-01T00:00:00+00:00","unassignedByOwnerId":"{{id}}"}""";

        TaskAbandoned a = JsonSerializer.Deserialize<TaskAbandoned>(abandoned, options)!;
        TaskResolved r = JsonSerializer.Deserialize<TaskResolved>(resolved, options)!;
        TaskUnassigned u = JsonSerializer.Deserialize<TaskUnassigned>(unassigned, options)!;

        (a.OnBehalfOfOwnerRootFingerprint, a.OverrideReason).Should().Be((null, null));
        (r.OnBehalfOfOwnerRootFingerprint, r.OverrideReason).Should().Be((null, null));
        (u.OnBehalfOfOwnerRootFingerprint, u.OverrideReason).Should().Be((null, null));

        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(id, DomainId.New(), "Old", ["x"], TaskType.Chore, null, null, null, a.AbandonedAt, id));
        task.Apply(a);
        task.State.Should().Be(TaskState.Abandoned);
    }

    [Fact]
    public void An_owners_own_act_from_the_decider_leaves_the_override_fields_empty()
    {
        Guid id = DomainId.New();
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(id, DomainId.New(), "Mine", ["x"], TaskType.Chore, null, null, null, DateTimeOffset.UtcNow, id));

        TaskAbandoned abandoned = TaskDecider.Abandon(task, "done", DateTimeOffset.UtcNow, id);

        abandoned.OnBehalfOfOwnerRootFingerprint.Should().BeNull();
        abandoned.OverrideReason.Should().BeNull();
    }
}
