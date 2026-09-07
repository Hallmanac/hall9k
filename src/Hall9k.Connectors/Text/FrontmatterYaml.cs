using System.Globalization;
using System.Text;

namespace Hall9k.Connectors.Text;

/// <summary>
/// The one reader and writer of the key/value frontmatter that both <c>h9k task add --file</c> and
/// the machine-readable task record on a published issue are written in
/// (<see cref="WorkItems.TaskRecord"/>). Two callers, one parser: the record block claims to be
/// "the same shape --file accepts", and the only way that claim stays true is for both to read
/// through the same code.
/// <para>
/// The document grammar is deliberately the forgiving line-oriented one the file format has always
/// had — one <c>key: value</c> per line, <c>- item</c> lists, unknown keys ignored — rather than a
/// whole-document YAML parse. That is not laziness: <c>TaskDocumentRenderer</c> has written plain
/// scalars carrying colons and <c>#</c> characters into every task.md since the format existed, and
/// a strict parser would refuse to read back files this platform itself wrote. What changed, and
/// what this class is for, is the <em>scalars</em>: each value is read as a real YAML scalar, so a
/// double-quoted or single-quoted value arrives without its quote characters and a <c>|</c> or
/// <c>&gt;</c> block scalar arrives as the multi-line text it denotes.
/// </para>
/// <para>
/// Origin incident (2026-09-06): the Windows node adopted issues #81 and #82 from record blocks
/// hand-written with double-quoted YAML scalars, and the line-oriented parser stored the quote
/// characters as part of the objective and of every criterion. <see cref="WriteScalar"/> answers
/// the same failure from the writing side — it never quotes, so a record this platform writes has
/// nothing for any reader to have to unquote.
/// </para>
/// </summary>
public static class FrontmatterYaml
{
    /// <summary>How far a block scalar's content sits under the key that opened it.</summary>
    public const int BlockIndent = 2;

    /// <summary>
    /// Read <paramref name="text"/> as frontmatter plus an optional body. Text opening with a
    /// <c>---</c> line is frontmatter up to the matching closing <c>---</c>, with the remainder as
    /// <see cref="Frontmatter.Body"/>; text with no opening delimiter — the record block, which is a
    /// bare mapping inside a fenced code block — is frontmatter all the way down and has no body.
    /// </summary>
    public static Frontmatter Parse(string? text)
    {
        string[] lines = (text ?? string.Empty).ReplaceLineEndings("\n").Split('\n');
        int index = 0;
        bool delimited = lines.Length > 0 && lines[0].Trim() == "---";
        if (delimited)
        {
            index = 1;
        }

        Dictionary<string, string> scalars = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> lists = new(StringComparer.OrdinalIgnoreCase);
        List<string>? openList = null;
        int bodyStart = lines.Length;

        while (index < lines.Length)
        {
            string line = lines[index];
            if (delimited && line.Trim() == "---")
            {
                bodyStart = index + 1;
                break;
            }

            if (line.Trim().Length == 0)
            {
                // A blank line closes nothing. YAML permits a block sequence's items to be
                // separated by blank lines, and a hand-written record block routinely has them —
                // closing the open list here dropped every item after the blank one silently (or,
                // when the item carried a colon, misread it as a stray top-level key), so a record
                // adopted with --from-issue came back with a shorter readiness contract than the
                // origin wrote it with, and nothing said so. This platform's own writer produces
                // the same shape: a criterion ending in two or more newlines is written as a |+
                // block item, whose trailing blank lines ReadBlockScalar hands back to this
                // scanner (independent pre-PR review, cycle 1, adversarial lens).
                index++;
                continue;
            }

            if (openList is not null && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                string item = line.TrimStart()[2..].Trim();
                openList.Add(BlockHeader.TryRead(item) is { } itemHeader
                    // The dash's own column is the parent indent, not the column after it: a block
                    // scalar under "- |" has its content indented past the dash, and measuring from
                    // the character after it would read every such block as empty.
                    ? ReadBlockScalar(lines, ref index, Indent(line), itemHeader)
                    : Unquote(item));
                index++;
                continue;
            }

            openList = null;
            int separator = line.IndexOf(':');
            string key = separator < 0 ? string.Empty : line[..separator].Trim();
            if (separator < 0 || key.Length == 0)
            {
                index++;
                continue;
            }

            string value = line[(separator + 1)..].Trim();
            if (value.Length == 0)
            {
                // An empty value opens a block sequence — "criteria:" then "- one", "- two". It is
                // recorded as an empty list rather than as an empty scalar, so a key present with no
                // items reads as "the origin had none" rather than as "the origin said nothing".
                openList = Open(lists, key);
                index++;
                continue;
            }

            if (BlockHeader.TryRead(value) is { } header)
            {
                scalars[key] = ReadBlockScalar(lines, ref index, Indent(line), header);
                index++;
                continue;
            }

            scalars[key] = Unquote(value);
            // A scalar may still be followed by "- " items: "blocked-by: a, b" with more
            // dependencies listed underneath has been legal in the file format since it existed,
            // and dropping the items here would silently lose dependencies a task file declared.
            openList = Open(lists, key);
            index++;
        }

        string? body = delimited && bodyStart < lines.Length
            ? string.Join('\n', lines.Skip(bodyStart)).Trim()
            : null;
        return new Frontmatter(
            scalars,
            lists.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
            body.IsBlank() ? null : body);
    }

