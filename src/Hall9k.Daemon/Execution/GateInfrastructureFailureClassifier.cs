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
    /// The environment variable name a dotnet-test-shaped gate's own process tree carries the
    /// cross-process container gate's wait-evidence directory under (PLAN.md §16 #132). Defined
    /// here rather than in the test project's own <c>CrossProcessContainerGate.AcquireAsync</c>
    /// (in <c>Hall9k.Tests</c>) because this classifier is the reader and the constant belongs
    /// beside the code that interprets it; <c>Hall9k.Tests</c> already references
    /// <c>Hall9k.Daemon</c> (the reverse direction is what AGENTS.md's reference graph forbids),
    /// so the writer reads this same constant rather than duplicating its literal value.
    /// </summary>
    public const string GateWaitEvidenceDirectoryEnvironmentVariable = "HALL9K_VERIFY_GATE_WAIT_DIR";

    /// <summary>
    /// True when the directory named by <see cref="GateWaitEvidenceDirectoryEnvironmentVariable"/>
    /// still holds a file at the moment a gate was killed — i.e. a class was genuinely queued on
    /// the cross-process container gate (PLAN.md §16 #132) when the timeout landed, not stuck for
    /// some other reason. The gate's own captured console output cannot answer this question: a
    /// wait line written from inside a dotnet-test-shaped gate's testhost is buffered by
    /// vstest.console and only ever relayed if vstest.console itself survives long enough to
    /// report the testhost's death, which <c>VerificationRunner</c>'s own
    /// <c>process.Kill(entireProcessTree: true)</c> does not allow (adversarial review, this
    /// cycle: reproduced against this repo's own package versions — the marker line never once
    /// reached the redirected log under a real entireProcessTree kill, only under a kill that left
    /// vstest.console alive a moment longer than its own testhost, which this platform's kill
    /// never does). A file written directly by the waiting process — created the moment
    /// contention is genuinely observed, deleted the moment a permit is acquired — carries no
    /// such gap: its mere presence after the kill needs no tail window or marker text, because
    /// nothing but an unresolved wait ever leaves one behind.
    /// </summary>
    public static bool IsUnresolvedGateWaitTimeout(string? gateWaitEvidenceDirectory) =>
        !string.IsNullOrEmpty(gateWaitEvidenceDirectory)
        && Directory.Exists(gateWaitEvidenceDirectory)
        && Directory.EnumerateFiles(gateWaitEvidenceDirectory).Any();

    /// <summary>
    /// The excerpt to record alongside a true <see cref="IsUnresolvedGateWaitTimeout"/> result,
    /// the same reasoning as <see cref="MatchingExcerpt"/>: the caller records this next to the
    /// retry it explains, so the durable retry event says what triggered it rather than just that
    /// something did. Reads whichever evidence file is first in enumeration order — under a
    /// single dotnet-test-shaped gate's own process tree there is ordinarily just the one class
    /// still genuinely contended at the moment of the kill, and any second one says the same
    /// thing about the same gate.
    /// </summary>
    public static string? UnresolvedGateWaitExcerpt(string? gateWaitEvidenceDirectory)
    {
        if (string.IsNullOrEmpty(gateWaitEvidenceDirectory) || !Directory.Exists(gateWaitEvidenceDirectory))
        {
            return null;
        }

        string? evidenceFile = Directory.EnumerateFiles(gateWaitEvidenceDirectory).FirstOrDefault();
        return evidenceFile is null ? null : File.ReadAllText(evidenceFile).Trim();
    }

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
