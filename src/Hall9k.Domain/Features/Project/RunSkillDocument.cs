using System.Text;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// The one shape every project's run skill shares (idea b9b09779, piece 4), and the mechanics for
/// composing and checking a document in it. Stated here, in code, rather than left to the prompt
/// alone: a reader standing a project up on a machine they have never seen it on reads the same
/// six sections in the same order whatever the project is, so the sections are a contract the
/// daemon holds a composing session to, not a suggestion the prose makes.
/// <para>
/// The first line is composed here too, never taken from the session (criterion 3: the skill's
/// first line states which shape it is). The daemon already knows the shape — it read it off the
/// session's own trailer, or, for <see cref="RunSkillShape.NoneDiscoverable"/>, decided it
/// mechanically before any session ran — so writing the line from that value is strictly more
/// reliable than parsing a sentence the session was asked to type.
/// </para>
/// </summary>
public static class RunSkillDocument
{
    /// <summary>
    /// The six headings, in order. A composing session is given exactly this list and the daemon
    /// checks its answer against it (<see cref="MissingHeadings"/>) before recording anything: a
    /// document missing sections is not a run skill in this shape, and recording one anyway would
    /// make "one shape every project shares" a claim nothing enforces.
    /// </summary>
    public static readonly IReadOnlyList<string> Headings =
    [
        PrerequisitesHeading,
        OneTimeSetupHeading,
        LaunchHeading,
        HowToKnowItIsUpHeading,
        AddressOrEntryPointHeading,
        HumanStepsHeading,
    ];

    /// <summary>What has to already be on the machine, and how to check each one.</summary>
    public const string PrerequisitesHeading = "Prerequisites";

    /// <summary>Everything done once per machine or per clone, in order.</summary>
    public const string OneTimeSetupHeading = "One-time setup";

    /// <summary>
    /// The command or commands that actually start it. Named rather than indexed because it is
    /// the one section whose steps a local launch leaves running rather than waiting on
    /// (<see cref="RunSkillSteps"/>, idea b9b09779 piece 5).
    /// </summary>
    public const string LaunchHeading = "Launch";

    /// <summary>The observable signal that it came up.</summary>
    public const string HowToKnowItIsUpHeading = "How to know it is up";

    /// <summary>Where a reader goes to actually use it.</summary>
    public const string AddressOrEntryPointHeading = "Address or entry point";

    /// <summary>Everything the composing session could not determine, never guessed at.</summary>
    public const string HumanStepsHeading = "Human steps";

    /// <summary>
    /// What every run skill's first line starts with. A stable prefix so a reader — and
    /// <see cref="StripShapeLine"/>, when a session typed one for itself — can find the
    /// declaration without parsing the sentence after it.
    /// </summary>
    public const string ShapeLinePrefix = "Run skill shape:";

    /// <summary>The first line for a shape: the prefix, the shape's own value, and what that means for the reader.</summary>
    public static string FirstLine(RunSkillShape shape) => shape.Value switch
    {
        "pointer" => $"{ShapeLinePrefix} pointer. This repository already documents how to launch it; "
            + "the steps below point at those files by path and add only what they leave out.",
        "full-text" => $"{ShapeLinePrefix} full-text. This repository documents no launch procedure, "
            + "so the steps below hold the whole of it and cite the file each one derives from.",
        "none-discoverable" => $"{ShapeLinePrefix} none-discoverable. Nothing in this repository says "
            + "how to run it, and nothing below is a guess at how it might be run.",
        _ => $"{ShapeLinePrefix} unknown.",
    };

    /// <summary>
    /// The document as it lands on the ledger: this shape's own first line, a blank line, then the
    /// body with any shape line the composer typed for itself removed, so the declaration is never
    /// stated twice and never states something the recorded shape disagrees with.
    /// </summary>
    public static string Compose(RunSkillShape shape, string body)
    {
        StringBuilder document = new();
        document.AppendLine(FirstLine(shape));
        document.AppendLine();
        document.AppendLine(StripShapeLine(body).TrimEnd());
        return document.ToString();
    }