    /// <summary>
    /// One frontmatter line, written so that no reader ever has to unquote it: a value safe as a
    /// YAML plain scalar is written bare, and anything else — multi-line text, a value opening with
    /// an indicator character, one carrying <c>": "</c> or <c>" #"</c> — is written as a literal
    /// block scalar. Quoted scalars are never produced, which is the writing half of the 2026-09-06
    /// finding in this class's own summary.
    /// </summary>
    public static string WriteScalar(string key, string? value, int indent = 0)
    {
        string normalized = (value ?? string.Empty).ReplaceLineEndings("\n");
        string pad = new(' ', indent);
        return IsPlainSafe(normalized)
            ? $"{pad}{key}: {normalized}\n"
            : $"{pad}{key}: {BlockScalar(normalized, indent + BlockIndent)}";
    }

    /// <summary>
    /// A value that is already YAML — a boolean, a number, a flow sequence — written through
    /// verbatim. Separate from <see cref="WriteScalar"/> because that one is for strings and would
    /// dutifully block-scalar the word <c>true</c> to keep it one, which is the opposite of what a
    /// caller writing an actual boolean means.
    /// </summary>
    public static string WriteValue(string key, string value, int indent = 0) =>
        $"{new string(' ', indent)}{key}: {value}\n";

    /// <summary>One item of a block sequence, under the same never-quote rule as <see cref="WriteScalar"/>.</summary>
    public static string WriteListItem(string? value, int indent = 0)
    {
        string normalized = (value ?? string.Empty).ReplaceLineEndings("\n");
        string pad = new(' ', indent);
        return IsPlainSafe(normalized)
            ? $"{pad}- {normalized}\n"
            : $"{pad}- {BlockScalar(normalized, indent + BlockIndent)}";
    }

    /// <summary>
    /// A literal block scalar: the header (with the chomping indicator the value's own trailing
    /// newlines call for) and the indented content. An explicit indentation indicator is written
    /// whenever the first content line itself begins with a space, because without one a strict
    /// YAML reader would take that extra space as part of the block's indentation and silently
    /// re-flow every line under it.
    /// </summary>
    private static string BlockScalar(string value, int indent)
    {
        string content = value.TrimEnd('\n');
        int trailingNewlines = value.Length - content.Length;
        char chomping = trailingNewlines switch
        {
            0 => '-',
            1 => '\0',
            _ => '+',
        };

        string[] contentLines = content.Split('\n');
        bool needsIndentIndicator = contentLines.Length > 0 && contentLines[0].StartsWith(' ');
        StringBuilder written = new();
        written.Append('|');
        if (needsIndentIndicator)
        {
            written.Append(BlockIndent.ToString(CultureInfo.InvariantCulture));
        }

        if (chomping != '\0')
        {
            written.Append(chomping);
        }

        written.Append('\n');
        string pad = new(' ', indent);
        foreach (string contentLine in contentLines)
        {
            // A blank line is written blank rather than padded: trailing whitespace on an otherwise
            // empty line is invisible in a diff and changes nothing about how the block reads back.
            written.Append(contentLine.Length == 0 ? string.Empty : pad + contentLine).Append('\n');
        }

        for (int extra = 1; extra < trailingNewlines; extra++)
        {
            written.Append('\n');
        }

        return written.ToString();
    }

    private static List<string> Open(Dictionary<string, List<string>> lists, string key)
    {
        if (!lists.TryGetValue(key, out List<string>? list))
        {
            list = [];
            lists[key] = list;
        }

        return list;
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    /// <summary>
    /// Whether a value can be written bare. Conservative on purpose: every doubtful shape falls
    /// through to a block scalar, which is always correct, so the cost of a false negative is a
    /// slightly longer record and the cost of a false positive is a record that reads back wrong.
    /// </summary>
    private static bool IsPlainSafe(string value)
    {
        if (value.Length == 0 || value.Contains('\n'))
        {
            return false;
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return false;
        }

        if (YamlIndicators.Contains(value[0]))
        {
            return false;
        }

        if (value.Contains(": ", StringComparison.Ordinal) || value.EndsWith(':')
            || value.Contains(" #", StringComparison.Ordinal))
        {
            return false;
        }

        // A word YAML would hand back as something other than a string has to be quoted to stay a
        // string, and this writer does not quote — so it block-scalars instead.
        return !NonStringPlainScalars.Contains(value)
            && !double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }

    private const string YamlIndicators = "-?:,[]{}#&*!|>'\"%@`";

    private static readonly HashSet<string> NonStringPlainScalars = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "on", "off", "null", "~",
    };

