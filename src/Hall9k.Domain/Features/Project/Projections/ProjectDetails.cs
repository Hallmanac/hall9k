using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Run;
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
    /// <summary>
    /// The retired session-denominated per-project ceiling (Decisions Log #140) — read only to
    /// name the retirement to whoever set it. Nothing appends it and nothing enforces it;
    /// <see cref="MaxParallelTasks"/> is what the dispatcher reads.
    /// </summary>
    public int MaxParallelAgents { get; set; } = ProjectAggregate.LegacyMaxParallelAgentsDefault;
    /// <summary>
    /// This project's own run ceiling in task runs (Decisions Log #140); null is uncapped and 0
    /// is the deliberate pause. <see cref="ProjectRunCeiling"/> owns the semantics, and the
    /// dispatcher re-reads this document every sweep, so a change lands on the next dispatch
    /// cycle with no daemon restart.
    /// </summary>
    public int? MaxParallelTasks { get; set; }
    /// <summary>
    /// Which tier this project's ready work competes in for a free dispatch slot (Decisions Log
    /// #141). Read by the dispatcher's rotation off this document every sweep, exactly as
    /// <see cref="MaxParallelTasks"/> is, so a tier change lands on the next dispatch cycle with
    /// no daemon restart. A document written before this field existed has no key for it and reads
    /// the initialised default, <see cref="ProjectPriority.Normal"/> — which is also the tier that
    /// changes nothing, so no backfill is needed to make an old project schedule as it always did.
    /// </summary>
    public ProjectPriority Priority { get; set; } = ProjectPriority.Normal;
    public CommitStyle CommitStyle { get; set; } = CommitStyle.Unknown;
    /// <summary>The project's model default; Unknown defers to the platform chain (Decisions Log #33).</summary>
    public AgentModel Model { get; set; } = AgentModel.Unknown;
    /// <summary>
    /// This project's orchestrator-window override (task: an operator starts a lean node or
    /// project orchestrator window) — outranks <see cref="Model"/> for the project's
    /// <c>recipes/settings.json</c> alone, so the model dispatched agents run on and the model
    /// the operator's own window runs on can be set independently. Unknown defers to
    /// <see cref="Model"/>, then the node's resolution.
    /// </summary>
    public AgentModel OrchestratorModel { get; set; } = AgentModel.Unknown;
    /// <summary>
    /// Whether closeout asks this project's reviewers for another pass after a fix follow-up
    /// pushed (Decisions Log #62). Outranks the owner's preference; Unknown defers to it.
    /// </summary>
    public ReviewRerequestPolicy ReviewRerequest { get; set; } = ReviewRerequestPolicy.Unknown;
    /// <summary>The Jira board this project's cards live on; None when nothing is bound (backlog 18).</summary>
    public JiraProjectKey JiraProjectKey { get; set; } = JiraProjectKey.None;
    /// <summary>Where a published task's work becomes visible outside Hall9k; None is the platform's original behavior.</summary>
    public BacklogPolicy BacklogPolicy { get; set; } = BacklogPolicy.None;
    /// <summary>Free-text routing guidance handed verbatim to the Jira agent; a label list for github-issues.</summary>
    public string? BacklogRoutingGuidance { get; set; }
    /// <summary>This project's override of the conformance review track's cycle cap; null defers to the node (Decisions Log #63).</summary>
    public int? MaxComplianceReviewCycles { get; set; }
    /// <summary>This project's override of the adversarial review track's cycle cap; null defers to the node.</summary>
    public int? MaxAdversarialReviewCycles { get; set; }
    /// <summary>This project's override of the mandatory final-full-pass round cap; null defers to the node.</summary>
    public int? MaxFinalFullPassRounds { get; set; }
    /// <summary>This project's override of the task-lifetime review-cycle budget; null defers to the node.</summary>
    public int? LifetimeReviewCycleBudget { get; set; }
    /// <summary>
    /// This project's override of which pre-PR review stages a run gets (task: the review
    /// pipeline's stage composition becomes configuration recorded per run); null defers to the
    /// node. Task overrides this project value; this project value overrides the node.
    /// </summary>
    public ReviewStageComposition? ReviewStageComposition { get; set; }
    /// <summary>
    /// The name this project's task branches are cut under; the default renders exactly the
    /// <c>task/&lt;shortid&gt;-&lt;slug&gt;</c> name the platform cut before templates existed.
    /// </summary>
    public BranchNameTemplate BranchNameTemplate { get; set; } = BranchNameTemplate.Default;
    /// <summary>
    /// The last auto-pr-review speed this project's stream recorded — never the effective one
    /// (idea e5e98a33, Decisions Log #161). This document is serialised whole on every write, so
    /// the initialised default below has been stored under this key since a project's very first
    /// event and cannot be told apart from the same value chosen deliberately. Read
    /// <see cref="AutoPrReviewSetting"/> instead wherever the effective value or its origin
    /// matters; it resolves from the stream, which is the only honest record of what an operator
    /// chose.
    /// </summary>
    public AutoPrReviewSpeed AutoPrReview { get; set; } = AutoPrReviewSpeed.Off;
    /// <summary>
    /// What has to be true on this install before a task linked to a Jira card or a GitHub issue
    /// may be claimed here (idea 64c75e43); Off is the platform's original behavior.
    /// </summary>
    public ClaimGate ClaimGate { get; set; } = ClaimGate.Off;
    /// <summary>
    /// Whether true closeout closes this project's tasks' linked GitHub issues, and when (task: a
    /// task's linked GitHub issue is closed at true closeout under a configurable rule); a
    /// document written before this feature existed has no key for it and reads the initialised
    /// default, <see cref="CloseLinkedIssueRule.WhenAllTasksClose"/> — the same "no backfill
    /// needed" reasoning <see cref="Priority"/> already documents.
    /// </summary>
    public CloseLinkedIssueRule CloseLinkedIssue { get; set; } = CloseLinkedIssueRule.WhenAllTasksClose;
    /// <summary>A label list that forces <see cref="CloseLinkedIssueRule.Never"/> for an issue carrying any of them at closeout time.</summary>
    public List<string> NeverCloseLabels { get; set; } = [];
    /// <summary>
    /// How prose an agent composes for people has to read on this project (task: every piece of
    /// prose the daemon posts to GitHub under the owner's login obeys the project's writing
    /// conventions). A document written before this field existed has no key for it and reads the
    /// initialised default, <see cref="Project.WritingConventions.Default"/>, which is also the
    /// value that changes nothing, so no backfill is needed; the same reasoning
    /// <see cref="Priority"/> already documents.
    /// </summary>
    public WritingConventions WritingConventions { get; set; } = WritingConventions.Default;
    /// <summary>
    /// Where this project lives on disk (backlog 47). None for a project registered before homes
    /// existed, or one whose home has not been created on this machine.
    /// </summary>
    public ProjectHome HomeDirectory { get; set; } = ProjectHome.None;
    public List<VerifyCommand> VerifyCommands { get; set; } = [];
    public List<ContextLink> ContextLinks { get; set; } = [];
    /// <summary>This project's launch text, one per agent CLI (task: an operator starts a lean orchestrator window).</summary>
    public List<LaunchText> LaunchTexts { get; set; } = [];
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? SettingsChangedAt { get; set; }
    /// <summary>
    /// Whether this project is archived on this install (task: a project can be archived, listed
    /// as archived, reactivated, and renamed). Named for the purge follow-up to build on: the
    /// second half of this design schedules a hard delete off this same flag rather than a new one.
    /// </summary>
    public bool IsArchived { get; set; }
    /// <summary>When this project was archived; null while it never has been or after reactivation.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }
    /// <summary>Why this project was archived; left unknown when omitted, never inferred.</summary>
    public string? ArchivedReason { get; set; }
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
        SkipPermissions = @event.Data.SkipPermissions,
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

        if (@event.Data.MaxParallelTasks.HasValue)
        {
            view.MaxParallelTasks = @event.Data.MaxParallelTasks.Value;
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

        if (@event.Data.OrchestratorModel.HasValue)
        {
            view.OrchestratorModel = @event.Data.OrchestratorModel.Value ?? AgentModel.Unknown;
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

        if (@event.Data.BacklogPolicy.HasValue)
        {
            view.BacklogPolicy = @event.Data.BacklogPolicy.Value ?? BacklogPolicy.None;
        }

        if (@event.Data.BacklogRoutingGuidance.HasValue)
        {
            view.BacklogRoutingGuidance = @event.Data.BacklogRoutingGuidance.Value.IsBlank()
                ? null
                : @event.Data.BacklogRoutingGuidance.Value;
        }

        if (@event.Data.MaxComplianceReviewCycles.HasValue)
        {
            view.MaxComplianceReviewCycles = @event.Data.MaxComplianceReviewCycles.Value;
        }

        if (@event.Data.MaxAdversarialReviewCycles.HasValue)
        {
            view.MaxAdversarialReviewCycles = @event.Data.MaxAdversarialReviewCycles.Value;
        }

        if (@event.Data.MaxFinalFullPassRounds.HasValue)
        {
            view.MaxFinalFullPassRounds = @event.Data.MaxFinalFullPassRounds.Value;
        }

        if (@event.Data.LifetimeReviewCycleBudget.HasValue)
        {
            view.LifetimeReviewCycleBudget = @event.Data.LifetimeReviewCycleBudget.Value;
        }

        if (@event.Data.ReviewStageComposition.HasValue)
        {
            view.ReviewStageComposition = @event.Data.ReviewStageComposition.Value;
        }

        if (@event.Data.BranchNameTemplate.HasValue)
        {
            view.BranchNameTemplate = @event.Data.BranchNameTemplate.Value ?? BranchNameTemplate.Default;
        }

        if (@event.Data.AutoPrReview.HasValue)
        {
            view.AutoPrReview = @event.Data.AutoPrReview.Value ?? AutoPrReviewSpeed.Off;
        }

        if (@event.Data.Priority.HasValue)
        {
            view.Priority = @event.Data.Priority.Value ?? ProjectPriority.Normal;
        }

        if (@event.Data.ClaimGate.HasValue)
        {
            view.ClaimGate = @event.Data.ClaimGate.Value ?? ClaimGate.Off;
        }

        if (@event.Data.LaunchTexts.HasValue)
        {
            view.LaunchTexts = [.. @event.Data.LaunchTexts.Value ?? []];
        }

        if (@event.Data.CloseLinkedIssue.HasValue)
        {
            view.CloseLinkedIssue = @event.Data.CloseLinkedIssue.Value ?? CloseLinkedIssueRule.WhenAllTasksClose;
        }

        if (@event.Data.NeverCloseLabels.HasValue)
        {
            view.NeverCloseLabels = [.. @event.Data.NeverCloseLabels.Value ?? []];
        }

        if (@event.Data.WritingConventions.HasValue)
        {
            view.WritingConventions = @event.Data.WritingConventions.Value ?? WritingConventions.Default;
        }

        view.SettingsChangedAt = @event.Data.ChangedAt;
    }

    public void Apply(IEvent<ProjectArchived> @event, ProjectDetails view)
    {
        view.IsArchived = true;
        view.ArchivedAt = @event.Data.ArchivedAt;
        view.ArchivedReason = @event.Data.Reason;
    }

    public void Apply(IEvent<ProjectReactivated> @event, ProjectDetails view)
    {
        view.IsArchived = false;
        view.ArchivedAt = null;
        view.ArchivedReason = null;
    }

    public void Apply(IEvent<ProjectRenamed> @event, ProjectDetails view)
    {
        view.Name = @event.Data.NewName;
    }
}
