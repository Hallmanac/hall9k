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

    // The cross-process container gate's own wait line (CrossProcessContainerGate.AcquireAsync,
    // PLAN.md §16 #131) repeats every five seconds for as long as a class is genuinely queued on
    // it, and ordinary same-process contention prints it on nearly every run regardless of
    // outcome (independent pre-PR review, cycle 1) — so unlike the markers above, its presence
    // anywhere in a long-running gate's output says nothing about why the run eventually died.
    // Deliberately not folded into ConnectionFailureMarkers/IsInfrastructureFailure for that
    // reason: that scan also classifies an ordinary non-zero exit (a real test failure), and a
    // gate that already finished has necessarily gotten past the wait, so the line's presence
    // there is stale history from earlier contention, never the cause of the failure — treating
    // it as a marker there would misclassify most genuine test failures the moment any class in
    // the run ever queued on the gate at all. This is checked, and only checked, on
    // VerificationRunner's own timeout path: there, and only there, "the gate's own wait line is
    // the last thing this process said before being killed" is the literal, recognizable shape
    // that means the timeout landed on ordinary cross-process contention, not on the agent's own
    // work (adversarial review, cycle 1).
    private const string GateWaitMarker = "Waiting on cross-process container gate";

    // Wide enough to comfortably hold the wait line itself (well under 200 characters) plus any
    // interleaved output from other classes still running in parallel at the moment of the kill,
    // narrow enough that a wait line from minutes earlier, long superseded by real progress,
    // cannot still be sitting in the window.
    private const int GateWaitTailWindowSize = 2_000;

    /// <summary>
    /// True when a timed-out gate's own trailing output still shows it queued on
    /// <see cref="Hall9k.Tests.Integration.CrossProcessContainerGate"/> — i.e. the timeout landed
    /// while a class was genuinely waiting for a permit, not stuck for some other reason. Callers
    /// check this only alongside <see cref="IsInfrastructureFailure"/> on the timeout path, never
    /// on a gate that exited on its own (see <see cref="GateWaitMarker"/>'s own comment for why).
    /// </summary>
    public static bool IsUnresolvedGateWaitTimeout(string? timeoutOutput)
    {
        if (string.IsNullOrEmpty(timeoutOutput))
        {
            return false;
        }

        string tail = timeoutOutput.Length <= GateWaitTailWindowSize
            ? timeoutOutput
            : timeoutOutput[^GateWaitTailWindowSize..];

        return tail.Contains(GateWaitMarker, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bounded excerpt to record alongside a true <see cref="IsUnresolvedGateWaitTimeout"/>
    /// result, the same reasoning as <see cref="MatchingExcerpt"/>: the caller records this next
    /// to the retry it explains, so the durable retry event says what triggered it rather than
    /// just that something did. Anchored to the last occurrence of the marker, since that is the
    /// one <see cref="IsUnresolvedGateWaitTimeout"/> itself found in the trailing window.
    /// </summary>
    public static string? UnresolvedGateWaitExcerpt(string? timeoutOutput)
    {
        if (timeoutOutput is null)
        {
            return null;
        }

        int index = timeoutOutput.LastIndexOf(GateWaitMarker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        int start = Math.Max(0, index - 50);
        int end = Math.Min(timeoutOutput.Length, index + GateWaitMarker.Length + 250);
        return timeoutOutput[start..end].Trim();
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
