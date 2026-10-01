namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// What <see cref="TaskOwnerRule.Decide"/> concluded. <see cref="OwnerRootFingerprint"/> is the
/// root the task is treated as belonging to when the acting root may not act: the holder's, else
/// the assignee's, else the creator's. <see cref="AssigneeRootFingerprint"/> is set only when the
/// assignee is a different known root than that owner. <see cref="UnknownFact"/> names the fact
/// that could not be resolved when <see cref="Outcome"/> is <see cref="TaskOwnerOutcome.Unknown"/>.
/// </summary>
public sealed record TaskOwnerCheck(
    TaskOwnerOutcome Outcome,
    string? OwnerRootFingerprint,
    string? AssigneeRootFingerprint,
    string? UnknownFact)
{
    public bool MayAct => Outcome == TaskOwnerOutcome.MayAct;

    public static TaskOwnerCheck Permitted(string actingRoot) => new(TaskOwnerOutcome.MayAct, actingRoot, null, null);
}

/// <summary>An unpersisted, in-process answer, so an enum rather than a closed vocabulary.</summary>
public enum TaskOwnerOutcome
{
    MayAct,
    NotOwner,
    Unknown,
}
