namespace Hall9k.Daemon.Review;

/// <summary>
/// Recognizes a fix session's own final message naming a background task it is still waiting on
/// (task: a headless build, fix, or recovery session never ends its turn while a gate it started
/// is still running in the background) — the shape all three origin incidents took, each ending a
/// turn with a `dotnet test` run backgrounded rather than waited on in the foreground: "I'm
/// waiting on the background dotnet test run... to complete before finishing this session", "The
/// full dotnet test run is still running in the background", "Test suite is running in the
/// background; I've set a monitor to notify me." All three name the word "background" together
/// with a word for the task still being in flight — matched narrowly on both, per the
/// never-guess-at-unobserved-facts discipline (AGENTS.md), rather than on "background" alone: an
/// innocuous "for background context, this fixes X" carries the first word without the second and
/// must not be misread as a policy violation it never was.
/// <para>
/// Feeds <c>ReviewResultParser.ParseFixOutcome</c>'s fallback when no <c>RESOLUTION:</c> marker is
/// found, so a session that says this reads as
/// <see cref="Hall9k.Domain.Features.Run.ReviewFixOutcome.WaitingOnBackgroundGate"/> — a distinct,
/// named outcome — rather than the generic <c>(undeclared)</c> every other unmarked ending shares
/// (h9k task show and the run log both name the cause instead of reporting it as undeclared).
/// </para>
/// </summary>
public static class PendingBackgroundTaskParser
{
    private static readonly string[] StillInFlightWords =
        ["running", "wait", "monitor", "finish", "complete"];

    private static readonly char[] ClauseSeparators = ['.', '!', '?', ';', '\n'];

    /// <summary>
    /// Phrases that turn "background" from something still pending into something explicitly
    /// avoided or already resolved: "rather than backgrounding it", "instead of the background
    /// run", "no longer running in the background". Checked within the same clause as
    /// "background" itself, not the whole summary, so a negation elsewhere in a long summary
    /// cannot silently suppress a genuine pending-task clause later on.
    /// </summary>
    private static readonly string[] NegationCues =
        ["rather than", "instead of", "no longer", "not backgrounding", "without backgrounding", "never backgrounded"];

    public static bool NamesPendingBackgroundTask(string? summary)
    {
        if (summary is null)
        {
            return false;
        }

        foreach (string clause in summary.Split(ClauseSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!clause.Contains("background", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (NegationCues.Any(cue => clause.Contains(cue, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (StillInFlightWords.Any(word => clause.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
