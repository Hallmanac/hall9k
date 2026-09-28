using System.Text.RegularExpressions;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// Which shell commands turn a pr-review session's own read-only <c>git diff</c>/<c>git log</c>
/// allowance into a write outside the checkout (independent pre-PR review, cycle 1, both lenses):
/// both subcommands take an <c>--output=&lt;path&gt;</c> flag that redirects git's own output to
/// any path the process can write, and <see cref="ClaudeSettingsFile.PrReviewAllowedTools"/>'s
/// prefix rules (<c>Bash(git diff:*)</c>, <c>Bash(git log:*)</c>) allow it through: a prefix rule
/// matches the command as spelled and "git diff --output=/Users/owner/.zshrc" still starts with
/// "git diff". Verified in a throwaway repository: <c>git log -1 --format='format:echo pwned'
/// --output=&lt;path&gt;</c> wrote exactly the formatted text to that path, and <c>git diff
/// --output=</c> wrote its file the identical way.
/// <para>
/// Matched on the command text, the same terms <see cref="ReviewThreadReplyRoutes"/> states for
/// its own routes: a <c>PreToolUse</c> hook is given nothing else, so recognition has to survive
/// quoting and <c>bash -c</c> wrapping rather than assume a particular shell parses the line.
/// </para>
/// </summary>
public static class GitReadOnlyGuardRoutes
{
    private static readonly Regex GitLogOrDiff = new(
        @"\bgit\s+(log|diff)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// <c>--output</c> is the only flag either subcommand has for this (there is no short form),
    /// spelled with <c>=</c>, a space, or as the command's last token — the three ways an operator
    /// or an injected session would write it.
    /// </summary>
    private static readonly Regex OutputFlag = new(
        @"--output(=|\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Whether this command reaches <c>git diff</c> or <c>git log</c> with an <c>--output</c> flag
    /// — the one way either subcommand writes outside the checkout despite being otherwise
    /// read-only. False for everything else, including an ordinary <c>git diff</c>/<c>git log</c>
    /// with no such flag, and for a command that merely quotes "--output" in a search pattern or a
    /// commit message.
    /// </summary>
    public static bool WritesOutsideTheCheckout(string? command) =>
        command.IsNotBlank() && GitLogOrDiff.IsMatch(command) && OutputFlag.IsMatch(command);

    /// <summary>
    /// What the session is told when the guard refuses, in the shape a refusal has to take: name
    /// the rule and say what to do instead. An agent that cannot self-correct from the message
    /// will simply retry the same command.
    /// </summary>
    public const string RefusalReason =
        "git diff and git log are read-only in this session, and --output writes their formatted "
        + "text to an arbitrary path outside this checkout — refused regardless of the path named. "
        + "Read the command's own stdout instead; nothing about a pr-review lens needs a file on disk.";
}
