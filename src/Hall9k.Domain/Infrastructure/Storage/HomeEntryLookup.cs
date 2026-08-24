using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The read-only half of a home entry's on-disk identity (backlog 48, backlog 49 cycle 2 review):
/// given an id, find whatever directory already carries it under a root such as <c>tasks/</c> or
/// <c>ideas/</c>, however it is currently named. Shared by <c>Hall9k.Daemon</c>'s
/// <c>HomeEntryWriter</c> (which also owns the write half — moving a stale directory onto its
/// current name, creating a fresh one when none exists) and by <c>Hall9k.Cli</c>'s idea commands,
/// which cannot reference <c>Hall9k.Daemon</c> but face the identical race: a directory resolved
/// from an entity's live text can name a slug the render sweep has not renamed onto yet, and
/// creating that not-yet-existing directory ahead of the sweep would strand the true,
/// already-populated one under its old name.
/// </summary>
public static class HomeEntryLookup
{
    /// <summary>
    /// A hidden per-directory marker carrying the entity's full id: the short id in a directory
    /// name is 32 bits, which two entities in one large project can collide on, so a prefix match
    /// alone is not enough to know a candidate directory actually belongs to the id being sought.
    /// </summary>
    public const string IdentityMarkerFileName = ".hall9k-id";

    /// <summary>
    /// The directory already on disk for this id, under whatever name it currently carries, if
    /// any. Callers that only need to place content under an entry's CURRENT directory — without
    /// inventing a name the render sweep has not renamed onto yet — resolve against this rather
    /// than a freshly computed name.
    /// </summary>
    public static string? FindExistingDirectory(string rootDirectory, Guid id) => FindExisting(rootDirectory, id);

    /// <summary>
    /// A directory already on disk for this id, optionally excluding one already-checked name.
    /// Matching is by short-id prefix first (cheap, and right in the overwhelming majority of
    /// cases) and then confirmed against the full id recorded in <see cref="IdentityMarkerFileName"/>,
    /// because the prefix alone is a 32-bit value and two unrelated entities in a project with
    /// enough history can share one — without the confirmation, a same-prefix directory belonging
    /// to a different task or idea would be matched as this one's. A same-prefix candidate with no
    /// marker, or a mismatched one, is not this entity's directory and is left alone.
    /// </summary>
    public static string? FindExisting(string rootDirectory, Guid id, string? excludingName = null)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return null;
        }

        string prefix = DomainId.Short(id) + "-";
        string fullId = id.ToString("N");
        return Directory.EnumerateDirectories(rootDirectory)
            .FirstOrDefault(directory =>
            {
                string name = Path.GetFileName(directory);
                if (!name.StartsWith(prefix, StringComparison.Ordinal)
                    || (excludingName is not null && string.Equals(name, excludingName, StringComparison.Ordinal)))
                {
                    return false;
                }

                string markerPath = Path.Combine(directory, IdentityMarkerFileName);
                return File.Exists(markerPath) && File.ReadAllText(markerPath).Trim() == fullId;
            });
    }
}
