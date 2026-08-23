namespace Hall9k.Daemon.Execution;

/// <summary>
/// Tells a gate failure caused by the verification environment itself — Postgres refusing or
/// dropping a connection, Testcontainers failing to bring a container up, the SSLRequest
/// handshake mismatch a container answers before Postgres inside it is ready — apart from a
/// gate that failed because the agent's work is actually broken (backlog 53).
/// <para>
/// The never-guess rule, applied exactly as backlog 40 applied it to budget exhaustion:
/// classification fires only on the literal, recognizable shape of a connection-class failure.
/// A test's own assertion output mentioning "connection" in passing does not carry any of
/// these markers and stays a real failure.
/// </para>
/// </summary>
public static class GateInfrastructureFailureClassifier
{
    private static readonly string[] ConnectionFailureMarkers =
    [
        // Npgsql connection refused / reset / timeout. Npgsql.PostgresException is
        // deliberately not a marker here: per Npgsql's own docs it is thrown whenever
        // "the PostgreSQL backend reports errors" — a bad migration or a unique-constraint
        // violation throws it too, and that is the agent's own work, not the environment
        // (adversarial review, cycle 1).
        "Npgsql.NpgsqlException",
        "Connection refused",
        "Connection reset by peer",
        "Exception while reading from stream",
        "Failed to connect to",
        "Timeout during handshake",
        "Timeout while reading from stream",
        // The SSLRequest handshake mismatch (origin incident, 2026-08-23): the container
        // answered before Postgres inside it was actually ready for the protocol.
        "unknown response H for SSLRequest",
        // Testcontainers itself failing to bring the container up.
        "DotNet.Testcontainers",
        "Docker.DotNet.DockerApiException",
    ];

    public static bool IsInfrastructureFailure(string? gateOutput) =>
        gateOutput is not null
        && ConnectionFailureMarkers.Any(marker => gateOutput.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A bounded excerpt around the first marker that classified this output as infrastructure,
    /// null when nothing matches. The caller records this alongside the retry it explains: the
    /// recorded gate summary is truncated to its last 400 characters for size, and a marker
    /// logged early in a large run's output would otherwise leave the durable retry event with
    /// no evidence of what triggered the classification (PR #36's Copilot review).
    /// </summary>
    public static string? MatchingExcerpt(string? gateOutput)
    {
        if (gateOutput is null)
        {
            return null;
        }

        foreach (string marker in ConnectionFailureMarkers)
        {
            int index = gateOutput.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            int start = Math.Max(0, index - 50);
            int end = Math.Min(gateOutput.Length, index + marker.Length + 250);
            return gateOutput[start..end].Trim();
        }

        return null;
    }
}
