using Hall9k.Domain.Features.Tasks.Notes;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// The closing block of notes a build, follow-up or fix session may end its final message with,
/// read the way <see cref="HandoffParser"/> reads the handoff but split out of the message rather
/// than left in it. Pure: whether a message is a work session's is the caller's to decide, because
/// a review, pr-review or spike report that quotes the marker must lose no line.
/// <para>
/// A block is the marker line and the bullets under it, blank lines between them included; the
/// first other line ends it and stays part of the message. Every block is removed from the text
/// the daemon derives everything else from (the handoff, the pull request summary, a park or
/// failure reason, a position file, a review-thread reply), wherever it sits, including after the
/// handoff marker. Only the LAST block records notes, because a session quotes its instructions
/// before answering, which is the reason <see cref="HandoffParser"/> takes the last marker too.
/// </para>
/// <para>
/// The marker is matched with its case exact and at the start of a line, and text on the marker
/// line records nothing. No prompt shows a sample bullet, so a session that quotes its instructions
/// leaves a marker line with nothing under it and records nothing.
/// </para>
/// </summary>
public static class TaskNotesBlock
{
    /// <summary>
    /// The marker line the closing-block instruction names. Declared here, and reaching a template
    /// only through a placeholder, so the instruction and the parser cannot drift apart and an
    /// edited-away marker cannot silently stop notes being recorded
    /// (<c>PromptTemplateContractTests</c>).
    /// </summary>
    public const string Marker = "TASK NOTES:";

    /// <summary>What reading a final message found: the message without any block, and the notes of the last one.</summary>
    /// <param name="Clean">The message with every block removed; the very same string when it held none.</param>
    /// <param name="Notes">One entry per bullet of the last block, at most <see cref="TaskNoteLimits.MaxClosingBlockNotes"/>, each already cut to <see cref="TaskNoteLimits.MaxTextLength"/>.</param>
    public sealed record Result(string? Clean, IReadOnlyList<string> Notes)
    {
        /// <summary>True when the message held at least one marker line.</summary>
        public bool HadBlock { get; init; }
    }

    /// <summary>Splits the notes block out of <paramref name="message"/>.</summary>
    public static Result Extract(string? message)
    {
        if (message is null || !message.Contains(Marker, StringComparison.Ordinal))
        {
            return new Result(message, []);
        }

        string[] lines = message.Split('\n');
        List<(int Start, int End)> blocks = [];
        for (int index = 0; index < lines.Length; index++)
        {
            if (!lines[index].TrimStart().StartsWith(Marker, StringComparison.Ordinal))
            {
                continue;
            }

            int end = index;
            for (int next = index + 1; next < lines.Length; next++)
            {
                if (IsBullet(lines[next]))
                {
                    end = next;
                }
                else if (!IsBlank(lines[next]))
                {
                    break;
                }
            }

            blocks.Add((index, end));
            index = end;
        }

        if (blocks.Count == 0)
        {
            return new Result(message, []);
        }

        (int lastStart, int lastEnd) = blocks[^1];
        List<string> notes = [];
        for (int index = lastStart + 1; index <= lastEnd && notes.Count < TaskNoteLimits.MaxClosingBlockNotes; index++)
        {
            if (!IsBullet(lines[index]))
            {
                continue;
            }

            string text = lines[index].Trim()[1..].Trim();
            if (text.Length > 0)
            {
                notes.Add(Cut(text));
            }
        }

        return new Result(Remove(lines, blocks), notes) { HadBlock = true };
    }

    private static string Remove(string[] lines, List<(int Start, int End)> blocks)
    {
        List<string> kept = [];
        int blockIndex = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            if (blockIndex < blocks.Count && index == blocks[blockIndex].Start)
            {
                index = blocks[blockIndex].End;
                blockIndex++;

                // The blank line that set the block apart from the text above it goes with it, so
                // removing a block never leaves a doubled gap behind.
                bool nothingFollows = index + 1 >= lines.Length || IsBlank(lines[index + 1]);
                if (nothingFollows && kept.Count > 0 && IsBlank(kept[^1]))
                {
                    kept.RemoveAt(kept.Count - 1);
                }

                continue;
            }

            kept.Add(lines[index]);
        }

        return string.Join('\n', kept);
    }

    private static string Cut(string text) =>
        text.Length <= TaskNoteLimits.MaxTextLength
            ? text
            : string.Concat(text.AsSpan(0, TaskNoteLimits.MaxTextLength - TaskNoteLimits.CutSuffix.Length).TrimEnd(), TaskNoteLimits.CutSuffix);

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    private static bool IsBullet(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '-' or '*' or '•' && (trimmed.Length == 1 || char.IsWhiteSpace(trimmed[1]));
    }
}
