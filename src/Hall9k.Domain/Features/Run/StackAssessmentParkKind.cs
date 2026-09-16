namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Which git shape a stacked checkpoint or the pre-final-pass rebase would otherwise have parked
/// for a human on, before its one read-only assessment run (task: a stacked checkpoint that would
/// park for a human on a git shape first dispatches a read-only assessment run). Lands on
/// <see cref="Events.StackAssessmentDispatched"/> and <see cref="Events.StackAssessmentCompleted"/>,
/// so a sealed record with static instances rather than an enum (TASK-MODEL.md §8), following
/// <c>StackedCheckpoint</c>'s own open-conversion shape: this names the shape for a prompt
/// placeholder and an audit trail, not a switch the daemon branches replay-or-park logic on — the
/// caller already knows which shape it is by which park it was about to take.
/// </summary>
public sealed record StackAssessmentParkKind
{
    /// <summary>A mechanical <c>git rebase --onto</c> attempt conflicted.</summary>
    public static readonly StackAssessmentParkKind ReplayConflict = new("ReplayConflict");

    /// <summary>The stacked parent's pull request merged into something other than the project's own base branch.</summary>
    public static readonly StackAssessmentParkKind ParentMergedElsewhere = new("ParentMergedElsewhere");

    /// <summary>The parent branch or its pull request could not be resolved to a live, reachable head (a dead or unresolvable parent).</summary>
    public static readonly StackAssessmentParkKind UndecidableBoundary = new("UndecidableBoundary");

    /// <summary>A Decisions Log placeholder's own renumbering guard declined a real collision it could not resolve mechanically.</summary>
    public static readonly StackAssessmentParkKind DecisionsLogPlaceholderCollision = new("DecisionsLogPlaceholderCollision");

    /// <summary>A force-with-lease push was refused because origin's tip is one this node's own history never accounted for.</summary>
    public static readonly StackAssessmentParkKind PushGuardRefusal = new("PushGuardRefusal");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly StackAssessmentParkKind Unknown = new("");

    public string Value { get; }

    private StackAssessmentParkKind(string value) => Value = value;

    /// <summary>What a human reads in a park message and a template placeholder — the value itself, already a plain phrase.</summary>
    public string Describe() => Value.IsNotBlank() ? Value : "an unrecorded park kind";

    public static implicit operator string(StackAssessmentParkKind? value) => value?.Value ?? string.Empty;

    public static implicit operator StackAssessmentParkKind(string? value) =>
        value.IsBlank() ? Unknown : new StackAssessmentParkKind(value);

    public bool Equals(StackAssessmentParkKind? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;
}
