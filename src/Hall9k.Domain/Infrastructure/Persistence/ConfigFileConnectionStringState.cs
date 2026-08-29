namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// The platform config file's own connection-string situation, independent of the
/// precedence chain <see cref="Hall9kDatabase.Resolve"/> walks — a caller warning about a
/// missing durable copy (an environment variable that will not survive into an autostart
/// registration, for instance) needs to say which of these four is actually true rather
/// than collapsing them into one "does not supply one" sentence: a file that does not
/// exist, one that exists but never got a <c>connectionString</c> key written into it
/// (<c>h9k config set</c> writes one with only its operating-settings section), one
/// that exists but fails to parse, and one that exists but this process could not even
/// read — each calls for a different fix.
/// </summary>
public enum ConfigFileConnectionStringState
{
    /// <summary><see cref="Hall9kDatabase.ConfigFile"/> does not exist at all.</summary>
    Missing,

    /// <summary><see cref="Hall9kDatabase.ConfigFile"/> exists but is not valid JSON.</summary>
    Malformed,

    /// <summary>
    /// <see cref="Hall9kDatabase.ConfigFile"/> exists and may well be valid JSON, but this process
    /// could not read it (permissions dropped by another account, an exclusive lock held
    /// elsewhere) — the fix is access to the file, not its syntax.
    /// </summary>
    Unreadable,

    /// <summary><see cref="Hall9kDatabase.ConfigFile"/> parses but carries no non-empty <c>connectionString</c> key.</summary>
    PresentWithoutConnectionString,

    /// <summary><see cref="Hall9kDatabase.ConfigFile"/> parses and carries a non-empty <c>connectionString</c>.</summary>
    Supplied,
}