    /// <summary>
    /// Which of <see cref="Headings"/> the text does not carry, in order — empty when it carries
    /// them all. Matched on the heading words rather than on an exact markdown level, so a
    /// composer that wrote <c>## Launch</c> where another wrote <c>### Launch</c> is not refused
    /// over a hash: the contract is that the section is there and named, not how deeply it nests.
    /// </summary>
    public static IReadOnlyList<string> MissingHeadings(string? text)
    {
        string[] lines = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        List<string> present = [.. lines
            .Select(line => line.TrimStart())
            .Where(line => line.StartsWith('#'))
            .Select(line => line.TrimStart('#').Trim())];

        return
        [
            .. Headings.Where(heading =>
                !present.Any(line => line.Equals(heading, StringComparison.OrdinalIgnoreCase))),
        ];
    }

    /// <summary>
    /// The whole document for a repository the survey found nothing to read in — composed here
    /// rather than by a session, because there is nothing for a session to read and dispatching
    /// one to say so would spend a session to learn what the survey already knows.
    /// <paramref name="lookedFor"/> is what the survey actually looked for, named so a human
    /// reading this can tell "there is nothing here" apart from "the platform looked in the wrong
    /// place".
    /// <para>
    /// Everything it names is repository-relative, and the local directory the survey actually
    /// walked is deliberately not in it: this document lands on a project-scoped event and in the
    /// ledger file every member reads, so a path that exists only on the node that ran the survey
    /// would make a team fact node-specific and put one machine's filesystem layout on everybody
    /// else's disk (independent pre-PR review, cycle 1, adversarial lens).
    /// </para>
    /// </summary>
    public static string ComposeNoneDiscoverable(IReadOnlyList<string> lookedFor)
    {
        StringBuilder body = new();
        Section(body, Headings[0], "Unknown. Nothing in this repository states any.");
        Section(body, Headings[1], "Unknown. Nothing in this repository states any.");
        Section(
            body, Headings[2],
            "Unknown. A survey of this repository found no README, documentation, agent briefing, "
            + "skill, or build file that says how to launch it, so there is no launch command to "
            + "state and none is guessed at here.");
        Section(body, Headings[3], "Unknown, since there is no launch to observe.");
        Section(body, Headings[4], "Unknown, since there is no launch to observe.");
        Section(
            body, Headings[5],
            "Everything. Somebody who knows how this project runs has to say so, either by writing it "
            + "into the repository and asking for discovery again "
            + "(h9k project set PROJECT --discover-run-skill) or by setting the skill by hand "
            + "(h9k project run-skill set PROJECT --file PATH --shape full-text).");

        body.AppendLine("What the survey looked for in this repository and did not find:");
        body.AppendLine();
        foreach (string entry in lookedFor)
        {
            body.AppendLine($"- {entry}");
        }

        return Compose(RunSkillShape.NoneDiscoverable, body.ToString());
    }

    private static void Section(StringBuilder body, string heading, string text)
    {
        body.AppendLine($"## {heading}");
        body.AppendLine();
        body.AppendLine(text);
        body.AppendLine();
    }

    /// <summary>
    /// The body with a leading <see cref="ShapeLinePrefix"/> line dropped. Only ever the FIRST
    /// non-blank line: a later line that happens to quote the prefix (this skill explaining its
    /// own contract, say) is the body's own content and is left exactly as written.
    /// </summary>
    private static string StripShapeLine(string body)
    {
        string[] lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int first = Array.FindIndex(lines, line => line.Trim().Length > 0);
        return first >= 0 && lines[first].TrimStart().StartsWith(ShapeLinePrefix, StringComparison.OrdinalIgnoreCase)
            ? string.Join('\n', lines[(first + 1)..]).Trim('\n')
            : string.Join('\n', lines).Trim('\n');
    }
}
