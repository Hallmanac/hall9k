using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Node;

public static class NodeDecider
{
    public static NodeRegistered Register(Guid id, Guid ownerId, string machineName, string operatingSystem, DateTimeOffset registeredAt)
    {
        if (ownerId == Guid.Empty)
        {
            throw new DomainValidationException("A node requires an owner — nodes belong to humans (PLAN.md §6.2).");
        }

        if (machineName.IsBlank())
        {
            throw new DomainValidationException("A node requires its machine name.");
        }

        return new NodeRegistered(id, ownerId, machineName, operatingSystem, registeredAt);
    }
}