    /// <summary>
    /// The content of a block scalar, read from the lines under its header.
    /// <paramref name="index"/> is left on the last line consumed, so the caller's own advance
    /// steps past it exactly as it does for a one-line scalar. <paramref name="parentIndent"/> is
    /// the column the header's own key starts at: content has to sit further right than that, and
    /// a block whose first non-blank line does not is an empty block followed by the next key,
    /// never a block that swallows it.
    /// </summary>
    private static string ReadBlockScalar(string[] lines, ref int index, int parentIndent, BlockHeader header)
    {
        int start = index + 1;
        int? firstIndent = FirstContentIndent(lines, start);
        int contentIndent = header.Indent is { } explicitIndent
            ? parentIndent + explicitIndent
            : firstIndent ?? parentIndent + BlockIndent;
        if (firstIndent is not { } observed || observed <= parentIndent || observed < contentIndent)
        {
            return string.Empty;
        }

        List<string> content = [];
        int scan = start;
        for (; scan < lines.Length; scan++)
        {
            string line = lines[scan];
            if (line.Trim().Length == 0)
            {
                content.Add(string.Empty);
                continue;
            }

            if (Indent(line) < contentIndent)
            {
                break;
            }

            content.Add(line.Length <= contentIndent ? string.Empty : line[contentIndent..]);
        }

        // Trailing blank lines belong to the chomping rule below, not to the block's content, and
        // they are also the lines a following key sits after — so they are handed back to the
        // scanner rather than swallowed.
        int end = content.Count;
        while (end > 0 && content[end - 1].Length == 0)
        {
            end--;
        }

        int trailingBlanks = content.Count - end;
        index = scan - 1 - trailingBlanks;
        string joined = header.Folded ? Fold(content.Take(end)) : string.Join('\n', content.Take(end));
        return header.Chomping switch
        {
            '-' => joined,
            '+' => joined + new string('\n', trailingBlanks + 1),
            _ => joined.Length == 0 ? string.Empty : joined + "\n",
        };
    }

    private static int? FirstContentIndent(string[] lines, int start)
    {
        for (int scan = start; scan < lines.Length; scan++)
        {
            if (lines[scan].Trim().Length > 0)
            {
                return Indent(lines[scan]);
            }
        }

        return null;
    }

    /// <summary>
    /// YAML's folded style: single newlines between non-empty lines become spaces, and a blank line
    /// is the paragraph break that survives as a newline of its own.
    /// </summary>
    private static string Fold(IEnumerable<string> content)
    {
        StringBuilder folded = new();
        bool previousWasContent = false;
        foreach (string line in content)
        {
            if (line.Length == 0)
            {
                folded.Append('\n');
                previousWasContent = false;
                continue;
            }

            if (previousWasContent)
            {
                folded.Append(' ');
            }

            folded.Append(line);
            previousWasContent = true;
        }

        return folded.ToString();
    }

    /// <summary>
    /// A YAML scalar's quote characters removed, or the value verbatim when it is not a quoted
    /// scalar at all. The closing quote has to be the value's last character for the value to be
    /// one: <c>"one" and "two"</c> opens and closes with a quote without being a quoted scalar,
    /// and stripping its outer characters would silently corrupt it.
    /// </summary>
    internal static string Unquote(string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        return value[0] switch
        {
            '"' => ReadDoubleQuoted(value) ?? value,
            '\'' => ReadSingleQuoted(value) ?? value,
            _ => value,
        };
    }

    private static string? ReadDoubleQuoted(string value)
    {
        StringBuilder read = new();
        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            if (character == '"')
            {
                return index == value.Length - 1 ? read.ToString() : null;
            }

            if (character != '\\')
            {
                read.Append(character);
                continue;
            }

            index++;
            if (index >= value.Length)
            {
                return null;
            }

            switch (value[index])
            {
                case 'n':
                    read.Append('\n');
                    break;
                case 't':
                    read.Append('\t');
                    break;
                case 'r':
                    read.Append('\r');
                    break;
                case '0':
                    read.Append('\0');
                    break;
                case 'u' when index + 4 < value.Length
                    && ushort.TryParse(
                        value.AsSpan(index + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                        out ushort code):
                    read.Append((char)code);
                    index += 4;
                    break;
                default:
                    read.Append(value[index]);
                    break;
            }
        }

        return null;
    }

