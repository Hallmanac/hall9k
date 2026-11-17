using Hall9k.Domain.Features.Connection;

namespace Hall9k.Connectors.ClaudeCode;

/// <summary>What one check of a Claude Code config directory found.</summary>
public enum ClaudeAccountCheckOutcome
{
    Passed,
    DirectoryMissing,
    LoggedOut,
    OverrideCredential,
    WrongAccount,
    CouldNotRead,
}

/// <summary>
/// One check's outcome, the status it read (null when none was read), and <see cref="Message"/>: for a failure,
/// the text that names the directory and the fix for what was found, which <c>h9k status</c>, a refused CLI
/// spawn, <c>h9k connection add claude-code</c>, and <c>h9k doctor</c> all print as it stands.
/// </summary>
public sealed record ClaudeAccountCheckResult(
    ClaudeAccountCheckOutcome Outcome,
    ClaudeAuthStatus? Status,
    string Message)
{
    public bool Passed => Outcome == ClaudeAccountCheckOutcome.Passed;
}

/// <summary>
/// The rules both the connection check and the spawn check apply to what <c>claude auth status</c> reported,
/// and the fix line each failure names. Pure, so every outcome is testable without a process.
/// </summary>
public static class ClaudeAccountCheck
{
    /// <summary>
    /// Judges a read for <paramref name="directory"/>. With no expected account (registering a connection)
    /// any account that names itself passes; with one (a spawn), the email and org id must both match.
    /// </summary>
    public static ClaudeAccountCheckResult Evaluate(
        string directory, ClaudeAuthStatusRead read, string? expectedEmail, string? expectedOrganizationId,
        bool windows)
    {
        if (read.Status is not { } status)
        {
            return new ClaudeAccountCheckResult(
                ClaudeAccountCheckOutcome.CouldNotRead, null,
                $"Could not read the Claude Code login in {directory}: {read.Failure ?? "no status was printed."}");
        }

        if (!status.LoggedIn)
        {
            return new ClaudeAccountCheckResult(
                ClaudeAccountCheckOutcome.LoggedOut, status,
                $"The Claude Code config directory {directory} is not logged in. Run {LoginLine(directory, windows)}, "
                + "then /login inside it.");
        }

        if (!status.NamesAnAccount)
        {
            string source = status.OverrideSource;
            return new ClaudeAccountCheckResult(
                ClaudeAccountCheckOutcome.OverrideCredential, status,
                $"claude auth status for {directory} reports the credential from {source}, which outranks the "
                + "directory's own login, so the account cannot be confirmed and nothing is billed to it by mistake. "
                + $"Remove {source} from the environment h9k and h9kd run in (and from any apiKeyHelper or env block "
                + $"in {directory}'s settings.json), then retry.");
        }

        if (expectedEmail is not null
            && (!string.Equals(status.Email, expectedEmail, StringComparison.OrdinalIgnoreCase)
                || status.OrganizationId != expectedOrganizationId))
        {
            return new ClaudeAccountCheckResult(
                ClaudeAccountCheckOutcome.WrongAccount, status,
                $"The Claude Code config directory {directory} is logged in as {status.Email} "
                + $"({status.OrganizationName ?? status.OrganizationId}), not the account this connection recorded, "
                + $"{expectedEmail}. Log back in as {expectedEmail} with {LoginLine(directory, windows)}, then /login, "
                + $"or accept the new account with h9k connection add claude-code --config-dir {Quote(directory, windows)}.");
        }

        return new ClaudeAccountCheckResult(
            ClaudeAccountCheckOutcome.Passed, status,
            $"{directory} is logged in as {status.Email} ({status.OrganizationName ?? status.OrganizationId}).");
    }

    /// <summary>The failure for a directory that does not exist, found before claude runs, since claude would create it.</summary>
    public static ClaudeAccountCheckResult Missing(string directory, bool windows) =>
        new(
            ClaudeAccountCheckOutcome.DirectoryMissing, null,
            $"The Claude Code config directory {directory} does not exist. Create it and log in by running "
            + $"{LoginLine(directory, windows)}, then /login inside it.");

    /// <summary>
    /// The command that starts Claude Code under <paramref name="directory"/>, quoted so that any path
    /// survives: POSIX single quotes, or PowerShell's form on Windows.
    /// </summary>
    public static string LoginLine(string directory, bool windows) =>
        windows
            ? $"$env:{ClaudeConfigDirectory.Variable}={Quote(directory, windows: true)}; claude"
            : $"{ClaudeConfigDirectory.Variable}={Quote(directory, windows: false)} claude";

    /// <summary>A single-quoted literal for the shell a person pastes into: POSIX closes and escapes a quote, PowerShell doubles it.</summary>
    public static string Quote(string value, bool windows) =>
        windows
            ? $"'{value.Replace("'", "''")}'"
            : $"'{value.Replace("'", "'\\''")}'";
}
