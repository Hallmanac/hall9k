using System.Text;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// Whether a shell command line actually INVOKES a given <c>gh</c> subcommand, as opposed to
/// naming it as text (task: a dispatched session never speaks to a person at the top level of a
/// pull request on its own). The question the hook behind
/// <see cref="ReviewThreadReplyRoutes"/> has to answer for <c>gh pr comment</c>,
/// <c>gh issue comment</c> and <c>gh pr review</c>, which unlike the API routes have no second
/// half to test: they reach GitHub without going near <c>gh api</c>, so a regex over the raw text
/// refuses <c>git grep "gh pr comment"</c> and <c>git commit -m "stop using gh pr comment"</c>
/// just as readily as the real thing.
/// <para>
/// So the command is read the way a shell reads it, far enough to tell a program from an
/// argument: it is split into simple commands at <c>; &amp; | ( ) { }</c> and newlines, quoting is
/// removed (so <c>g"h" pr comment</c> is still <c>gh pr comment</c>), command substitution and
/// backticks open nested commands even inside double quotes, and <c>bash -c "…"</c>, <c>eval</c>
/// and the PowerShell equivalents are read through to the command text they run. A simple command
/// matches when its program is <c>gh</c> after any variable assignments and wrapper words
/// (<c>env</c>, <c>sudo</c>, <c>time</c>, …), the group and verb follow any global flags, and
/// nothing else about it matters. A search pattern or a <c>-m</c> message is an argument to some
/// other program, so it never matches.
/// </para>
/// <para>
/// This is a heuristic over text, not a shell: it fails toward ALLOWING what it cannot parse,
/// because the hook it serves fails open by design and is not a sandbox. A spelling that hides the
/// program behind a variable expansion or an indirection this does not follow is a route around it,
/// which is the same honest claim <see cref="ReviewThreadReplyRoutes"/> makes for the API routes.
/// </para>
/// </summary>
internal static class ShellInvocations
{
    private const int MaxNesting = 4;