    private static string? ReadSingleQuoted(string value)
    {
        StringBuilder read = new();
        for (int index = 1; index < value.Length; index++)
        {
            if (value[index] != '\'')
            {
                read.Append(value[index]);
                continue;
            }

            if (index + 1 < value.Length && value[index + 1] == '\'')
            {
                read.Append('\'');
                index++;
                continue;
            }

            return index == value.Length - 1 ? read.ToString() : null;
        }

        return null;
    }

    /// <summary>A block scalar's header line: <c>|</c> or <c>&gt;</c>, its chomping, its indentation indicator.</summary>
    private readonly record struct BlockHeader(bool Folded, char Chomping, int? Indent)
    {
        public static BlockHeader? TryRead(string value)
        {
            if (value.Length == 0 || (value[0] != '|' && value[0] != '>'))
            {
                return null;
            }

            bool folded = value[0] == '>';
            char chomping = '\0';
            int? indent = null;
            for (int index = 1; index < value.Length; index++)
            {
                char character = value[index];
                if (character is '-' or '+' && chomping == '\0')
                {
                    chomping = character;
                    continue;
                }

                if (character is >= '1' and <= '9' && indent is null)
                {
                    indent = character - '0';
                    continue;
                }

                // Anything else means this was never a block header — "| the objective" is a plain
                // scalar someone wrote a pipe into, and reading it as a header would eat the lines
                // beneath it.
                return null;
            }

            return new BlockHeader(folded, chomping, indent);
        }
    }
}

/// <summary>
/// A parsed frontmatter block: its scalars, its block sequences, and the markdown body underneath
/// it when the text carried <c>---</c> delimiters. Lookups are case-insensitive on the key, which
/// is how the record's <c>type: feature</c> and a hand-written <c>Type: Feature</c> both read
/// (the record's own casing rule lives in <see cref="WorkItems.TaskRecord"/>).
/// </summary>
public sealed class Frontmatter(
    IReadOnlyDictionary<string, string> scalars,
    IReadOnlyDictionary<string, IReadOnlyList<string>> lists,
    string? body)
{
    /// <summary>What followed the closing <c>---</c>, trimmed; null when there was none.</summary>
    public string? Body { get; } = body;

    /// <summary>Whether the block mentioned this key at all, however it was valued.</summary>
    public bool Has(string key) => scalars.ContainsKey(key) || lists.ContainsKey(key);

    /// <summary>The key's scalar, or null when it carried none (a list, or nothing at all).</summary>
    public string? Scalar(string key) =>
        scalars.TryGetValue(key, out string? value) && value.IsNotBlank() ? value : null;

    /// <summary>
    /// The key's sequence: its <c>- item</c> lines, or the flow form (<c>[a, b]</c>, <c>[]</c>)
    /// when it was written on the key's own line. A flow sequence is read out of the scalar rather
    /// than parsed at scan time so that a plain value which merely opens with a bracket — an
    /// objective reading <c>[BUG] the thing</c> — stays the string it is.
    /// <para>
    /// A key written in both forms at once — a flow sequence on its own line with <c>- item</c>
    /// lines underneath — hands back both, in that order, the same way <see cref="ListOrInline"/>
    /// already treats its comma form. Neither writer produces that shape, but preferring one form
    /// and discarding the other would silently lose declared items, which is the whole failure the
    /// blank-line fix in <see cref="FrontmatterYaml.Parse"/> answers on the scanning side.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> List(string key)
    {
        string[]? flow = Flow(Scalar(key));
        if (lists.TryGetValue(key, out IReadOnlyList<string>? items) && items.Count > 0)
        {
            return flow is null ? items : [.. flow, .. items];
        }

        return flow ?? [];
    }

    /// <summary>
    /// <see cref="List"/> plus the file format's long-standing inline comma form
    /// (<c>blocked-by: a, b</c>) — the reading only <c>h9k task add --file</c>'s own keys want, and
    /// never the record's, whose sequences are always written as real YAML.
    /// </summary>
    public IReadOnlyList<string> ListOrInline(string key)
    {
        List<string> combined = [];
        if (Scalar(key) is { } scalar)
        {
            combined.AddRange(Flow(scalar)
                ?? scalar.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (lists.TryGetValue(key, out IReadOnlyList<string>? items))
        {
            combined.AddRange(items);
        }

        return combined;
    }

    /// <summary>The key read as a YAML boolean, or null when it is absent or says something else.</summary>
    public bool? Flag(string key) => Scalar(key)?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "on" => true,
        "false" or "no" or "off" => false,
        _ => null,
    };

    /// <summary>The key read as an integer, or null when it is absent or is not one.</summary>
    public int? Number(string key) =>
        Scalar(key) is { } value
        && int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;

    private static string[]? Flow(string? value) =>
        value is not null && value.StartsWith('[') && value.EndsWith(']')
            ? [.. value[1..^1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(FrontmatterYaml.Unquote)]
            : null;
}
