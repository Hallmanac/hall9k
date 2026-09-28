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
/// single-quoted, double-quoted, ANSI-C-quoted, or backslash-escaped flag — split across a quote
/// boundary or reassembled whole inside one — turns into the real flag once the shell removes the
/// quoting, and this recognizer reassembles whole shell words the identical way before matching
/// (independent pre-PR review, cycles 1 through 4; each spelling verified in a throwaway
/// repository: it wrote or read its file exactly as the unquoted form does). A word that is not
/// itself the refused flag or path once reassembled — an ordinary commit message or
/// <c>--format</c> string that merely names one inside a longer value — is blanked out instead, so
/// naming either term inside one still runs.
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
    /// Reverses the shell tricks that reassemble a refused flag or path out of pieces this class's
    /// own regexes would otherwise miss, by reasoning about whole shell words rather than isolated
    /// quoted spans (independent pre-PR review, cycles 1 through 4). A single shell word can mix
    /// unquoted text, <c>'...'</c>, <c>"..."</c>, <c>$'...'</c> ANSI-C quoting, and backslash escapes
    /// with no whitespace between the pieces — <c>--out'put=/tmp/my file.txt'</c>,
    /// <c>--out"put=$HOME/my file"</c>, and <c>--outp$'ut=/tmp/my file.txt'</c> are each one word
    /// that bash reassembles into <c>--output=/tmp/my file.txt</c> — so this method joins every
    /// adjacent piece of a word (decoding each quote or escape the way bash does) before judging it,
    /// and splits only on unquoted whitespace. A word untouched by any quoting or escaping — the
    /// overwhelming majority, including the literal <c>git</c>/<c>diff</c>/<c>log</c> tokens this
    /// class's other regexes match on — passes through unchanged. A word that <em>was</em> touched
    /// is judged as a whole: if its reassembled text opens with a refused term
    /// (<see cref="BeginsWithARefusedTerm"/> — <c>--output</c>, <c>--no-index</c>, or an absolute
    /// path), the reassembled text is substituted in so the flag regexes see it exactly as git
    /// would; otherwise the whole word is blanked out. That covers the ordinary multi-word argument
    /// (a commit message, a <c>--format</c> string) that merely names a refused term somewhere
    /// inside itself — substituting it in place would read a flag's own name, or an absolute path,
    /// out of a value that never reaches git as one (a regression this method's own tests pin) — and
    /// it also closes the gap a per-span check left open: judging only the decoded <c>$'...'</c>
    /// span itself, rather than the whole word around it, missed <c>--outp$'ut=/tmp/my file.txt'</c>
    /// because the refused term only appears once the unquoted prefix and the quoted remainder are
    /// joined (cycle 4, human review).
    /// </summary>
    private static string UnescapeShellQuoting(string command)
    {
        StringBuilder result = new(command.Length);
        int index = 0;
        while (index < command.Length)
        {
            if (char.IsWhiteSpace(command[index]))
            {
                result.Append(command[index]);
                index++;
                continue;
            }

            int wordStart = index;
            StringBuilder decodedWord = new();
            bool wordHasQuoting = false;
            while (index < command.Length && !char.IsWhiteSpace(command[index]))
            {
                char current = command[index];
                if (current == '$' && index + 1 < command.Length && command[index + 1] == '\'')
                {
                    wordHasQuoting = true;
                    if (!TryConsumeAnsiCQuoted(command, index, out string decoded, out int nextIndex))
                    {
                        decodedWord.Append(command, index, command.Length - index);
                        index = command.Length;
                        break;
                    }

                    decodedWord.Append(decoded);
                    index = nextIndex;
                    continue;
                }

                if (current is '\'' or '"')
                {
                    wordHasQuoting = true;
                    int closingIndex = command.IndexOf(current, index + 1);
                    if (closingIndex < 0)
                    {
                        decodedWord.Append(command, index, command.Length - index);
                        index = command.Length;
                        break;
                    }

                    decodedWord.Append(command, index + 1, closingIndex - index - 1);
                    index = closingIndex + 1;
                    continue;
                }

                if (current == '\\' && index + 1 < command.Length)
                {
                    wordHasQuoting = true;
                    decodedWord.Append(command[index + 1]);
                    index += 2;
                    continue;
                }

                decodedWord.Append(current);
                index++;
            }

            int wordLength = index - wordStart;
            if (!wordHasQuoting)
            {
                result.Append(command, wordStart, wordLength);
                continue;
            }

            string word = decodedWord.ToString();
            result.Append(BeginsWithARefusedTerm(word) ? word : new string('#', wordLength));
        }

        return result.ToString();
    }

    /// <summary>
    /// Whether a reassembled shell word — every adjacent unquoted, <c>'...'</c>, <c>"..."</c>, and
    /// <c>$'...'</c> piece between two runs of unquoted whitespace, joined and decoded — opens with
    /// one of the refused terms itself, rather than merely naming one somewhere inside a longer,
    /// unrelated value (independent pre-PR review, cycles 3 and 4). Bash hands git the whole
    /// reassembled word as a single argument regardless of how many quotes built it, so
    /// <c>--outp$'ut=/tmp/my file.txt'</c> reaches git exactly as <c>--output=/tmp/my file.txt</c>
    /// would unquoted: the flag is the word's own prefix, not text mentioning the flag. Judging only
    /// the quoted span in isolation, rather than the word it sits inside, missed exactly this case —
    /// the refused term only appears once the unquoted <c>--outp</c> and the quoted <c>ut=...</c>
    /// are joined (cycle 4, human review). Checking the match's own index rather than just whether it
    /// matches is what keeps the still-required case running: <c>note: mentions --output here</c>
    /// also matches <see cref="OutputFlag"/>, but not at index 0, so <see cref="UnescapeShellQuoting"/>
    /// still blanks it.
    /// </summary>
    private static bool BeginsWithARefusedTerm(string decoded)
    {
        Match outputMatch = OutputFlag.Match(decoded);
        if (outputMatch.Success && outputMatch.Index == 0)
        {
            return true;
        }

        Match noIndexMatch = NoIndexFlag.Match(decoded);
        if (noIndexMatch.Success && noIndexMatch.Index == 0)
        {
            return true;
        }

        Match pathMatch = AbsolutePathArgument.Match(decoded);
        return pathMatch.Success && pathMatch.Index == 0;
    }

    /// <summary>
    /// Reads one bash <c>$'...'</c> ANSI-C quoted string starting at <paramref name="dollarIndex"/>
    /// (the index of the <c>$</c> itself) and decodes its backslash escapes the way bash does —
    /// unlike a plain <c>'...'</c> quote, a backslash inside <c>$'...'</c> both escapes the closing
    /// quote (so <c>\'</c> does not end the string) and introduces an escape sequence
    /// (<see cref="DecodeAnsiCEscapes"/>). Returns false, with <paramref name="decoded"/> empty and
    /// <paramref name="nextIndex"/> at the end of the string, for an unterminated quote — the same
    /// "copy the rest through unmodified" fallback the plain-quote branch above uses.
    /// </summary>
    private static bool TryConsumeAnsiCQuoted(
        string command, int dollarIndex, out string decoded, out int nextIndex)
    {
        StringBuilder raw = new();
        int scan = dollarIndex + 2;
        while (scan < command.Length)
        {
            char current = command[scan];
            if (current == '\\' && scan + 1 < command.Length)
            {
                raw.Append(current).Append(command[scan + 1]);
                scan += 2;
                continue;
            }

            if (current == '\'')
            {
                decoded = DecodeAnsiCEscapes(raw.ToString());
                nextIndex = scan + 1;
                return true;
            }

            raw.Append(current);
            scan++;
        }

        decoded = string.Empty;
        nextIndex = command.Length;
        return false;
    }

    /// <summary>
    /// Decodes the backslash escapes bash recognises inside a <c>$'...'</c> ANSI-C quoted string
    /// (<c>man bash</c>, "ANSI-C Quoting"): the single-character escapes, <c>\xHH</c>/<c>\uHHHH</c>/
    /// <c>\UHHHHHHHH</c> hex and Unicode escapes, <c>\nnn</c> octal, and <c>\cX</c> control
    /// characters. An escape this method does not recognise is left exactly as bash leaves it — the
    /// backslash and the following character both kept literally, verified against bash directly
    /// (<c>echo $'\z'</c> prints <c>\z</c>) — rather than dropped, which would silently unescape
    /// something bash itself does not.
    /// </summary>
    private static string DecodeAnsiCEscapes(string raw)
    {
        StringBuilder decoded = new(raw.Length);
        int index = 0;
        while (index < raw.Length)
        {
            char current = raw[index];
            if (current != '\\' || index + 1 >= raw.Length)
            {
                decoded.Append(current);
                index++;
                continue;
            }

            char next = raw[index + 1];
            switch (next)
            {
                case '\\': decoded.Append('\\'); index += 2; break;
                case '\'': decoded.Append('\''); index += 2; break;
                case '"': decoded.Append('"'); index += 2; break;
                case 'a': decoded.Append('\a'); index += 2; break;
                case 'b': decoded.Append('\b'); index += 2; break;
                case 'e' or 'E': decoded.Append('\x1b'); index += 2; break;
                case 'f': decoded.Append('\f'); index += 2; break;
                case 'n': decoded.Append('\n'); index += 2; break;
                case 'r': decoded.Append('\r'); index += 2; break;
                case 't': decoded.Append('\t'); index += 2; break;
                case 'v': decoded.Append('\v'); index += 2; break;
                case 'x':
                    index = AppendHexEscape(raw, index, decoded, maxDigits: 2, toChar: true);
                    break;
                case 'u':
                    index = AppendHexEscape(raw, index, decoded, maxDigits: 4, toChar: false);
                    break;
                case 'U':
                    index = AppendHexEscape(raw, index, decoded, maxDigits: 8, toChar: false);
                    break;
                case 'c' when index + 2 < raw.Length:
                    decoded.Append((char)(char.ToUpperInvariant(raw[index + 2]) ^ 0x40));
                    index += 3;
                    break;
                case >= '0' and <= '7':
                {
                    int start = index + 1;
                    int end = start;
                    while (end < raw.Length && end < start + 3 && raw[end] is >= '0' and <= '7')
                    {
                        end++;
                    }

                    decoded.Append((char)Convert.ToInt32(raw[start..end], 8));
                    index = end;
                    break;
                }

                default:
                    decoded.Append('\\').Append(next);
                    index += 2;
                    break;
            }
        }

        return decoded.ToString();
    }

    /// <summary>
    /// Shared digit-scanning for <c>\xHH</c>, <c>\uHHHH</c> and <c>\UHHHHHHHH</c>: reads up to
    /// <paramref name="maxDigits"/> hex digits after the two-character escape lead-in, appends the
    /// decoded character (or, past the BMP, the Unicode scalar's UTF-16 form), and returns the index
    /// to resume scanning from. Falls back to the literal escape text when zero digits follow — an
    /// escape bash itself leaves unmodified in that case. Never throws: <c>\U</c>'s own eight hex
    /// digits reach well past a valid Unicode scalar (bash itself masks the value down before
    /// interpreting it), and <see cref="char.ConvertFromUtf32(int)"/> throws for exactly that case
    /// as well as for a lone surrogate — this guard's own doc requires it fail open, not throw, on a
    /// crafted input a hostile pr-review session controls end to end.
    /// </summary>
    private static int AppendHexEscape(string raw, int index, StringBuilder decoded, int maxDigits, bool toChar)
    {
        int start = index + 2;
        int end = start;
        while (end < raw.Length && end < start + maxDigits && Uri.IsHexDigit(raw[end]))
        {
            end++;
        }

        if (end == start)
        {
            decoded.Append('\\').Append(raw[index + 1]);
            return index + 2;
        }

        int codePoint = Convert.ToInt32(raw[start..end], 16);
        if (toChar)
        {
            decoded.Append((char)codePoint);
        }
        else if (codePoint is >= 0 and < 0xD800 or > 0xDFFF and <= 0x10FFFF)
        {
            decoded.Append(char.ConvertFromUtf32(codePoint));
        }
        else
        {
            decoded.Append((char)(codePoint & 0xFFFF));
        }

        return end;
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
