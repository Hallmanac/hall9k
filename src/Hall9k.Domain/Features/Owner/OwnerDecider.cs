using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Owner;

public static class OwnerDecider
{
    public static OwnerRegistered Register(Guid id, string name, string? email, DateTimeOffset registeredAt)
    {
        if (name.IsBlank())
        {
            throw new DomainValidationException("An owner requires a name — every node belongs to a human (PLAN.md §6.2).");
        }

        return new OwnerRegistered(id, name, email, registeredAt);
    }

    public static OwnerSettingsChanged ChangeSettings(
        OwnerAggregate owner,
        Optional<ReviewRerequestPolicy> reviewRerequest,
        DateTimeOffset changedAt)
    {
        // Unknown is a legal explicit value: it clears the owner's preference so the
        // project setting or the node default decides again (the CommitStyle convention).
        if (reviewRerequest.HasValue
            && reviewRerequest.Value is { } policy
            && policy != ReviewRerequestPolicy.Unknown
            && policy != ReviewRerequestPolicy.Enabled
            && policy != ReviewRerequestPolicy.Disabled)
        {
            throw new DomainValidationException(
                $"The review re-request policy must be {ReviewRerequestPolicy.Enabled} or "
                + $"{ReviewRerequestPolicy.Disabled} (whether closeout asks the reviewers for another "
                + "pass after a fix follow-up pushes, Decisions Log #62).");
        }

        return new OwnerSettingsChanged(owner.Id, reviewRerequest, changedAt);
    }
}
