using Hall9k.Domain.Features.Project.Events;
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

        view.SettingsChangedAt = @event.Data.ChangedAt;
    }
}
