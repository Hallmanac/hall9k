using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Run;
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
    /// <summary>
    /// The retired session-denominated per-project ceiling (Decisions Log #140), replayed off
    /// streams that recorded one so <c>h9k project show</c>/<c>h9k project set</c> can name the
    /// retirement to whoever set it. Nothing appends it any more and nothing enforces it;
    /// <see cref="MaxParallelTasks"/> is the setting the dispatcher reads.
    /// <see cref="LegacyMaxParallelAgentsDefault"/> is what an untouched project reads, and the
    /// one value a recorded setting is indistinguishable from.
    /// </summary>
    public int MaxParallelAgents { get; private set; } = LegacyMaxParallelAgentsDefault;
    /// <summary>
    /// This project's own run ceiling in task runs (Decisions Log #140): how many of its runs the
    /// dispatcher may hold live at once. Null is uncapped — the node ceiling alone decides, the
    /// behaviour every project had before this setting existed — and 0 is the deliberate pause.
    /// <see cref="ProjectRunCeiling"/> owns what those values mean.
    /// </summary>
    public int? MaxParallelTasks { get; private set; }

    /// <summary>
    /// Which tier this project's ready work competes in for a free dispatch slot (Decisions Log
    /// #141). <see cref="ProjectPriority.Normal"/> on every project that never set one — the tier
    /// the whole rotation runs in by default — and <see cref="ProjectPriority"/> owns what a higher
    /// tier means and why it releases itself.
    /// </summary>
    public ProjectPriority Priority { get; private set; } = ProjectPriority.Normal;

    /// <summary>
    /// What <see cref="MaxParallelAgents"/> reads on a project that never recorded one. A
    /// recorded 3 is indistinguishable from this default — deliberately not worked around,
    /// because retiring a 3 and retiring an absence come to the same enforced behaviour
    /// (uncapped), so the notice that would tell them apart has nothing to add.
    /// </summary>
    public const int LegacyMaxParallelAgentsDefault = 3;
    public CommitStyle CommitStyle { get; private set; } = CommitStyle.Unknown;
    /// <summary>The project's model default; Unknown defers to the platform chain (Decisions Log #33).</summary>
    public AgentModel Model { get; private set; } = AgentModel.Unknown;
    /// <summary>
    /// This project's orchestrator-window override (task: an operator starts a lean node or
    /// project orchestrator window) — outranks <see cref="Model"/> for the project's
    /// <c>recipes/settings.json</c> alone. Unknown defers to <see cref="Model"/>, then the node's
    /// resolution.
    /// </summary>
    public AgentModel OrchestratorModel { get; private set; } = AgentModel.Unknown;
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
    /// This project's override of which pre-PR review stages a run gets (task: the review
    /// pipeline's stage composition becomes configuration recorded per run); null defers to the
    /// node. Task overrides this project value; this project value overrides the node.
    /// </summary>
    public ReviewStageComposition? ReviewStageComposition { get; private set; }
    /// <summary>
    /// The name this project's task branches are cut under; the default renders exactly the
    /// <c>task/&lt;shortid&gt;-&lt;slug&gt;</c> name the platform cut before templates existed.
    /// </summary>
    public BranchNameTemplate BranchNameTemplate { get; private set; } = BranchNameTemplate.Default;
    /// <summary>
    /// The last auto-pr-review speed this project's stream recorded (idea e5e98a33). Off here is
    /// the replay of a stream that recorded nothing as much as one that recorded an opt-out, so
    /// this is not the effective setting: <see cref="AutoPrReviewSetting"/> resolves that, and
    /// Decisions Log #161 says why the difference matters.
    /// </summary>
    public AutoPrReviewSpeed AutoPrReview { get; private set; } = AutoPrReviewSpeed.Off;
    /// <summary>
    /// What has to be true on this install before a task linked to a Jira card or a GitHub issue
    /// may be claimed here (idea 64c75e43); Off is the platform's original behavior.
    /// </summary>
    public ClaimGate ClaimGate { get; private set; } = ClaimGate.Off;
    /// <summary>
    /// Whether true closeout closes this project's tasks' linked GitHub issues, and when (task: a
    /// task's linked GitHub issue is closed at true closeout under a configurable rule);
    /// <see cref="CloseLinkedIssueRule.WhenAllTasksClose"/> is what a new project starts with.
    /// </summary>
    public CloseLinkedIssueRule CloseLinkedIssue { get; private set; } = CloseLinkedIssueRule.WhenAllTasksClose;
    /// <summary>
    /// How prose an agent composes for people has to read on this project; the platform default
    /// until an operator states their own (<see cref="Project.WritingConventions"/>).
    /// </summary>
    public WritingConventions WritingConventions { get; private set; } = WritingConventions.Default;
    public DateTimeOffset RegisteredAt { get; private set; }
    /// <summary>
    /// Whether this project is archived on this install (task: a project can be archived, listed
    /// as archived, reactivated, and renamed). Archiving is reversible and per-install — it never
    /// touches the home directory on disk, never deletes anything, and says nothing about whether
    /// the same repository is registered on another node.
    /// </summary>
    public bool IsArchived { get; private set; }
    /// <summary>When this project was archived; null while it never has been or after reactivation clears it.</summary>
    public DateTimeOffset? ArchivedAt { get; private set; }
    /// <summary>Why this project was archived; left unknown when omitted, never inferred (the same discipline TaskAbandoned's own Reason follows).</summary>
    public string? ArchivedReason { get; private set; }
    /// <summary>
    /// When a scheduled purge (task: an archived project can be purged) will fire; null while
    /// none is pending or after <see cref="ProjectPurgeCancelled"/> clears it. A daemon sweep
    /// checks this deadline each tick and on start, and it is what makes the schedule durable —
    /// nothing else on this project survives a purge that actually fires.
    /// </summary>
    public DateTimeOffset? PurgeAt { get; private set; }

    private readonly List<VerifyCommand> _verifyCommands = [];
    public IReadOnlyList<VerifyCommand> VerifyCommands => _verifyCommands;

    private readonly List<ContextLink> _contextLinks = [];
    public IReadOnlyList<ContextLink> ContextLinks => _contextLinks;

    private readonly List<LaunchText> _launchTexts = [];
    public IReadOnlyList<LaunchText> LaunchTexts => _launchTexts;

    private readonly List<string> _neverCloseLabels = [];
    /// <summary>A label list that forces <see cref="CloseLinkedIssueRule.Never"/> for an issue carrying any of them at closeout time.</summary>
    public IReadOnlyList<string> NeverCloseLabels => _neverCloseLabels;

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
        SkipPermissions = @event.SkipPermissions;
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

        if (@event.MaxParallelTasks.HasValue)
        {
            MaxParallelTasks = @event.MaxParallelTasks.Value;
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

        if (@event.OrchestratorModel.HasValue)
        {
            OrchestratorModel = @event.OrchestratorModel.Value ?? AgentModel.Unknown;
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

        if (@event.ReviewStageComposition.HasValue)
        {
            ReviewStageComposition = @event.ReviewStageComposition.Value;
        }

        if (@event.AutoPrReview.HasValue)
        {
            AutoPrReview = @event.AutoPrReview.Value ?? AutoPrReviewSpeed.Off;
        }

        if (@event.Priority.HasValue)
        {
            Priority = @event.Priority.Value ?? ProjectPriority.Normal;
        }

        if (@event.ClaimGate.HasValue)
        {
            ClaimGate = @event.ClaimGate.Value ?? ClaimGate.Off;
        }

        if (@event.LaunchTexts.HasValue)
        {
            _launchTexts.Clear();
            _launchTexts.AddRange(@event.LaunchTexts.Value ?? []);
        }

        if (@event.CloseLinkedIssue.HasValue)
        {
            CloseLinkedIssue = @event.CloseLinkedIssue.Value ?? CloseLinkedIssueRule.WhenAllTasksClose;
        }

        if (@event.NeverCloseLabels.HasValue)
        {
            _neverCloseLabels.Clear();
            _neverCloseLabels.AddRange(@event.NeverCloseLabels.Value ?? []);
        }

        if (@event.WritingConventions.HasValue)
        {
            WritingConventions = @event.WritingConventions.Value ?? WritingConventions.Default;
        }
    }

    public void Apply(ProjectArchived @event)
    {
        IsArchived = true;
        ArchivedAt = @event.ArchivedAt;
        ArchivedReason = @event.Reason;
    }

    public void Apply(ProjectReactivated @event)
    {
        IsArchived = false;
        ArchivedAt = null;
        ArchivedReason = null;
    }

    public void Apply(ProjectRenamed @event)
    {
        Name = @event.NewName;
    }

    public void Apply(ProjectPurgeScheduled @event)
    {
        PurgeAt = @event.PurgeAt;
    }

    public void Apply(ProjectPurgeCancelled @event)
    {
        PurgeAt = null;
    }
}
