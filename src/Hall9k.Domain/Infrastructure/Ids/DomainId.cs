using UUIDNext;

namespace Hall9k.Domain.Infrastructure.Ids;

/// <summary>
/// The single seam for domain ID generation: UUIDv7, time-ordered, Postgres-friendly
/// (PLAN.md Decisions Log #14). Never use Guid.NewGuid() in domain code.
/// </summary>
public static class DomainId
{
    public static Guid New() => Uuid.NewDatabaseFriendly(Database.PostgreSql);

    /// <summary>
    /// The eight hex characters a human types and reads back: the tail of the id, because
    /// UUIDv7's time-ordered prefix is what every id in one day shares, so the tail is where
    /// the entropy — and the uniqueness a fragment match needs — actually lives.
    /// </summary>
    public static string Short(Guid id) => id.ToString("N")[^8..];
}
