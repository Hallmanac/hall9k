namespace Hall9k.Domain.Features.Tasks.Handlers;

/// <summary>
/// Whether an owner root may act on a task, by the receive gate's own rule
/// (<c>EventReplicationInbox.EvaluateTaskActVerdict</c>, the Member-role branch for the
/// pre-assignment-capable conditional acts): the root may when it equals the task's assigned root
/// or its holder root, and when the task has neither the creator's root decides. A pure function
/// over <see cref="TaskOwnerFacts"/>, so the CLI guard and any surface that wants to know which
/// levers are the viewer's to pull ask the identical question.
/// <para>
/// A fact that is present but cannot be resolved makes the owner unknown rather than falling
/// through to a later fact: an assignment recorded by an owner this node has never heard of must
/// not let the creator's root answer for it. The holder and the assignee are read in that order,
/// and only the first one present decides whether the owner is unknown, because a forced takeover
/// leaves the new holder's owner id on the assignment with its fingerprint cleared, and that
/// unresolvable id must not hide a holder this node can name.
/// </para>
/// </summary>
public static class TaskOwnerRule
{
    public const string HolderFact = "holder";
    public const string AssigneeFact = "assignee";
    public const string CreatorFact = "creator";

    public static TaskOwnerCheck Decide(string actingRoot, TaskOwnerFacts facts)
    {
        bool neitherHolderNorAssignee = facts.Holder.State == OwnerRootFactState.Absent
            && facts.Assigned.State == OwnerRootFactState.Absent;
        if (Matches(facts.Holder, actingRoot)
            || Matches(facts.Assigned, actingRoot)
            || (neitherHolderNorAssignee && Matches(facts.Creator, actingRoot)))
        {
            return TaskOwnerCheck.Permitted(actingRoot);
        }

        (OwnerRootFact owner, string ownerName) = (facts.Holder, facts.Assigned, facts.Creator) switch
        {
            ({ State: not OwnerRootFactState.Absent } holder, _, _) => (holder, HolderFact),
            (_, { State: not OwnerRootFactState.Absent } assigned, _) => (assigned, AssigneeFact),
            (_, _, var creator) => (creator, CreatorFact),
        };

        if (owner.State != OwnerRootFactState.Known)
        {
            return new TaskOwnerCheck(TaskOwnerOutcome.Unknown, null, null, ownerName);
        }

        string? assignee = ownerName == HolderFact
            && facts.Assigned.State == OwnerRootFactState.Known
            && facts.Assigned.RootFingerprint != owner.RootFingerprint
                ? facts.Assigned.RootFingerprint
                : null;
        return new TaskOwnerCheck(TaskOwnerOutcome.NotOwner, owner.RootFingerprint, assignee, null);
    }

    private static bool Matches(OwnerRootFact fact, string actingRoot) =>
        fact.State == OwnerRootFactState.Known && fact.RootFingerprint == actingRoot;
}
