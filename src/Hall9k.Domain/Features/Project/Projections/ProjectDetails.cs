using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Project.Projections;

public sealed class ProjectDetails
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid ConnectionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RepositoryPath { get; set; } = string.Empty;
    public Uri? RepositoryUrl { get; set; }
    public string BaseBranch { get; set; } = string.Empty;
    public bool SkipPermissions { get; set; }
    public int MaxParallelAgents { get; set; } = 3;
    public CommitStyle CommitStyle { get; set; } = CommitStyle.Unknown;
    /// <summary>The project's model default; Unknown defers to the platform chain (Decisions Log #33).</summary>
    public AgentModel Model { get; set; } = AgentModel.Unknown;
    /// <summary>
    /// Whether closeout asks this project's reviewers for another pass after a fix follow-up
    /// pushed (Decisions Log #62). Outranks the owner's preference; Unknown defers to it.
    /// </summary>
    public ReviewRerequestPolicy ReviewRerequest { get; set; } = ReviewRerequestPolicy.Unknown;
    /// <summary>The Jira board this project's cards live on; None when nothing is bound (backlog 18).</summary>
    public JiraProjectKey JiraProjectKey { get; set; } = JiraProjectKey.None;
    /// <summary>
    /// Where this project lives on disk (backlog 47). None for a project registered before homes
    /// existed, or one whose home has not been created on this machine.
    /// </summary>
    public ProjectHome HomeDirectory { get; set; } = ProjectHome.None;
    public List<VerifyCommand> VerifyCommands { get; set; } = [];
    public List<ContextLink> ContextLinks { get; set; } = [];
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? SettingsChangedAt { get; set; }
}

public sealed class ProjectDetailsProjection : SingleStreamProjection<ProjectDetails, Guid>
{
    public ProjectDetails Create(IEvent<ProjectRegistered> @event) => new()
    {
        Id = @event.Data.Id,
        OwnerId = @event.Data.OwnerId,
        ConnectionId = @event.Data.ConnectionId,
        Name = @event.Data.Name,
        RepositoryPath = @event.Data.RepositoryPath,
        RepositoryUrl = @event.Data.RepositoryUrl,
        BaseBranch = @event.Data.BaseBranch,
        HomeDirectory = @event.Data.HomeDirectory ?? ProjectHome.None,
        RegisteredAt = @event.Data.RegisteredAt,
    };

    public void Apply(IEvent<ProjectSettingsChanged> @event, ProjectDetails view)
    {
        if (@event.Data.VerifyCommands.HasValue)
        {
            view.VerifyCommands = [.. @event.Data.VerifyCommands.Value ?? []];
        }

        if (@event.Data.SkipPermissions.HasValue)
        {
            view.SkipPermissions = @event.Data.SkipPermissions.Value;
        }

        if (@event.Data.MaxParallelAgents.HasValue)
        {
            view.MaxParallelAgents = @event.Data.MaxParallelAgents.Value;
        }

        if (@event.Data.ContextLinks.HasValue)
        {
            view.ContextLinks = [.. @event.Data.ContextLinks.Value ?? []];
        }

        if (@event.Data.CommitStyle.HasValue)
        {
            view.CommitStyle = @event.Data.CommitStyle.Value ?? CommitStyle.Unknown;
        }

        if (@event.Data.Model.HasValue)
        {
            view.Model = @event.Data.Model.Value ?? AgentModel.Unknown;
        }

        if (@event.Data.ReviewRerequest.HasValue)
        {
            view.ReviewRerequest = @event.Data.ReviewRerequest.Value ?? ReviewRerequestPolicy.Unknown;
        }

        if (@event.Data.JiraProjectKey.HasValue)
        {
            view.JiraProjectKey = @event.Data.JiraProjectKey.Value ?? JiraProjectKey.None;
        }

        if (@event.Data.HomeDirectory.HasValue)
        {
            view.HomeDirectory = @event.Data.HomeDirectory.Value ?? ProjectHome.None;
        }

        if (@event.Data.RepositoryPath.HasValue && @event.Data.RepositoryPath.Value.IsNotBlank())
        {
            view.RepositoryPath = @event.Data.RepositoryPath.Value;
        }

        view.SettingsChangedAt = @event.Data.ChangedAt;
    }
}
