using UUIDNext;

namespace Hall9k.Domain.Infrastructure.Ids;

/// <summary>
/// The single seam for domain ID generation: UUIDv7, time-ordered, Postgres-friendly
/// (PLAN.md Decisions Log #14). Never use Guid.NewGuid() in domain code.
/// </summary>
public static class DomainId
{
    public static Guid New() => Uuid.NewDatabaseFriendly(Database.PostgreSql);
}
