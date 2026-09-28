using System.Text;
using System.Text.RegularExpressions;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// Which shell commands turn a pr-review session's own read-only <c>git diff</c>/<c>git log</c>
/// allowance into an escape outside the checkout (independent pre-PR review, cycle 1, both
/// lenses), in either direction:
/// <list type="bullet">
/// <item>a write — both subcommands take an <c>--output=&lt;path&gt;</c> flag that redirects
/// git's own output to any path the process can write. Verified in a throwaway repository:
/// <c>git log -1 --format='format:echo pwned' --output=&lt;path&gt;</c> wrote exactly the
/// formatted text to that path, and <c>git diff --output=</c> wrote its file the identical
/// way.</item>
/// <item>a read — either subcommand silently switches to <c>--no-index</c> mode, comparing two
/// filesystem paths instead of the checkout's own history, the moment it is handed a path outside
/// the working tree (lesson f059f669). <c>git diff /dev/null ~/.config/gh/hosts.yml</c> prints
/// that file's content as an ordinary diff, with no flag naming the mode at all.</item>
/// </list>
/// and <see cref="ClaudeSettingsFile.PrReviewAllowedTools"/>'s prefix rules (<c>Bash(git
/// diff:*)</c>, <c>Bash(git log:*)</c>) allow either through: a prefix rule matches the command as
/// spelled, and both "git diff --output=/Users/owner/.zshrc" and "git diff /dev/null
/// ~/.config/gh/hosts.yml" still start with "git diff".
/// <para>
/// Matched on the command text, the same terms <see cref="ReviewThreadReplyRoutes"/> states for
/// its own routes: a <c>PreToolUse</c> hook is given nothing else, so recognition has to survive
/// quoting and <c>bash -c</c> wrapping rather than assume a particular shell parses the line. A
/// single-quoted or backslash-escaped flag (<c>'--output'=&lt;path&gt;</c>,
/// <c>--outpu\t=&lt;path&gt;</c>) reassembles into the real flag once the shell removes the quote
/// or the backslash, and this recognizer un-escapes the identical way before matching — verified
/// against all three spellings in a throwaway repository (independent pre-PR review, cycle 1,
/// adversarial lens): each one wrote its file exactly as the unquoted form does. A quoted run of
/// text that contains whitespace — an ordinary commit message or <c>--format</c> string — is
/// blanked out instead, so naming either flag inside one still runs.
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
    /// <c>--no-index</c> is the one flag that forces the filesystem-diff mode this guard also has
    /// to refuse (see <see cref="AbsolutePathArgument"/> for the case where the mode is entered
    /// implicitly, with no flag at all).
    /// </summary>
    private static readonly Regex NoIndexFlag = new(
        @"--no-index\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// An argument that names an absolute filesystem path — the shape <c>--no-index</c> mode
    /// triggers on even with no flag naming it (lesson f059f669). Nothing this project's own
    /// documented pr-review usage runs (<c>review-mechanics.md</c>'s own ordinary-diff-range,
    /// <c>origin/&lt;base&gt;...HEAD</c>, or a bare <c>git log</c>) ever needs an absolute path
    /// argument, so refusing every one of them costs a lens nothing.
    /// </summary>
    private static readonly Regex AbsolutePathArgument = new(
        @"(?:^|\s)(?:/|~|[A-Za-z]:[\\/])\S*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Whether this command reaches <c>git diff</c> or <c>git log</c> in a way that escapes the
    /// checkout — an <c>--output</c> write, or a <c>--no-index</c> read triggered by an explicit
    /// flag or an absolute-path argument. False for everything else, including an ordinary
    /// <c>git diff</c>/<c>git log</c> with none of these, and for a command that merely quotes one
    /// of these terms in a search pattern or a commit message.
    /// </summary>
    public static bool EscapesTheCheckout(string? command)
    {
        if (command.IsBlank())
        {
            return false;
        }

        string unescaped = UnescapeShellQuoting(command);
        return GitLogOrDiff.IsMatch(unescaped)
            && (OutputFlag.IsMatch(unescaped) || NoIndexFlag.IsMatch(unescaped)
                || AbsolutePathArgument.IsMatch(unescaped));
    }

    /// <summary>
    /// Reverses the two shell tricks that reassemble a refused flag out of pieces this class's own
    /// regexes would otherwise miss (independent pre-PR review, cycle 1, adversarial lens):
    /// splitting a flag across a quote (<c>'--output'=&lt;path&gt;</c>) and escaping one of its
    /// characters (<c>--outpu\t=&lt;path&gt;</c>). A quoted run of text that itself contains
    /// whitespace is blanked out instead of unescaped — that shape is an ordinary multi-word
    /// argument (a commit message, a <c>--format</c> string), not a flag split across a quote
    /// boundary, and unescaping it in place would read a flag's own name, or an absolute path, out
    /// of a value that never reaches git as one (a regression this method's own tests pin: naming
    /// either term inside a quoted commit message must keep running).
    /// </summary>
    private static string UnescapeShellQuoting(string command)
    {
        StringBuilder result = new(command.Length);
        int index = 0;
        while (index < command.Length)
        {
            char current = command[index];
            if (current is '\'' or '"')
            {
                int closingIndex = command.IndexOf(current, index + 1);
                if (closingIndex < 0)
                {
                    result.Append(command, index, command.Length - index);
                    break;
                }

                string quoted = command[(index + 1)..closingIndex];
                result.Append(quoted.Contains(' ') || quoted.Contains('\t')
                    ? new string('#', closingIndex - index + 1)
                    : quoted);
                index = closingIndex + 1;
                continue;
            }

            if (current == '\\' && index + 1 < command.Length)
            {
                result.Append(command[index + 1]);
                index += 2;
                continue;
            }

            result.Append(current);
            index++;
        }

        return result.ToString();
    }

    /// <summary>
    /// What the session is told when the guard refuses, in the shape a refusal has to take: name
    /// the rule and say what to do instead. An agent that cannot self-correct from the message
    /// will simply retry the same command.
    /// </summary>
    public const string RefusalReason =
        "git diff and git log are read-only in this session: --output writes their formatted text "
        + "to an arbitrary path outside this checkout, and --no-index (explicit, or entered "
        + "implicitly by naming an absolute path) reads one instead of the checkout's own history "
        + "— both refused regardless of the path named. Read the command's own stdout instead; "
        + "nothing about a pr-review lens needs a file on disk or a path outside the checkout.";
}
