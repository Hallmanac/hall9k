namespace Hall9k.Daemon;

/// <summary>The resolved Postgres connection string (Aspire-injected or the shared default).</summary>
public sealed record DaemonConnection(string ConnectionString);
