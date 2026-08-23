namespace Hall9k.Cli.Diagnostics;

/// <summary>What was actually observed about one connection attempt — host and port parsed from
/// the connection string so the teaching message can name them, even on failure.</summary>
public sealed record ReachabilityReport(ReachabilityStatus Status, string Detail, string Host, int Port, string Database);
