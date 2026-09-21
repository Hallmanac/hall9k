using System.Text;
using System.Text.RegularExpressions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// A run skill read as an ordered plan somebody (or something) can actually follow (idea b9b09779,
/// piece 5): the steps in the order they are needed, plus the two sections that are a readout
/// rather than a step.
/// </summary>
/// <param name="Steps">Every step, numbered from 1, in the order a launch walks them.</param>
/// <param name="HowToKnowItIsUp">The skill's own observable signal, verbatim; empty when the skill states none.</param>
/// <param name="AddressOrEntryPoint">Where the reader goes to use it, verbatim; empty when the skill states none.</param>
public sealed record RunSkillPlan(
    IReadOnlyList<RunSkillStep> Steps, string HowToKnowItIsUp, string AddressOrEntryPoint)
{
    /// <summary>The steps that start the product and are left running, rather than waited on.</summary>
    public IReadOnlyList<RunSkillStep> LaunchSteps =>
        [.. Steps.Where(step =>
            step.Kind == RunSkillStepKind.Command
            && step.Section.Equals(RunSkillDocument.LaunchHeading, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Every step a person has to do themselves, in plan order.</summary>
    public IReadOnlyList<RunSkillStep> HumanSteps =>
        [.. Steps.Where(step => step.Kind == RunSkillStepKind.Human)];
}

/// <summary>
/// Reads a run-skill document (<see cref="RunSkillDocument"/>) into the ordered plan
/// <c>h9k task run-local</c> walks (idea b9b09779, piece 5).
/// <para>
/// The document is prose a session composed, not a script, so this parse is deliberately narrow
/// about what it will call a command: a fenced code block's lines, and an inline backtick span
/// carrying whitespace. Everything else in a step is a <see cref="RunSkillStepKind.Human"/> step,
/// which prints and stops rather than running. That asymmetry is the point — mistaking a command
/// for prose costs the reviewer one manual paste, and mistaking prose for a command runs something
/// nobody wrote on their machine.
/// </para>
/// <para>
/// The <c>## Human steps</c> section is hoisted to the front of the plan rather than left where
/// the document puts it last. Every entry in it is by definition something the composing session
/// could NOT determine (the discovery prompt's own human-steps rule), which means the platform
/// also cannot sequence it: a credential it never saw might be needed before the first setup
/// command or after the last. Front is the one position that is never wrong, because a thing
/// already done cannot be needed later than it happened.
/// </para>
/// </summary>
public static partial class RunSkillSteps
{
    /// <summary>
    /// How a section says it has nothing in it, matched on the first word of its first non-blank
    /// line. The discovery prompt tells a composing session that a section whose honest content is
    /// "none" still gets its heading and says so, and <see cref="RunSkillDocument.ComposeNoneDiscoverable"/>
    /// writes exactly these words, so this is the convention the platform itself already produces
    /// rather than a guess at how prose might phrase an absence.
    /// </summary>
    private static readonly string[] NothingWords = ["none", "nothing", "n/a", "unknown", "not applicable"];

    /// <summary>The sections that hold steps, in the order a launch walks them — human steps first, for the reason on the class.</summary>
    private static readonly string[] StepSections =
    [
        RunSkillDocument.HumanStepsHeading,
        RunSkillDocument.PrerequisitesHeading,
        RunSkillDocument.OneTimeSetupHeading,
        RunSkillDocument.LaunchHeading,
    ];

    /// <summary>The plan this document describes. A document with no recognizable section parses as an empty plan rather than throwing.</summary>
    public static RunSkillPlan Parse(string? document)
    {
        IReadOnlyDictionary<string, string> sections = Sections(document);
        List<RunSkillStep> steps = [];
        foreach (string section in StepSections)
        {
            if (!sections.TryGetValue(section, out string? body))
            {
                continue;
            }

            foreach (string item in Items(body))
            {
                steps.AddRange(StepsFrom(section, item, steps.Count));
            }
        }

        return new RunSkillPlan(
            steps,
            sections.TryGetValue(RunSkillDocument.HowToKnowItIsUpHeading, out string? up) && !IsNothing(up)
                ? up.Trim()
                : string.Empty,
            sections.TryGetValue(RunSkillDocument.AddressOrEntryPointHeading, out string? address) && !IsNothing(address)
                ? address.Trim()
                : string.Empty);
    }

    /// <summary>
    /// Each canonical heading's body, keyed by the canonical spelling. A heading the document
    /// carries at some other markdown level still matches, the same leniency
    /// <see cref="RunSkillDocument.MissingHeadings"/> already applies, and a heading the document
    /// repeats keeps the first body rather than silently concatenating two sections.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Sections(string? document)
    {
        Dictionary<string, string> sections = new(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        StringBuilder body = new();
        bool fenced = false;

        void Close()
        {
            if (current is not null && !sections.ContainsKey(current))
            {
                sections[current] = body.ToString();
            }

            body.Clear();
        }

        foreach (string line in Lines(document))
        {
            if (IsFence(line))
            {
                fenced = !fenced;
            }
            else if (!fenced && line.TrimStart().StartsWith('#'))
            {
                string heading = line.TrimStart().TrimStart('#').Trim();
                string? canonical = RunSkillDocument.Headings
                    .FirstOrDefault(known => known.Equals(heading, StringComparison.OrdinalIgnoreCase));
                if (canonical is not null)
                {
                    Close();
                    current = canonical;
                    continue;
                }

                // A heading the run-skill shape does not name — a composing session's own
                // subheading inside a section. It stays in the section's body rather than
                // closing it, so the steps under it are not silently dropped.
            }

            if (current is not null)
            {
                body.Append(line).Append('\n');
            }
        }

        Close();
        return sections;
    }

    /// <summary>
    /// One section's body split into its list items, or the whole body as a single item when it
    /// carries no list at all. A section saying it has nothing yields nothing.
    /// </summary>
    private static IReadOnlyList<string> Items(string body)
    {
        if (IsNothing(body))
        {
            return [];
        }

        List<string> items = [];
        StringBuilder current = new();
        bool fenced = false;

        void Close()
        {
            if (current.ToString().IsNotBlank())
            {
                items.Add(current.ToString().TrimEnd());
            }

            current.Clear();
        }

        foreach (string line in Lines(body))
        {
            if (IsFence(line))
            {
                fenced = !fenced;
            }
            else if (!fenced && ListMarker().Match(line) is { Success: true } marker)
            {
                Close();
                current.Append(line[marker.Length..]).Append('\n');
                continue;
            }

            current.Append(line).Append('\n');
        }

        Close();
        return items;
    }

    /// <summary>
    /// One list item's steps: one per command it carries, in the order it carries them, or a
    /// single human step when it carries none. Several commands in one item become several steps
    /// rather than one joined command, because a launch runs each step through a shell that takes
    /// exactly one command line, and because a paused reviewer resuming mid-item would otherwise
    /// have no step number to resume from.
    /// </summary>
    private static IEnumerable<RunSkillStep> StepsFrom(string section, string item, int alreadyNumbered)
    {
        string text = Prose(item);

        // Every human-steps entry is a human step whatever it carries. A step there might well
        // quote a command, but the section exists precisely because the session could not
        // determine something about it, so running the quoted command would be acting on the
        // half of it the document admits it does not have.
        IReadOnlyList<string> commands =
            section.Equals(RunSkillDocument.HumanStepsHeading, StringComparison.OrdinalIgnoreCase)
                ? []
                : Commands(item);
        if (commands.Count == 0)
        {
            return [new RunSkillStep(alreadyNumbered + 1, RunSkillStepKind.Human, section, text, string.Empty)];
        }

        return commands.Select((command, index) =>
            new RunSkillStep(alreadyNumbered + index + 1, RunSkillStepKind.Command, section, text, command));
    }

    /// <summary>
    /// The commands in one list item: every non-blank, non-comment line inside its fenced code
    /// blocks, or — when it has none — every inline backtick span carrying whitespace. A bare
    /// backticked token (<c>docker-compose.yml</c>, <c>Hall9k.slnx</c>) is a name the prose is
    /// pointing at, not something to run, and a one-word command is left to be a human step
    /// rather than guessed at from a token that looks like it might be one.
    /// </summary>
    private static IReadOnlyList<string> Commands(string item)
    {
        List<string> fencedCommands = [];
        bool fenced = false;
        foreach (string line in Lines(item))
        {
            if (IsFence(line))
            {
                fenced = !fenced;
                continue;
            }

            string trimmed = line.Trim();
            if (fenced && trimmed.IsNotBlank() && !trimmed.StartsWith('#'))
            {
                fencedCommands.Add(trimmed);
            }
        }

        if (fencedCommands.Count > 0)
        {
            return fencedCommands;
        }

        return
        [
            .. InlineCode().Matches(item)
                .Select(match => match.Groups[1].Value.Trim())
                .Where(span => span.Any(char.IsWhiteSpace)),
        ];
    }

    /// <summary>The item with its fenced blocks dropped, so a printed step reads as the sentence the document wrote rather than as the sentence plus the command already shown beside it.</summary>
    private static string Prose(string item)
    {
        StringBuilder prose = new();
        bool fenced = false;
        foreach (string line in Lines(item))
        {
            if (IsFence(line))
            {
                fenced = !fenced;
                continue;
            }

            if (!fenced)
            {
                prose.Append(line).Append('\n');
            }
        }

        string written = prose.ToString().Trim();
        return written.IsNotBlank() ? written : item.Trim();
    }

    /// <summary>
    /// Whether this body is a section saying, in the convention the discovery prompt teaches, that
    /// it has nothing in it.
    /// <para>
    /// A section carrying anything that reads as a command is never nothing, whatever its first
    /// word is. "Nothing else is needed; run <c>make dev</c>" opens on one of the words this
    /// matches on and is plainly not an empty section, and dropping it would lose the one launch
    /// command the whole launch depends on — silently, since an empty plan and a plan whose
    /// commands were discarded look identical from here.
    /// </para>
    /// </summary>
    private static bool IsNothing(string body)
    {
        if (Commands(body).Count > 0)
        {
            return false;
        }

        string? first = Lines(body)
            .Select(line => line.Trim().TrimStart('-', '*', '+', '>', ' ').TrimStart('_', '*').Trim())
            .FirstOrDefault(line => line.IsNotBlank());
        return first is null
            || NothingWords.Any(word =>
                first.StartsWith(word, StringComparison.OrdinalIgnoreCase)
                && (first.Length == word.Length || !char.IsLetter(first[word.Length])));
    }

    private static bool IsFence(string line) => line.TrimStart().StartsWith("```", StringComparison.Ordinal);

    private static string[] Lines(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    [GeneratedRegex(@"^\s{0,3}(?:[-*+]|\d+[.)])\s+")]
    private static partial Regex ListMarker();

    [GeneratedRegex("`([^`\n]+)`")]
    private static partial Regex InlineCode();
}
