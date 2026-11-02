using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Domain.Features.Plan;

/// <summary>
/// Where a plan's discovery report lives in an idea's workspace, asked the same way by the CLI (start, show, pursue) and the
/// daemon (dispatch, copy, refinement). The directory is recomputed from the idea's current note every time, because the
/// render sweep renames an idea's directory when the note's slug changes (<see cref="IdeaPaths.ResolveDirectory"/> tracks it
/// at read time), so a path remembered from dispatch can be stale by the time the report lands.
/// </summary>
public static class DiscoveryWorkspace
{
    /// <summary>
    /// The idea's workspace directory as this node would name it, or null when the idea's home was recorded on another
    /// node's operating system, where no directory of this host's is meant (the syntax of a foreign path read as this host's
    /// would fabricate one). It does not say the directory exists, and nothing here creates it.
    /// </summary>
    public static string? DirectoryOf(IdeaAggregate idea) =>
        idea.WorkspaceHome is { HasValue: true, IsNativeForm: false }
            ? null
            : IdeaPaths.WorkspaceDirectory(
                IdeaPaths.ResolveDirectory(idea.WorkspaceHome, ProjectHomePaths.EntryDirectoryName(idea.Id, idea.Text), idea.Id));

    /// <summary>Whether the idea has a workspace directory on this node.</summary>
    public static bool Exists(IdeaAggregate idea) => DirectoryOf(idea) is { } directory && Directory.Exists(directory);

    /// <summary>The file name a plan's report is copied to: the plan's short id keeps one plan's report from another's.</summary>
    public static string ReportFileName(Guid planId) => $"discovery-{DomainId.Short(planId)}.md";

    /// <summary>The path of the plan's report in the idea's workspace now, or null when the idea has no workspace path on this node.</summary>
    public static string? ReportPathOf(IdeaAggregate idea, Guid planId) =>
        DirectoryOf(idea) is { } directory
            ? Path.Combine(directory, ReportFileName(planId))
            : null;
}
