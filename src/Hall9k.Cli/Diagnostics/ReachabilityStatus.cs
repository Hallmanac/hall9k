namespace Hall9k.Cli.Diagnostics;

/// <summary>
/// What a raw Npgsql connection attempt found — kept apart because "nothing is listening"
/// and "reached it, credentials rejected" are completely different fixes, and conflating
/// them is the usual sin the doctor check exists to avoid (Decisions Log #58, #73).
/// </summary>
public enum ReachabilityStatus
{
    Reachable,

    /// <summary>Nothing answered at host:port — a closed port, a wrong host, or a firewall.</summary>
    RefusedConnection,

    /// <summary>The server answered and rejected the username/password in the connection string.</summary>
    AuthenticationFailed,

    /// <summary>The server answered, authenticated, but the named database does not exist there.</summary>
    DatabaseMissing,

    /// <summary>Reached and authenticated, but something else Postgres said stopped the connection.</summary>
    OtherError,
}
