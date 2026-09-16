namespace Hall9k.Domain.Features.Run;

/// <summary>
/// The three shapes a stack assessment run's structured verdict can take (task: a stacked
/// checkpoint that would park for a human on a git shape first dispatches a read-only assessment
/// run). Lands on <see cref="Events.StackAssessmentCompleted"/>, so a sealed record with static
/// instances rather than a plain enum (TASK-MODEL.md §8) — and a closed switch on the way back
/// from a string, unlike a looser value object such as <c>StackedCheckpoint</c>: this one is a
/// discriminator the daemon branches real control flow on (replay a branch, dispatch a fix
/// session, or park), so an unrecognized string collapses to <see cref="Unknown"/> rather than
/// round-tripping as its own never-matched instance.
/// </summary>
public sealed record StackAssessmentVerdictKind
{
    /// <summary>The branch already sits on the commit it should; nothing needs to move.</summary>
    public static readonly StackAssessmentVerdictKind Aligned = new("aligned");

    /// <summary>The branch's own commits need a mechanical replay from a named boundary commit onto a named onto commit.</summary>
    public static readonly StackAssessmentVerdictKind Replay = new("replay");

    /// <summary>
    /// Neither of the above could be said honestly, or the trailer that should have said so was
    /// missing or malformed — the two are indistinguishable to a caller acting on the verdict, by
    /// design (<see cref="Hall9k.Daemon.Review.StackAssessmentResultParser"/>'s own doc).
    /// </summary>
    public static readonly StackAssessmentVerdictKind Undecidable = new("undecidable");

    /// <summary>Not one of the three recognized values. Serializes as an empty string; never itself acted on as a verdict.</summary>
    public static readonly StackAssessmentVerdictKind Unknown = new("");

    public string Value { get; }

    private StackAssessmentVerdictKind(string value) => Value = value;

    public static implicit operator string(StackAssessmentVerdictKind? value) => value?.Value ?? string.Empty;

    public static implicit operator StackAssessmentVerdictKind(string? value) => value switch
    {
        "aligned" => Aligned,
        "replay" => Replay,
        "undecidable" => Undecidable,
        _ => Unknown,
    };

    public bool Equals(StackAssessmentVerdictKind? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;
}
