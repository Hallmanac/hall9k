using System.Text.RegularExpressions;

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
    /// Matches the elapsed-seconds figure <c>CrossProcessContainerGate.AcquireAsync</c>'s own
    /// periodic diagnostic embeds, e.g. "(842s elapsed, 4 max concurrent)".
    /// </summary>
    private static readonly Regex ElapsedSecondsPattern = new(@"\((?<seconds>\d+)s elapsed", RegexOptions.Compiled);

    /// <summary>
    /// The evidence file <c>CrossProcessContainerGate.AcquireAsync</c> writes is a short,
    /// fixed-shape diagnostic line — always well under this. <c>GateWaitEvidenceDirectoryEnvironmentVariable</c>
    /// is exported to the whole gate's process tree, i.e. to the agent's own test code too, so a
    /// file found there is skipped rather than read unbounded into memory and, from there, onto
    /// the durable <see cref="Hall9k.Domain.Features.Run.Events.GateRetried"/> event with no cap
    /// (adversarial review, this cycle).
    /// </summary>
    private const long MaxWaitEvidenceBytes = 4096;

    /// <summary>
    /// True when some file in the directory named by
    /// <see cref="GateWaitEvidenceDirectoryEnvironmentVariable"/> shows a wait that, at the
    /// moment of the kill, had already consumed most of <paramref name="gateTimeout"/> — proof
    /// that specific class never got a permit for nearly the whole run, not merely that
    /// <em>a</em> class happened to be queued at the instant of the kill. The earlier form of
    /// this check treated any file's mere presence as proof (conformance/adversarial review,
    /// cycle 1 origin), but during a busy tier — up to 14 classes contending for 4 fixed permits —
    /// several classes are queued behind the gate for most of the tier's own duration as pure
    /// ordinary operation, so a timeout caused by something else entirely (the agent's own test
    /// hanging, unrelated to the gate) would still find the directory non-empty and be
    /// misclassified as infrastructure (independent pre-PR review, this cycle). The evidence
    /// file's own embedded elapsed-seconds figure is the signal that actually discriminates the
    /// two: an ordinary queued wait resolves in well under the gate's own multi-minute budget as
    /// permits keep cycling, while only a wait that has consumed most of that budget — 80%,
    /// chosen as a bar clearly above ordinary queuing depth's own typical wait and clearly below
    /// the full timeout, so a kill landing a moment before the true full-budget mark is not missed
    /// on a technicality — indicates this run genuinely never made progress against the gate. The
    /// known gap this narrower bar accepts: a class whose own wait started only partway through
    /// the run and never resolved for its own remaining minority of the budget is not caught
    /// either, a deliberate trade against the far more common false positive above (never guess:
    /// AGENTS.md) — the gate's own captured console output still cannot answer this question
    /// either way, for the same buffering-and-entireProcessTree-kill reason
    /// <see cref="UnresolvedGateWaitExcerpt"/> already documents.
    /// </summary>
    public static bool IsUnresolvedGateWaitTimeout(string? gateWaitEvidenceDirectory, TimeSpan gateTimeout) =>
        UnresolvedGateWaitExcerpt(gateWaitEvidenceDirectory, gateTimeout) is not null;

    /// <summary>
    /// The excerpt to record alongside a true <see cref="IsUnresolvedGateWaitTimeout"/> result,
    /// the same reasoning as <see cref="MatchingExcerpt"/>: the caller records this next to the
    /// retry it explains, so the durable retry event says what triggered it rather than just that
    /// something did. Reads every evidence file present — never trusting enumeration order alone,
    /// since under a single dotnet-test-shaped gate's own process tree more than one class can be
    /// genuinely contended at kill time — and returns the first one whose own elapsed figure
    /// clears <paramref name="gateTimeout"/>'s own bar (see <see cref="IsUnresolvedGateWaitTimeout"/>).
    /// A file that vanishes or is rewritten between this method's own enumeration and its read —
    /// the class it belongs to acquired its permit and deleted it in the narrow window between the
    /// two — is skipped rather than left to throw out of the daemon's own timeout handler
    /// (adversarial review, this cycle), and a file over <see cref="MaxWaitEvidenceBytes"/> is
    /// skipped outright, unread.
    /// </summary>
    public static string? UnresolvedGateWaitExcerpt(string? gateWaitEvidenceDirectory, TimeSpan gateTimeout)
    {
        if (string.IsNullOrEmpty(gateWaitEvidenceDirectory) || !Directory.Exists(gateWaitEvidenceDirectory))
        {
            return null;
        }

        TimeSpan threshold = gateTimeout * 0.8;
        foreach (string evidenceFile in Directory.EnumerateFiles(gateWaitEvidenceDirectory))
        {
            string content;
            try
            {
                if (new FileInfo(evidenceFile).Length > MaxWaitEvidenceBytes)
                {
                    continue;
                }

                content = File.ReadAllText(evidenceFile);
            }
            catch (IOException)
            {
                continue;
            }

            Match match = ElapsedSecondsPattern.Match(content);
            if (match.Success
                && int.TryParse(match.Groups["seconds"].Value, out int seconds)
                && TimeSpan.FromSeconds(seconds) >= threshold)
            {
                return content.Trim();
            }
        }

        return null;
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
