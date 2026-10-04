using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Domain.Features.Tasks.Handlers;

/// <summary>
/// The override decision for abandon, resolve and unassign when the acting root is not the task's
/// owner: an Owner-role member may still end or hand away another owner's task, but only by naming
/// whose it is and saying why. Pure, so every input (the rule's own answer, what <c>--holder</c>
/// said, the reason, the result of the Owner-role check) is a plain argument and the CLI only
/// gathers them.
/// </summary>
public static class TaskOwnerOverride
{
    /// <summary>The word <c>--holder</c> must be when the task's owner cannot be resolved at all.</summary>
    public const string UnknownHolder = "unknown";

    /// <summary>The shortest root fingerprint prefix <c>--holder</c> may use in place of a label.</summary>
    public const int MinimumFingerprintPrefixLength = 8;

    /// <param name="check">What <see cref="TaskOwnerRule.Decide"/> concluded for the acting root.</param>
    /// <param name="ownerLabel">The owner root's label, for matching <paramref name="holder"/> and for the message. Null when the owner is unknown.</param>
    /// <param name="assigneeLabel">The assignee's label when it is a different root than the owner.</param>
    /// <param name="roleCheck">Whether the acting node passed the Owner-role check, or <see cref="OwnerRoleCheck.NotChecked"/> when it has not been asked yet.</param>
    /// <param name="noun">What the refusal calls the thing acted on: an idea reuses this rule with the same words.</param>
    public static TaskOwnerOverrideDecision Decide(
        Guid taskId,
        string verb,
        TaskOwnerCheck check,
        string? ownerLabel,
        string? assigneeLabel,
        string? holder,
        string? reason,
        OwnerRoleCheck roleCheck,
        string noun = TaskOwnerRefusal.TaskNoun)
    {
        if (check.MayAct)
        {
            return TaskOwnerOverrideDecision.OwnAct;
        }

        string hint = $"An Owner-role member may {verb} it on that owner's behalf with --holder <name> and "
            + "--reason <text>, both required together.";
        string refusal = TaskOwnerRefusal.Describe(taskId, check, ownerLabel, assigneeLabel, noun);

        // A reason alone is not the override: abandon, resolve, conclude and archive all take --reason for
        // their own ending, so a plain command against another owner's work carries one and must still be
        // told about the override rather than only that the two flags go together.
        if (holder.IsBlank())
        {
            return TaskOwnerOverrideDecision.Refuse($"{refusal} {hint}");
        }

        if (reason.IsBlank())
        {
            return TaskOwnerOverrideDecision.Refuse(
                $"{refusal} --holder and --reason are required together: name whose {noun.ToLowerInvariant()} this is and say why.");
        }

        if (!NamesOwner(check, ownerLabel, holder.Trim()))
        {
            string expected = check.Outcome == TaskOwnerOutcome.Unknown
                ? $"the owner is unknown, so --holder must be the word {UnknownHolder}"
                : $"--holder must be the owner's label ({ownerLabel}) or at least {MinimumFingerprintPrefixLength} "
                    + "hex characters of its root fingerprint";
            return TaskOwnerOverrideDecision.Refuse($"{refusal} --holder '{holder.Trim()}' does not name it: {expected}.");
        }

        return roleCheck switch
        {
            { Outcome: OwnerRoleCheckOutcome.NotChecked } => TaskOwnerOverrideDecision.NeedsRoleCheck,
            { Outcome: OwnerRoleCheckOutcome.Failed } => TaskOwnerOverrideDecision.Refuse(
                $"{refusal} The override needs the Owner role on this project: {roleCheck.Detail}"),
            _ => TaskOwnerOverrideDecision.Override(check.OwnerRootFingerprint, reason.Trim()),
        };
    }

    private static bool NamesOwner(TaskOwnerCheck check, string? ownerLabel, string holder) =>
        check.Outcome switch
        {
            TaskOwnerOutcome.Unknown => holder.Equals(UnknownHolder, StringComparison.OrdinalIgnoreCase),
            _ => (ownerLabel is not null && holder.Equals(ownerLabel, StringComparison.OrdinalIgnoreCase))
                || IsFingerprintPrefix(check.OwnerRootFingerprint, holder),
        };

    private static bool IsFingerprintPrefix(string? rootFingerprint, string candidate) =>
        rootFingerprint is not null
        && candidate.Length >= MinimumFingerprintPrefixLength
        && candidate.All(Uri.IsHexDigit)
        && rootFingerprint.StartsWith(candidate, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The words every guarded command refuses with, so the override and the plain guard read the same.</summary>
public static class TaskOwnerRefusal
{
    public const string TaskNoun = "Task";

    public const string IdeaNoun = "Idea";

    public static string Describe(
        Guid taskId, TaskOwnerCheck check, string? ownerLabel, string? assigneeLabel, string noun = TaskNoun)
    {
        string task = $"{noun} {DomainId.Short(taskId)}";
        if (check.Outcome == TaskOwnerOutcome.Unknown)
        {
            return $"{task}'s owner is unknown on this node: its {check.UnknownFact} cannot be resolved to an owner, "
                + $"so this refuses to act on it rather than guess whose {noun.ToLowerInvariant()} it is.";
        }

        string assignee = assigneeLabel is null
            ? string.Empty
            : $" (assigned to {assigneeLabel})";
        return $"{task} belongs to {ownerLabel}{assignee}; only that owner's nodes may act on it.";
    }
}

/// <summary>The result of asking whether the acting node may override another owner's task.</summary>
public sealed record OwnerRoleCheck(OwnerRoleCheckOutcome Outcome, string? Detail)
{
    public static readonly OwnerRoleCheck NotChecked = new(OwnerRoleCheckOutcome.NotChecked, null);

    public static readonly OwnerRoleCheck Passed = new(OwnerRoleCheckOutcome.Passed, null);

    public static OwnerRoleCheck Failed(string detail) => new(OwnerRoleCheckOutcome.Failed, detail);
}

/// <summary>An unpersisted, in-process answer, so an enum rather than a closed vocabulary.</summary>
public enum OwnerRoleCheckOutcome
{
    NotChecked,
    Passed,
    Failed,
}

/// <summary>
/// What <see cref="TaskOwnerOverride.Decide"/> concluded. <see cref="OnBehalfOfRootFingerprint"/>
/// is null for an override of an unknown owner, and everything but <see cref="Reason"/> is empty
/// for the owner's own act.
/// </summary>
public sealed record TaskOwnerOverrideDecision(
    TaskOwnerOverrideOutcome Outcome, string? Message, string? OnBehalfOfRootFingerprint, string? Reason)
{
    public static readonly TaskOwnerOverrideDecision OwnAct = new(TaskOwnerOverrideOutcome.OwnAct, null, null, null);

    public static readonly TaskOwnerOverrideDecision NeedsRoleCheck = new(TaskOwnerOverrideOutcome.NeedsRoleCheck, null, null, null);

    public static TaskOwnerOverrideDecision Refuse(string message) => new(TaskOwnerOverrideOutcome.Refused, message, null, null);

    public static TaskOwnerOverrideDecision Override(string? onBehalfOfRootFingerprint, string reason) =>
        new(TaskOwnerOverrideOutcome.Override, null, onBehalfOfRootFingerprint, reason);
}

/// <summary>An unpersisted, in-process answer, so an enum rather than a closed vocabulary.</summary>
public enum TaskOwnerOverrideOutcome
{
    OwnAct,
    NeedsRoleCheck,
    Override,
    Refused,
}
