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
    /// <summary>
    /// Whether closeout asks this project's reviewers for another pass after a fix follow-up
    /// pushed (Decisions Log #62). Outranks the owner's preference; Unknown defers to it.
    /// </summary>
    public ReviewRerequestPolicy ReviewRerequest { get; private set; } = ReviewRerequestPolicy.Unknown;
    /// <summary>The Jira board this project's cards live on; None when nothing is bound (backlog 18).</summary>
    public JiraProjectKey JiraProjectKey { get; private set; } = JiraProjectKey.None;
    /// <summary>
    /// Where this project lives on disk (backlog 47): the directory holding the generated
    /// AGENTS.md, repo/, ideas/, tasks/ and skills/. None for a project registered before homes
    /// existed, or one whose home has not been created on this machine — h9k project init is
    /// what ends that state.
    /// </summary>
    public ProjectHome HomeDirectory { get; private set; } = ProjectHome.None;
    /// <summary>Where a published task's work becomes visible outside Hall9k; None is the platform's original behavior.</summary>
    public BacklogPolicy BacklogPolicy { get; private set; } = BacklogPolicy.None;
    /// <summary>Free-text routing guidance handed verbatim to the Jira agent; a label list for github-issues.</summary>
    public string? BacklogRoutingGuidance { get; private set; }
    /// <summary>This project's override of the conformance review track's cycle cap; null defers to the node (Decisions Log #63).</summary>
    public int? MaxComplianceReviewCycles { get; private set; }
    /// <summary>This project's override of the adversarial review track's cycle cap; null defers to the node.</summary>
    public int? MaxAdversarialReviewCycles { get; private set; }
    /// <summary>This project's override of the mandatory final-full-pass round cap; null defers to the node.</summary>
    public int? MaxFinalFullPassRounds { get; private set; }
    /// <summary>This project's override of the task-lifetime review-cycle budget; null defers to the node.</summary>
    public int? LifetimeReviewCycleBudget { get; private set; }
    /// <summary>
    /// The name this project's task branches are cut under; the default renders exactly the
    /// <c>task/&lt;shortid&gt;-&lt;slug&gt;</c> name the platform cut before templates existed.
    /// </summary>
    public BranchNameTemplate BranchNameTemplate { get; private set; } = BranchNameTemplate.Default;
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
        HomeDirectory = @event.HomeDirectory ?? ProjectHome.None;
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

        if (@event.ReviewRerequest.HasValue)
        {
            ReviewRerequest = @event.ReviewRerequest.Value ?? ReviewRerequestPolicy.Unknown;
        }

        if (@event.JiraProjectKey.HasValue)
        {
            JiraProjectKey = @event.JiraProjectKey.Value ?? JiraProjectKey.None;
        }

        if (@event.HomeDirectory.HasValue)
        {
            HomeDirectory = @event.HomeDirectory.Value ?? ProjectHome.None;
        }

        if (@event.RepositoryPath.HasValue && @event.RepositoryPath.Value.IsNotBlank())
        {
            RepositoryPath = @event.RepositoryPath.Value;
        }

        if (@event.BacklogPolicy.HasValue)
        {
            BacklogPolicy = @event.BacklogPolicy.Value ?? BacklogPolicy.None;
        }

        if (@event.BacklogRoutingGuidance.HasValue)
        {
            BacklogRoutingGuidance = @event.BacklogRoutingGuidance.Value.IsBlank() ? null : @event.BacklogRoutingGuidance.Value;
        }

        if (@event.MaxComplianceReviewCycles.HasValue)
        {
            MaxComplianceReviewCycles = @event.MaxComplianceReviewCycles.Value;
        }

        if (@event.MaxAdversarialReviewCycles.HasValue)
        {
            MaxAdversarialReviewCycles = @event.MaxAdversarialReviewCycles.Value;
        }

        if (@event.MaxFinalFullPassRounds.HasValue)
        {
            MaxFinalFullPassRounds = @event.MaxFinalFullPassRounds.Value;
        }

        if (@event.LifetimeReviewCycleBudget.HasValue)
        {
            LifetimeReviewCycleBudget = @event.LifetimeReviewCycleBudget.Value;
        }

        if (@event.BranchNameTemplate.HasValue)
        {
            BranchNameTemplate = @event.BranchNameTemplate.Value ?? BranchNameTemplate.Default;
        }
    }
}