    /// <summary>Words a command may start with that hand the real program to the word after them.</summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "env", "sudo", "time", "command", "exec", "nohup", "nice", "xargs", "builtin", "call", "timeout",
        "then", "do", "else", "elif", "if", "while", "until", "!", "stdbuf",
    };

    /// <summary>Programs whose <c>-c</c>-style argument is itself a command line.</summary>
    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "sh", "zsh", "dash", "ksh", "fish", "pwsh", "powershell", "cmd",
    };

    /// <summary>Programs that run the text of their arguments as a command line.</summary>
    private static readonly HashSet<string> Evaluators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eval", "iex", "invoke-expression",
    };

    private static readonly string[] ExecutableSuffixes = [".exe", ".cmd", ".bat"];

    private static readonly HashSet<string> CommandFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-c", "-lc", "-cl", "-ic", "-ec", "-command", "-commandwithargs", "/c", "/k",
    };

    /// <summary>
    /// Whether any command in <paramref name="command"/> invokes <c>gh &lt;group&gt; &lt;verb&gt;</c>
    /// for one of <paramref name="routes"/>. Group and verb compare case-insensitively.
    /// </summary>
    public static bool InvokesGh(string command, params (string Group, string Verb)[] routes) =>
        SimpleCommands(command, 0).Any(words => InvokesGh(words, routes));

    private static bool InvokesGh(List<string> words, (string Group, string Verb)[] routes)
    {
        int start = ProgramIndex(words);
        if (start < 0 || !Program(words[start]).Equals("gh", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // gh takes global flags ahead of the group (`gh -R owner/repo pr comment`) and the group
        // takes flags ahead of the verb (`gh pr -R owner/repo comment`), so both gaps are skipped.
        int index = SkipFlags(words, start + 1);
        if (index >= words.Count)
        {
            return false;
        }

        string group = words[index];
        index = SkipFlags(words, index + 1);
        if (index >= words.Count)
        {
            return false;
        }

        string verb = words[index];
        return routes.Any(route =>
            route.Group.Equals(group, StringComparison.OrdinalIgnoreCase)
            && route.Verb.Equals(verb, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The index of the program word: past variable assignments and wrapper words (and the flags
    /// and bare numbers those take, as in <c>timeout 30 gh …</c>), or -1 when there is none.
    /// </summary>
    private static int ProgramIndex(List<string> words)
    {
        int index = 0;
        while (index < words.Count)
        {
            string word = words[index];
            if (IsAssignment(word))
            {
                index++;
            }
            else if (Wrappers.Contains(Program(word)))
            {
                index++;
                while (index < words.Count && (words[index].StartsWith('-') || IsAssignment(words[index])
                    || int.TryParse(words[index], out _)))
                {
                    index++;
                }
            }
            else
            {
                return index;
            }
        }

        return -1;
    }

    private static int SkipFlags(List<string> words, int index)
    {
        while (index < words.Count && words[index].StartsWith('-'))
        {
            // `-R`/`--repo` are the only gh flags before a subcommand that take a separate value.
            index += words[index] is "-R" or "--repo" ? 2 : 1;
        }

        return index;
    }

    private static bool IsAssignment(string word)
    {
        int equals = word.IndexOf('=');
        return equals > 0
            && (char.IsLetter(word[0]) || word[0] == '_')
            && word[..equals].All(character => char.IsLetterOrDigit(character) || character == '_');
    }

    /// <summary>
    /// The program a word names: the file name without its directory (either separator) or a
    /// <c>.exe</c>/<c>.cmd</c>/<c>.bat</c> suffix, so <c>C:\Tools\gh.exe</c> is <c>gh</c>.
    /// </summary>
    private static string Program(string word)
    {
        string name = word[(word.LastIndexOfAny(['/', '\\']) + 1)..];
        foreach (string suffix in ExecutableSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }

    /// <summary>
    /// Every simple command in <paramref name="command"/>, nested ones included, as quote-stripped
    /// words. A command that runs another command line (a shell's <c>-c</c>, <c>eval</c>, a heredoc
    /// piped into a shell) contributes that line's commands as well as its own.
    /// </summary>
    private static List<List<string>> SimpleCommands(string command, int depth)
    {
        List<List<string>> commands = [];
        if (depth > MaxNesting)
        {
            return commands;
        }

        List<string> words = [];
        StringBuilder word = new();
        bool inWord = false;
        List<string> pendingHeredocs = [];

        void EndWord()
        {
            if (inWord)
            {
                words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
        }

        void EndCommand()
        {
            EndWord();
            if (words.Count > 0)
            {
                commands.Add(words);
                words = [];
            }
        }

        int index = 0;
        while (index < command.Length)
        {
            char current = command[index];
            switch (current)
            {
                case '\\' when index + 1 < command.Length:
                    // A backslash escapes a quote, a space, a dollar, or another backslash; before
                    // any other character it is a path separator and is kept, so a Windows path to
                    // gh.exe survives.
                    if (command[index + 1] is '\'' or '"' or ' ' or '$' or '`' or '\\')
                    {
                        word.Append(command[index + 1]);
                        index += 2;
                    }
                    else
                    {
                        word.Append(current);
                        index++;
                    }

                    inWord = true;
                    break;
                case '\'':
                    {
                        int end = command.IndexOf('\'', index + 1);
                        end = end < 0 ? command.Length : end;
                        word.Append(command, index + 1, end - index - 1);
                        inWord = true;
                        index = end + 1;
                        break;
                    }
                case '"':
                    index = ReadDoubleQuoted(command, index, word, commands, depth);
                    inWord = true;
                    break;
                case '$' when index + 1 < command.Length && command[index + 1] == '(':
                    {
                        int end = MatchingParenthesis(command, index + 1);
                        commands.AddRange(SimpleCommands(command[(index + 2)..end], depth + 1));
                        word.Append("$()");
                        inWord = true;
                        index = end + 1;
                        break;
                    }
                case '`':
                    {
                        EndCommand();
                        int end = command.IndexOf('`', index + 1);
                        end = end < 0 ? command.Length : end;
                        commands.AddRange(SimpleCommands(command[(index + 1)..end], depth + 1));
                        index = end + 1;
                        break;
                    }
                case '#' when !inWord:
                    {
                        int end = command.IndexOf('\n', index);
                        index = end < 0 ? command.Length : end;
                        break;
                    }
                case '<' when index + 2 < command.Length && command[index + 1] == '<' && command[index + 2] != '<':
                    index = ReadHeredocMarker(command, index + 2, pendingHeredocs);
                    EndWord();
                    break;
                case '\n':
                    EndWord();
                    if (pendingHeredocs.Count > 0)
                    {
                        // A heredoc body is text unless the command it feeds is a shell.
                        bool feedsAShell = ProgramIndex(words) is var at and >= 0
                            && Shells.Contains(Program(words[at]));
                        foreach (string delimiter in pendingHeredocs)
                        {
                            (string body, int next) = ReadHeredocBody(command, index + 1, delimiter);
                            if (feedsAShell)
                            {
                                commands.AddRange(SimpleCommands(body, depth + 1));
                            }

                            index = next - 1;
                        }

                        pendingHeredocs.Clear();
                    }

                    EndCommand();
                    index++;
                    break;
                case ';' or '&' or '|' or '(' or ')' or '{' or '}':
                    EndCommand();
                    index++;
                    break;
                case ' ' or '\t' or '\r':
                    EndWord();
                    index++;
                    break;
                default:
                    word.Append(current);
                    inWord = true;
                    index++;
                    break;
            }
        }

        EndCommand();

        // Read through the commands that run a command line of their own. Done after the split so
        // the nested text is parsed once, whatever surrounds it.
        List<List<string>> nested = [];
        foreach (List<string> simple in commands)
        {
            int start = ProgramIndex(simple);
            if (start < 0)
            {
                continue;
            }

            string program = Program(simple[start]);
            if (Evaluators.Contains(program))
            {
                nested.AddRange(SimpleCommands(string.Join(' ', simple.Skip(start + 1)), depth + 1));
            }
            else if (Shells.Contains(program))
            {
                int flag = simple.FindIndex(start + 1, candidate => CommandFlags.Contains(candidate));
                if (flag >= 0)
                {
                    nested.AddRange(SimpleCommands(string.Join(' ', simple.Skip(flag + 1)), depth + 1));
                }
            }
        }

        commands.AddRange(nested);
        return commands;
    }

    /// <summary>
    /// Reads a double-quoted span starting at <paramref name="start"/>, appending its text to
    /// <paramref name="word"/> and the commands of any substitution inside it to
    /// <paramref name="commands"/>: double quotes suppress splitting but not command substitution,
    /// so <c>"$(gh pr comment …)"</c> runs. Returns the index just past the closing quote.
    /// </summary>
    private static int ReadDoubleQuoted(
        string command, int start, StringBuilder word, List<List<string>> commands, int depth)
    {
        int index = start + 1;
        while (index < command.Length && command[index] != '"')
        {
            char current = command[index];
            if (current == '\\' && index + 1 < command.Length && command[index + 1] is '"' or '\\' or '$' or '`')
            {
                word.Append(command[index + 1]);
                index += 2;
            }
            else if (current == '$' && index + 1 < command.Length && command[index + 1] == '(')
            {
                int end = MatchingParenthesis(command, index + 1);
                commands.AddRange(SimpleCommands(command[(index + 2)..end], depth + 1));
                word.Append("$()");
                index = end + 1;
            }
            else if (current == '`')
            {
                int end = command.IndexOf('`', index + 1);
                end = end < 0 ? command.Length : end;
                commands.AddRange(SimpleCommands(command[(index + 1)..end], depth + 1));
                index = end + 1;
            }
            else
            {
                word.Append(current);
                index++;
            }
        }

        return index + 1;
    }

    /// <summary>The index of the <c>)</c> closing the <c>(</c> at <paramref name="open"/>, or the end of the text.</summary>
    private static int MatchingParenthesis(string command, int open)
    {
        int depth = 0;
        for (int index = open; index < command.Length; index++)
        {
            switch (command[index])
            {
                case '(':
                    depth++;
                    break;
                case ')' when --depth == 0:
                    return index;
            }
        }

        return command.Length;
    }

    /// <summary>Reads the delimiter after a <c>&lt;&lt;</c> (optionally <c>-</c>, optionally quoted) and returns the index past it.</summary>
    private static int ReadHeredocMarker(string command, int index, List<string> pending)
    {
        if (index < command.Length && command[index] == '-')
        {
            index++;
        }

        while (index < command.Length && command[index] is ' ' or '\t')
        {
            index++;
        }

        StringBuilder delimiter = new();
        while (index < command.Length && !char.IsWhiteSpace(command[index]) && command[index] is not (';' or '&' or '|' or ')'))
        {
            if (command[index] is not ('\'' or '"' or '\\'))
            {
                delimiter.Append(command[index]);
            }

            index++;
        }

        if (delimiter.Length > 0)
        {
            pending.Add(delimiter.ToString());
        }

        return index;
    }

    private static (string Body, int Next) ReadHeredocBody(string command, int start, string delimiter)
    {
        int index = start;
        StringBuilder body = new();
        while (index < command.Length)
        {
            int end = command.IndexOf('\n', index);
            end = end < 0 ? command.Length : end;
            string line = command[index..end];
            index = Math.Min(end + 1, command.Length);
            if (line.Trim() == delimiter)
            {
                break;
            }

            body.Append(line).Append('\n');
        }

        return (body.ToString(), index);
    }
}
