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
    /// An argument that names an absolute filesystem path, or one built from an unexpanded
    /// environment reference (<c>$HOME/...</c>, <c>${HOME}/...</c>, PowerShell's
    /// <c>$env:USERPROFILE\...</c>) — the shape <c>--no-index</c> mode triggers on even with no
    /// flag naming it (lesson f059f669). Nothing this project's own documented pr-review usage
    /// runs (<c>review-mechanics.md</c>'s own ordinary-diff-range, <c>origin/&lt;base&gt;...HEAD</c>,
    /// or a bare <c>git log</c>) ever needs a path argument shaped like this, so refusing every one
    /// of them costs a lens nothing (independent pre-PR review, cycle 6, both lenses: a leading
    /// <c>$</c> reaches the same filesystem-diff mode once the shell expands it, and this class
    /// reasons about the command text before any shell expansion happens).
    /// </summary>
    private static readonly Regex AbsolutePathArgument = new(
        @"(?:^|\s)(?:/|~|\$|[A-Za-z]:[\\/])\S*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// An argument containing a <c>..</c> path segment — the other shape a relative path can take
    /// to point outside the working tree and still trigger <c>--no-index</c> mode (independent
    /// pre-PR review, cycle 6, both lenses: <c>git diff ../../sa.txt ../../sb.txt</c> run from a
    /// checkout under <c>~/.hall9k/projects/.../repo/wt-*</c> printed a file five levels above it).
    /// Matched anywhere a slash or backslash brackets the segment, or at the start or end of the
    /// argument, so it catches <c>../secret</c>, <c>foo/../secret</c>, and <c>..\secret</c> alike
    /// without flagging an ordinary git revision range such as <c>origin/main..HEAD</c>, where the
    /// dots sit directly against a ref name rather than a path separator.
    /// </summary>
    private static readonly Regex ParentDirectorySegment = new(
        @"(?:^|[\\/\s])\.\.(?:[\\/\s]|$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

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

        string unescaped = UnescapeShellQuoting(command, out bool hasUnterminatedQuote);
        return GitLogOrDiff.IsMatch(unescaped)
            && (hasUnterminatedQuote
                || OutputFlag.IsMatch(unescaped)
                || NoIndexFlag.IsMatch(unescaped)
                || AbsolutePathArgument.IsMatch(unescaped)
                || ParentDirectorySegment.IsMatch(unescaped)
                || HasUnquotedExpansion(command));
    }

    /// <summary>
    /// Whether the command contains an unquoted brace group with a comma (bash brace expansion,
    /// e.g. <c>{/dev/null,~/.config/gh/hosts.yml}</c>, or <c>-{-output=&lt;path&gt;,-stat}</c>) or a
    /// bare, unquoted <c>$</c> (parameter, command, or arithmetic expansion — <c>${IFS}</c>,
    /// <c>$(...)</c>, a plain <c>$IFS</c>) outside a fully single-quoted word. Both build or split a
    /// refused flag or path only once bash itself runs the command, after every text-based check
    /// above has already looked at the unexpanded text and found nothing (independent pre-PR
    /// review, cycle 8, adversarial lens; lesson 4a5df6e3): <c>git diff --output${IFS}/tmp/pwn</c>
    /// never contains a literal <c>--output</c> followed by <c>=</c>, whitespace, or end of string,
    /// so <see cref="OutputFlag"/> misses it, yet bash splits it into <c>--output /tmp/pwn</c>, a
    /// form git accepts. Verified in a throwaway repository: each shape wrote or read its file
    /// exactly as the unquoted, unexpanded form does.
    /// <para>
    /// This runs on the raw command text, not the reassembled one <see cref="UnescapeShellQuoting"/>
    /// produces: a <c>$'...'</c> or <c>$"..."</c> lead-in is quoting syntax, not an expansion (its
    /// own <c>$</c> is skipped over here the same way that method decodes it, rather than counted),
    /// and text inside a <c>'...'</c> or <c>"..."</c> span is skipped rather than inspected — quoting
    /// suppresses brace expansion entirely, and a double-quoted <c>$</c> still expands but without
    /// the word-splitting or pathname expansion that makes the unquoted form dangerous, so it costs
    /// nothing to leave it alone. This is gated by <see cref="GitLogOrDiff"/> matching the
    /// reassembled text, so it only fires on a command that already reads as <c>git diff</c>/<c>git
    /// log</c> once quoting is accounted for, never on the term appearing only inside an unrelated
    /// quoted value.
    /// </para>
    /// </summary>
    private static bool HasUnquotedExpansion(string command)
    {
        int index = 0;
        while (index < command.Length)
        {
            char current = command[index];
            if (current == '\\' && index + 1 < command.Length)
            {
                index += 2;
                continue;
            }

            if (current == '\'')
            {
                index = SkipPastClosingQuote(command, index + 1, '\'', honorBackslash: false);
                continue;
            }

            if (current == '"')
            {
                index = SkipPastClosingQuote(command, index + 1, '"', honorBackslash: true);
                continue;
            }

            if (current == '$' && index + 1 < command.Length && command[index + 1] is '\'' or '"')
            {
                index = SkipPastClosingQuote(command, index + 2, command[index + 1], honorBackslash: true);
                continue;
            }

            if (current == '$')
            {
                return true;
            }

            if (current == '{')
            {
                int depth = 1;
                int scan = index + 1;
                bool sawComma = false;
                while (scan < command.Length && depth > 0)
                {
                    if (command[scan] == '{')
                    {
                        depth++;
                    }
                    else if (command[scan] == '}')
                    {
                        depth--;
                    }
                    else if (command[scan] == ',' && depth == 1)
                    {
                        sawComma = true;
                    }

                    scan++;
                }

                if (sawComma && depth == 0)
                {
                    return true;
                }

                index++;
                continue;
            }

            index++;
        }

        return false;
    }

    /// <summary>
    /// Scans forward from <paramref name="start"/> for the next unescaped <paramref name="quoteChar"/>
    /// and returns the index just past it (or the end of the string, for an unterminated quote — the
    /// caller only uses this to skip safe content, so an unterminated quote here is left for
    /// <see cref="UnescapeShellQuoting"/>'s own <c>hasUnterminatedQuote</c> refusal to catch).
    /// <paramref name="honorBackslash"/> is false for a plain <c>'...'</c> span, where bash gives
    /// backslash no special meaning at all, and true for <c>"..."</c> and <c>$'...'</c>/<c>$"..."</c>,
    /// where a backslash can escape the closing quote.
    /// </summary>
    private static int SkipPastClosingQuote(string command, int start, char quoteChar, bool honorBackslash)
    {
        int index = start;
        while (index < command.Length && command[index] != quoteChar)
        {
            index += honorBackslash && command[index] == '\\' && index + 1 < command.Length ? 2 : 1;
        }

        return index < command.Length ? index + 1 : command.Length;
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
    /// <para>
    /// Also decodes bash's <c>$"..."</c> locale-translation quoting — which takes the identical
    /// backslash-escaping rules as plain <c>"..."</c> once no translation applies — and treats a
    /// backslash immediately followed by a newline as bash's line-continuation, removed entirely
    /// rather than kept as a literal character, both inside and outside a double-quoted span
    /// (independent pre-PR review, cycle 6, conformance lens: <c>--outp$"ut=..."</c> and
    /// <c>--out\</c>&lt;newline&gt;<c>put=...</c> each reassemble into <c>--output=...</c> in bash
    /// 5.3 but previously reassembled into something that did not start with it). Also decodes
    /// <c>\"</c>, <c>\\</c>, <c>` \` `</c>, and <c>\$</c> inside a double-quoted span the way bash
    /// does, rather than closing the span at the first <c>"</c> regardless of what precedes it
    /// (cycle 6, adversarial lens: <c>--format="%H\""</c> closed early on the escaped quote, which
    /// then folded a following <c>--output=...</c> argument into the same word and blanked it).
    /// </para>
    /// <para>
    /// An unterminated <c>'...'</c>, <c>"..."</c>, or <c>$'...'</c> sets
    /// <paramref name="hasUnterminatedQuote"/> rather than silently blanking whatever follows: the
    /// old fallback copied the rest of the command into the current word and then blanked all of it
    /// because the word did not start with a refused term, which made an unterminated quote a way to
    /// hide a real flag from every regex downstream (cycle 6, adversarial lens). The caller refuses
    /// outright on this rather than trying to guess what the shell would have done with a quote it
    /// never closed.
    /// </para>
    /// </summary>
    private static string UnescapeShellQuoting(string command, out bool hasUnterminatedQuote)
    {
        hasUnterminatedQuote = false;
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
                        hasUnterminatedQuote = true;
                        decodedWord.Append(command, index, command.Length - index);
                        index = command.Length;
                        break;
                    }

                    decodedWord.Append(decoded);
                    index = nextIndex;
                    continue;
                }

                if (current == '$' && index + 1 < command.Length && command[index + 1] == '"')
                {
                    // $"..." locale-translation quoting: without a matching translation loaded,
                    // bash treats it exactly like "...". Drop the '$' and fall into the double-quote
                    // branch below on the next iteration.
                    wordHasQuoting = true;
                    index++;
                    continue;
                }

                if (current == '"')
                {
                    wordHasQuoting = true;
                    int scan = index + 1;
                    StringBuilder quoted = new();
                    bool closed = false;
                    while (scan < command.Length)
                    {
                        char quotedChar = command[scan];
                        if (quotedChar == '"')
                        {
                            closed = true;
                            scan++;
                            break;
                        }

                        if (quotedChar == '\\' && scan + 1 < command.Length
                            && command[scan + 1] is '"' or '\\' or '`' or '$' or '\n')
                        {
                            if (command[scan + 1] != '\n')
                            {
                                quoted.Append(command[scan + 1]);
                            }

                            scan += 2;
                            continue;
                        }

                        quoted.Append(quotedChar);
                        scan++;
                    }

                    if (!closed)
                    {
                        hasUnterminatedQuote = true;
                        decodedWord.Append(command, index, command.Length - index);
                        index = command.Length;
                        break;
                    }

                    decodedWord.Append(quoted);
                    index = scan;
                    continue;
                }

                if (current == '\'')
                {
                    wordHasQuoting = true;
                    int closingIndex = command.IndexOf(current, index + 1);
                    if (closingIndex < 0)
                    {
                        hasUnterminatedQuote = true;
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
                    if (command[index + 1] == '\n')
                    {
                        // bash line continuation: backslash-newline is removed entirely, not kept
                        // as a literal newline.
                        index += 2;
                        continue;
                    }

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
            result.Append(
                BeginsWithARefusedTerm(word) || IsGitLogOrDiffToken(word) ? word : new string('#', wordLength));
        }

        return result.ToString();
    }

    /// <summary>
    /// Whether a reassembled shell word is exactly the <c>git</c>, <c>diff</c>, or <c>log</c> token
    /// itself, case-insensitively (independent pre-PR review, cycle 8, conformance lens). These
    /// three are not refused terms the way <see cref="BeginsWithARefusedTerm"/>'s are, but
    /// <see cref="GitLogOrDiff"/> anchors on them, so a quoted or escaped spelling of any one —
    /// <c>git 'diff' --output=...</c>, <c>git d''iff ...</c>, <c>\git log ...</c> — must still
    /// survive as itself rather than being blanked out like an ordinary unrelated word: blanking it
    /// let <see cref="GitLogOrDiff"/> miss the command entirely, and every check downstream of it
    /// along with it, regardless of what flags the command carried.
    /// </summary>
    private static bool IsGitLogOrDiffToken(string decoded) =>
        decoded.Equals("git", StringComparison.OrdinalIgnoreCase)
        || decoded.Equals("diff", StringComparison.OrdinalIgnoreCase)
        || decoded.Equals("log", StringComparison.OrdinalIgnoreCase);

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
    /// <para>
    /// A <see cref="ParentDirectorySegment"/> match is checked without that same index-0 requirement
    /// (independent pre-PR review, cycle 6): unlike a flag or an absolute path, a <c>..</c> segment
    /// reaches git's filesystem-diff mode wherever it sits in the argument
    /// (<c>foo/../secret</c> is exactly as much an escape as <c>../secret</c>), so the whole
    /// reassembled word is kept, not blanked, whenever one appears anywhere in it.
    /// </para>
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
        if (pathMatch.Success && pathMatch.Index == 0)
        {
            return true;
        }

        return ParentDirectorySegment.IsMatch(decoded);
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
