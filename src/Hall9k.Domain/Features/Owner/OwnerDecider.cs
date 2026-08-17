using Hall9k.Domain.Shared.Exceptions;

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
}
