using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Project;

public sealed class ProjectAggregate
{
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string RepositoryPath { get; private set; } = string.Empty;
    public Uri? RepositoryUrl { get; private set; }
    public string BaseBranch { get; private set; } = string.Empty;
    public bool SkipPermissions { get; private set; }
    public int MaxParallelAgents { get; private set; } = 3;
    public CommitStyle CommitStyle { get; private set; } = CommitStyle.Unknown;
    /// <summary>The project's model default; Unknown defers to the platform chain (Decisions Log #33).</summary>
    public AgentModel Model { get; private set; } = AgentModel.Unknown;
    public DateTimeOffset RegisteredAt { get; private set; }

    private readonly List<VerifyCommand> _verifyCommands = [];
    public IReadOnlyList<VerifyCommand> VerifyCommands => _verifyCommands;

    private readonly List<ContextLink> _contextLinks = [];
    public IReadOnlyList<ContextLink> ContextLinks => _contextLinks;

    public void Apply(ProjectRegistered @event)
    {
        Id = @event.Id;
        OwnerId = @event.OwnerId;
        ConnectionId = @event.ConnectionId;
        Name = @event.Name;
        RepositoryPath = @event.RepositoryPath;
        RepositoryUrl = @event.RepositoryUrl;
        BaseBranch = @event.BaseBranch;
        RegisteredAt = @event.RegisteredAt;
    }

    public void Apply(ProjectSettingsChanged @event)
    {
        if (@event.VerifyCommands.HasValue)
        {
            _verifyCommands.Clear();
            _verifyCommands.AddRange(@event.VerifyCommands.Value ?? []);
        }

        if (@event.SkipPermissions.HasValue)
        {
            SkipPermissions = @event.SkipPermissions.Value;
        }

        if (@event.MaxParallelAgents.HasValue)
        {
            MaxParallelAgents = @event.MaxParallelAgents.Value;
        }

        if (@event.ContextLinks.HasValue)
        {
            _contextLinks.Clear();
            _contextLinks.AddRange(@event.ContextLinks.Value ?? []);
        }

        if (@event.CommitStyle.HasValue)
        {
            CommitStyle = @event.CommitStyle.Value ?? CommitStyle.Unknown;
        }

        if (@event.Model.HasValue)
        {
            Model = @event.Model.Value ?? AgentModel.Unknown;
        }
    }
}
