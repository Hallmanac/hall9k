using System.Text;

namespace Hall9k.Connectors.Processes;

/// <summary>
/// Reads a file some other live process may still hold open for writing — a gate's
/// shell-redirected output log, the cross-process container gate's own wait-evidence file — which
/// is precisely what <see cref="File.ReadAllText(string)"/> cannot do.
/// <para>
/// Origin incident (2026-09-09 and 2026-09-10, this repository's Windows node): two
/// <c>VerificationRunnerTests</c> gate-retry cases failed the full test gate of three review laps
/// whose own branches never touched the verification runner, and passed in isolation every time.
/// <c>File.ReadAllText</c> opens with <see cref="FileShare.Read"/>, which Windows refuses outright
/// while any other handle on the file carries write access, and the callers swallowed the
/// resulting <see cref="IOException"/> into a placeholder string. That placeholder carries no
/// infrastructure marker, so a gate that genuinely failed on the environment was recorded as the
/// agent's own broken work and denied the one retry it had coming. Measured on an idle Windows
/// host: one read in thirty taken immediately after <c>Process.Kill(entireProcessTree: true)</c>
/// hit the sharing violation, because <c>Kill</c> is asynchronous and the gate's own tree still
/// held the log at the moment the classifier read it.
/// </para>
/// <para>
/// <see cref="FileShare.ReadWrite"/> plus <see cref="FileShare.Delete"/> is the whole fix: it asks
/// the operating system to tolerate a concurrent writer, and a concurrent delete, rather than to
/// exclude one. Reading a file mid-write can of course return a partial trailing line; that is an
/// honest partial observation and is strictly better than reporting nothing at all, which is what
/// the sharing violation amounted to.
/// </para>
/// </summary>
public static class ShareTolerantFile
{
    /// <summary>
    /// The file's whole text, <see cref="string.Empty"/> when it does not exist, or <c>null</c>
    /// when it exists and genuinely could not be read. The three are deliberately distinguishable:
    /// a caller that classifies on this text has to be able to tell "the writer produced nothing"
    /// from "nobody could see what the writer produced" (AGENTS.md's never-guess rule), and the
    /// old behaviour of folding the second into a readable-looking placeholder is exactly the
    /// defect this type exists to close.
    /// <para>
    /// Which of the three a path falls into is decided by the open attempt itself, never by a
    /// <see cref="File.Exists(string)"/> pre-check: <c>File.Exists</c> answers <c>false</c> for
    /// every path it cannot inspect as well as for every path that is not there — a denied
    /// directory or file ACL, a path that is really a directory — so the pre-check reported
    /// "the writer produced nothing" for files nobody could look at, which is the exact
    /// conflation the <c>null</c> contract above exists to forbid (Copilot review, PR #313). It
    /// also closed over a gap of its own: a log deleted in the window between the check and the
    /// open answered <c>null</c>, so the same absent file classified differently depending on
    /// when it went missing. One open, and the exception it throws, distinguishes all three
    /// honestly — <see cref="FileNotFoundException"/> and
    /// <see cref="DirectoryNotFoundException"/> are the writer having produced nothing, and every
    /// other refusal is the unobserved case.
    /// </para>
    /// </summary>
    public static string? TryReadAllText(string path)
    {
        try
        {
            using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Ordered ahead of the IOException catch below deliberately: both of these derive
            // from it, and only these two mean the file genuinely is not there.
            return string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
