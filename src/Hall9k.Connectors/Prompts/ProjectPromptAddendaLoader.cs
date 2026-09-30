using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// One addendum as a prompt builder actually reads it: the text to splice in, and whether it was
/// set over this project's own length cap (<see cref="OverCapMarker"/>) — so a builder that
/// appends one can name that in what it composes, the "log" acceptance criterion 3 asks for, since
/// none of these builders hold a logger of their own.
/// </summary>
public sealed record LoadedPromptAddendum(string Content, bool OverCap);

/// <summary>
/// Where every prompt builder actually reads a project's own addendum from (idea b9b09779, piece
/// 6): the daemon's local materialized copy of the ledger, never <see cref="ProjectDetails"/>'s own
/// audit trail and never the ledger itself. A dispatched work or review session — the composing
/// callers below — has no business holding a live ledger fetch, and a fellow member's node has no
/// other way to learn this project set an addendum before the distributed-team chain ships: the
/// ledger, fetched and materialized here by <c>Hall9k.Daemon</c>'s own sweep, is what already
/// reaches every member's node today.
/// </summary>
public static class ProjectPromptAddendaLoader
{
    /// <summary>
    /// The first line <c>Hall9k.Daemon.PromptAddenda.PromptAddendaSweepEngine</c> writes ahead of an addendum's own text
    /// when it went in over the cap — an HTML comment, so it is invisible wherever the markdown
    /// renders but still a plain, greppable fact in the file itself. Never written by anything else:
    /// an addendum's own text is free to start with any line, including this one's exact words, and
    /// a project that does so gets read as over-cap regardless — an honest, harmless
    /// misclassification rather than a byte the daemon has to escape.
    /// </summary>
    public const string OverCapMarker = "<!-- hall9k:over-cap -->";

    /// <summary>
    /// The line appended after an addendum cut at <see cref="ProjectDecider.PromptAddendumHardMaximumLength"/>,
    /// so the agent reading the prompt is told its guidance is incomplete.
    /// </summary>
    public static readonly string TruncationLabel =
        $"[Hall9k truncated this addendum at its hard limit of {ProjectDecider.PromptAddendumHardMaximumLength} characters; the rest was not read.]";

    /// <summary>
    /// The addendum for <paramref name="builder"/>, or null when this project has none, has no
    /// home yet, or the daemon has not materialized one here yet. Never throws on a missing file:
    /// an addendum is optional by definition, so its absence is an ordinary outcome, not a failure.
    /// </summary>
    public static LoadedPromptAddendum? TryLoad(ProjectDetails project, PromptBuilderKey builder)
    {
        if (!project.HomeDirectory.HasValue)
        {
            return null;
        }

        string path = ProjectHomePaths.PromptAddendumFile(project.HomeDirectory.Value, builder);
        if (!File.Exists(path))
        {
            return null;
        }

        string raw = File.ReadAllText(path);
        bool overCap = raw.StartsWith(OverCapMarker, StringComparison.Ordinal);
        string content = (overCap ? raw[OverCapMarker.Length..] : raw).TrimStart('\r', '\n');
        return content.IsBlank()
            ? null
            : new LoadedPromptAddendum(Truncate(content), overCap);
    }

    /// <summary>
    /// The set-time ceiling enforced again on load, so it only ever bites a forged or replicated
    /// file: the materialized file is unsigned and its over-cap marker is forgeable.
    /// </summary>
    private static string Truncate(string content)
    {
        if (content.Length <= ProjectDecider.PromptAddendumHardMaximumLength)
        {
            return content;
        }

        int end = ProjectDecider.PromptAddendumHardMaximumLength;
        if (char.IsHighSurrogate(content[end - 1]))
        {
            end--;
        }

        return $"{content[..end].TrimEnd()}\n\n{TruncationLabel}";
    }
}
