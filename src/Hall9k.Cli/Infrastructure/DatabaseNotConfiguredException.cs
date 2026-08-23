namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// Thrown by <see cref="CliConfig.ConnectionString"/> when nothing is configured anywhere
/// in the precedence chain. Program's top-level catch turns this into a doctor check
/// rather than printing this exception's own message — the check re-derives the answer
/// fresh, including the "what is available" probe, rather than this type carrying it.
/// </summary>
public sealed class DatabaseNotConfiguredException() : Exception("No Hall9k connection string is configured.");
