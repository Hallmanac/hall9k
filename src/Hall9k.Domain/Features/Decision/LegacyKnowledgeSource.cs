using System.Reflection;

namespace Hall9k.Domain.Features.Decision;

/// <summary>
/// The two markdown files the one-time import reads (idea d805fd8b, piece 3), carried inside this
/// assembly rather than looked up in a checkout.
/// <para>
/// <b>Why embedded.</b> The same change that adds this import is the change that deletes PLAN.md
/// §16 and AGENTS.md's rule paragraphs, so by the time anybody runs the import the working tree no
/// longer has the text to read. Embedding the snapshot makes the input frozen, identical on every
/// node, and readable with no repository present at all, which is also what lets the parser tests
/// run against the real section without touching a repository (Brian's 2026-09-13 testing rule).
/// <b>Does this block the later vision?</b> No: nothing else in the platform reads these
/// resources, and the day the last node has imported they are an archive that can be deleted.
/// </para>
/// <para>
/// Line endings are normalised on the way out. Both files are stored with LF, but a Windows
/// checkout of this repository converts them to CRLF before the build embeds them, so the same
/// resource is genuinely different bytes on a Windows node and a Mac one. Normalising here means
/// a statement recorded by whichever node ran the import reads the same either way, rather than
/// the imported text carrying its importer's operating system in it.
/// </para>
/// </summary>
public static class LegacyKnowledgeSource
{
    /// <summary>
    /// PLAN.md §16 verbatim, as of commit cba329cb, with two omissions the file's own header
    /// comment records: the mechanical placement note commit 06471890 appended to entry #265 when
    /// it assigned that number, and the rule closing the section.
    /// </summary>
    public const string DecisionsLogResource = "Hall9k.Domain.Legacy.v0-decisions-log.md";

    /// <summary>AGENTS.md's Git rules and Working agreements sections verbatim, as of commit cba329cb (the same commit the log snapshot was taken at, and the one that file's own header comment names), less the one bullet that header records.</summary>
    public const string StandingRulesResource = "Hall9k.Domain.Legacy.agents-standing-rules.md";

    public static string DecisionsLog() => Read(DecisionsLogResource);

    public static string StandingRules() => Read(StandingRulesResource);

    private static string Read(string resourceName)
    {
        using Stream stream = typeof(LegacyKnowledgeSource).GetTypeInfo().Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The embedded legacy knowledge resource '{resourceName}' is missing from Hall9k.Domain. "
                + "It is declared in Hall9k.Domain.csproj with an explicit LogicalName; a rename of the "
                + "file without a matching edit there is what breaks this.");

        using StreamReader reader = new(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
