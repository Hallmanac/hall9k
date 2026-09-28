using System.Globalization;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The one line naming where a project's current run skill came from — its recording node, the
/// <c>Author</c> vocabulary value, the commit it was composed against (or that none was recorded),
/// and when — shared by <c>h9k project run-skill show</c> and <c>h9k task run-local</c>'s own
/// pre-execution confirmation (idea b9b09779 piece 4/5, security review idea 6be68ee2,
/// process-injection finding 3). An operator approving a changed plan needs to know who composed it
/// before deciding whether to trust it, and a reader of the audit trail on its own show command
/// needs the identical answer — one function rather than two renderings that drift.
/// </summary>
internal static class RunSkillProvenanceDisplay
{
    /// <summary>
    /// The markup-ready line, or empty when <paramref name="project"/> has no run skill recorded at
    /// all — a caller that already refused over a missing run skill never reaches here, but this
    /// stays honest rather than throwing for one that somehow does.
    /// </summary>
    public static async Task<string> LineAsync(
        IQuerySession session, ProjectDetails project, Guid? thisNodeId, CancellationToken cancellationToken)
    {
        if (project.RunSkill is not { } skill)
        {
            return string.Empty;
        }

        string nodeLabel = project.RunSkillRecordedOnNodeId switch
        {
            { } recordingNode when thisNodeId is { } mine && recordingNode == mine => "this node",
            { } recordingNode => MemberLabelling.NodeMarkup(
                recordingNode, await MemberLabelling.LoadAsync(session, project.Id, cancellationToken)),
            null => "a node nobody observed",
        };

        return $"Composed by {skill.Author.Value.EscapeMarkup()} on {nodeLabel}, "
            + $"{skill.RecordedAt.ToString("u", CultureInfo.InvariantCulture)}, against "
            + (skill.ComposedAgainstCommit.IsNotBlank()
                ? $"commit {skill.ComposedAgainstCommit.EscapeMarkup()}"
                : "none recorded") + ".";
    }
}
