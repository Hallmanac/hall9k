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
    /// <summary>
    /// Which tracker becomes a task's primary reference by default when <c>h9k task add</c> adopts
    /// both a GitHub issue and a Jira card (task: a task may link to both a GitHub issue and a Jira
    /// card); Unknown means no default is set, and adoption then needs its own
    /// <c>--primary-tracker</c> override.
    /// </summary>
    public WorkItemProvider PrimaryTracker { get; set; } = WorkItemProvider.Unknown;
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
    /// The last design-review-drive choice this project's stream recorded — never the effective
    /// one (idea b9b09779, piece 3), on the identical terms <see cref="AutoPrReview"/> above
    /// states: read <see cref="ReviewDriveSetting"/> wherever the effective value or its origin
    /// matters. Initialised to the platform default (on) so the two at least agree for a project
    /// that never chose.
    /// </summary>
    public bool DesignReviewDrive { get; set; } = ReviewDriveSetting.DefaultFor(ReviewPersona.Designer);
    /// <summary>
    /// The last qa-review-drive choice this project's stream recorded (idea b9b09779, piece 2),
    /// never the effective one — the same terms <see cref="DesignReviewDrive"/> above states.
    /// Initialised to the platform default (off) so the two agree for a project that never chose.
    /// </summary>
    public bool QaReviewDrive { get; set; } = ReviewDriveSetting.DefaultFor(ReviewPersona.Qa);
    /// <summary>
    /// What has to be true on this install before a task linked to a Jira card or a GitHub issue
    /// may be claimed here (idea 64c75e43); Off is the platform's original behavior.
    /// </summary>
    public ClaimGate ClaimGate { get; set; } = ClaimGate.Off;
    /// <summary>
    /// How much of this project's own history the orchestrator feed hands a window (idea
    /// 89471598, piece 2) — see <see cref="Events.ProjectSettingsChanged.OrchestratorFeed"/>'s
    /// own doc; Transitions is the default.
    /// </summary>
    public OrchestratorFeedLevel OrchestratorFeed { get; set; } = OrchestratorFeedLevel.Default;
    /// <summary>
    /// This project's own ceiling on the feed courier's batching wait, in seconds; null defers to
    /// the platform default (idea 89471598, piece 3, <see cref="ProjectAggregate.DefaultCourierMaxWaitSeconds"/>).
    /// See <see cref="Events.ProjectSettingsChanged.CourierMaxWaitSeconds"/>'s own doc.
    /// </summary>
    public int? CourierMaxWaitSeconds { get; set; }
    /// <summary>Who answers a cooperative claim request (idea 202383dc, item 5) — see <see cref="Events.ProjectSettingsChanged.TakePolicy"/>'s own doc; Auto is the default.</summary>
    public TakePolicy TakePolicy { get; set; } = TakePolicy.Auto;
    /// <summary>Override of how long a cooperative take request waits for an answer, in minutes; null defers to the platform default (30). See <see cref="Events.ProjectSettingsChanged.TakeTimeoutMinutes"/>'s own doc.</summary>
    public int? TakeTimeoutMinutes { get; set; }
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
    /// <summary>This project's own additions to <see cref="NonExecutablePathDefaults"/>'s compiled rules — see <see cref="Events.ProjectSettingsChanged.NonExecutablePaths"/>'s own doc.</summary>
    public List<string> NonExecutablePaths { get; set; } = [];
    /// <summary>The compiled default rule set plus this project's own additions — the whole non-executable-path set <c>VerificationRunner</c> classifies a changed path against.</summary>
    public IReadOnlyList<string> EffectiveNonExecutablePaths => [.. NonExecutablePathDefaults.Rules, .. NonExecutablePaths];
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
    /// The <c>ChangedAt</c> of the settings change that last wrote each team-scoped field, keyed by
    /// the field's own property name. A team field is applied only when its change is not older than
    /// the one recorded here, so a pre-switch-on head that a catch-up answer delivers after a newer
    /// tail never overwrites the newer value, whatever order the two sit in the stream.
    /// </summary>
    public Dictionary<string, DateTimeOffset> TeamSettingStamps { get; set; } = [];

    /// <summary>The stamp (<c>IssuedAt</c> or <c>RemovedAt</c>) of the vouch or removal that last decided each member fingerprint, kept for a removed one too so an older vouch arriving late cannot bring it back.</summary>
    public Dictionary<string, DateTimeOffset> MemberStamps { get; set; } = [];

    /// <summary>The stamp (<c>SetAt</c> or <c>RemovedAt</c>) of the change that last decided each prompt-builder key, kept for a removed one too, on the same terms as <see cref="MemberStamps"/>.</summary>
    public Dictionary<string, DateTimeOffset> PromptAddendumStamps { get; set; } = [];

    /// <summary>
    /// Whether a change to one team-scoped field stamped <paramref name="changedAt"/> may be
    /// applied, recording the stamp when it may. A change stamped the same as the applied one is
    /// accepted, so an equal stamp keeps the append order it always had.
    /// </summary>
    public bool TryStampTeamSetting(string field, DateTimeOffset changedAt) =>
        TryStamp(TeamSettingStamps, field, changedAt);

    /// <summary>The same last-writer-by-stamp gate for one member fingerprint (<see cref="MemberStamps"/>).</summary>
    public bool TryStampMember(string rootFingerprint, DateTimeOffset stamp) =>
        TryStamp(MemberStamps, rootFingerprint, stamp);

    /// <summary>The same last-writer-by-stamp gate for one prompt-builder key (<see cref="PromptAddendumStamps"/>).</summary>
    public bool TryStampPromptAddendum(string builderKey, DateTimeOffset stamp) =>
        TryStamp(PromptAddendumStamps, builderKey, stamp);

    private static bool TryStamp(Dictionary<string, DateTimeOffset> stamps, string key, DateTimeOffset stamp)
    {
        if (stamps.TryGetValue(key, out DateTimeOffset applied) && stamp < applied)
        {
            return false;
        }

        stamps[key] = stamp;
        return true;
    }
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
    /// <summary>
    /// When a scheduled purge (task: an archived project can be purged) will fire; null while
    /// none is pending or after a cancel clears it. <c>h9k project list --include-archived</c> and
    /// <c>h9k project show</c> read this to mark a purge-pending project with its deadline and the
    /// cancel command; the daemon's purge sweep reads it to find every project whose deadline has
    /// passed.
    /// </summary>
    public DateTimeOffset? PurgeAt { get; set; }

    /// <summary>Mirrors <see cref="ProjectAggregate.Members"/>: this node's own audit trail, never
    /// what a membership read actually trusts.</summary>
    public Dictionary<string, ProjectMemberRole> Members { get; set; } = [];

    /// <summary>Mirrors <see cref="ProjectAggregate.PromptAddenda"/>: this node's own audit trail,
    /// never what a prompt builder actually splices in.</summary>
    public Dictionary<string, ProjectPromptAddendum> PromptAddenda { get; set; } = [];

    /// <summary>Mirrors <see cref="ProjectAggregate.ProjectKey"/>: this install's own local record
    /// of the project's ledger-derived key (idea 202383dc, M2). Null until a join actually reads
    /// one back from the ledger.</summary>
    public string? ProjectKey { get; set; }

    /// <summary>Mirrors <see cref="ProjectAggregate.RunSkill"/>: this node's own audit trail of
    /// how to stand this project up locally (idea b9b09779, piece 4), never what a member on
    /// another machine reads — that is the ledger file the daemon writes from the same event.</summary>
    public ProjectRunSkill? RunSkill { get; set; }

    /// <summary>Mirrors <see cref="ProjectAggregate.RunSkillDiscoveryRequestedAt"/>.</summary>
    public DateTimeOffset? RunSkillDiscoveryRequestedAt { get; set; }

    /// <summary>Mirrors <see cref="ProjectAggregate.RunSkillDiscoveryDispatchedAt"/>.</summary>
    public DateTimeOffset? RunSkillDiscoveryDispatchedAt { get; set; }

    /// <summary>Mirrors <see cref="ProjectAggregate.RunSkillDiscoveryFailure"/>.</summary>
    public string? RunSkillDiscoveryFailure { get; set; }

    /// <summary>
    /// Whether a run-skill discovery has been asked for and no session has been spawned for that
    /// particular ask yet — what the daemon's own sweep claims work off. Compared on the
    /// timestamps rather than a boolean flag so a fresh request made while an older session is
    /// still in flight (a repository that has since changed, a session that died) supersedes it
    /// and earns its own dispatch, which is the whole point of <c>--discover-run-skill</c> being
    /// re-runnable.
    /// </summary>
    public bool RunSkillDiscoveryOutstanding =>
        RunSkillDiscoveryRequestedAt is { } requested
        && (RunSkillDiscoveryDispatchedAt is not { } dispatched || dispatched < requested);
}

public sealed partial class ProjectDetailsProjection : SingleStreamProjection<ProjectDetails, Guid>
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
        if (@event.Data.VerifyCommands.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.VerifyCommands), @event.Data.ChangedAt))
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

        if (@event.Data.ContextLinks.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.ContextLinks), @event.Data.ChangedAt))
        {
            view.ContextLinks = [.. @event.Data.ContextLinks.Value ?? []];
        }

        if (@event.Data.CommitStyle.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.CommitStyle), @event.Data.ChangedAt))
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

        if (@event.Data.ReviewRerequest.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.ReviewRerequest), @event.Data.ChangedAt))
        {
            view.ReviewRerequest = @event.Data.ReviewRerequest.Value ?? ReviewRerequestPolicy.Unknown;
        }

        if (@event.Data.JiraProjectKey.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.JiraProjectKey), @event.Data.ChangedAt))
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

        if (@event.Data.BacklogPolicy.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.BacklogPolicy), @event.Data.ChangedAt))
        {
            view.BacklogPolicy = @event.Data.BacklogPolicy.Value ?? BacklogPolicy.None;
        }

        if (@event.Data.PrimaryTracker.HasValue)
        {
            view.PrimaryTracker = @event.Data.PrimaryTracker.Value ?? WorkItemProvider.Unknown;
        }

        if (@event.Data.BacklogRoutingGuidance.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.BacklogRoutingGuidance), @event.Data.ChangedAt))
        {
            view.BacklogRoutingGuidance = @event.Data.BacklogRoutingGuidance.Value.IsBlank()
                ? null
                : @event.Data.BacklogRoutingGuidance.Value;
        }

        if (@event.Data.MaxComplianceReviewCycles.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.MaxComplianceReviewCycles), @event.Data.ChangedAt))
        {
            view.MaxComplianceReviewCycles = @event.Data.MaxComplianceReviewCycles.Value;
        }

        if (@event.Data.MaxAdversarialReviewCycles.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.MaxAdversarialReviewCycles), @event.Data.ChangedAt))
        {
            view.MaxAdversarialReviewCycles = @event.Data.MaxAdversarialReviewCycles.Value;
        }

        if (@event.Data.MaxFinalFullPassRounds.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.MaxFinalFullPassRounds), @event.Data.ChangedAt))
        {
            view.MaxFinalFullPassRounds = @event.Data.MaxFinalFullPassRounds.Value;
        }

        if (@event.Data.LifetimeReviewCycleBudget.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.LifetimeReviewCycleBudget), @event.Data.ChangedAt))
        {
            view.LifetimeReviewCycleBudget = @event.Data.LifetimeReviewCycleBudget.Value;
        }

        if (@event.Data.ReviewStageComposition.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.ReviewStageComposition), @event.Data.ChangedAt))
        {
            view.ReviewStageComposition = @event.Data.ReviewStageComposition.Value;
        }

        if (@event.Data.BranchNameTemplate.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.BranchNameTemplate), @event.Data.ChangedAt))
        {
            view.BranchNameTemplate = @event.Data.BranchNameTemplate.Value ?? BranchNameTemplate.Default;
        }

        if (@event.Data.AutoPrReview.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.AutoPrReview), @event.Data.ChangedAt))
        {
            view.AutoPrReview = @event.Data.AutoPrReview.Value ?? AutoPrReviewSpeed.Off;
        }

        if (@event.Data.DesignReviewDrive.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.DesignReviewDrive), @event.Data.ChangedAt))
        {
            view.DesignReviewDrive = @event.Data.DesignReviewDrive.Value;
        }

        if (@event.Data.QaReviewDrive.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.QaReviewDrive), @event.Data.ChangedAt))
        {
            view.QaReviewDrive = @event.Data.QaReviewDrive.Value;
        }

        if (@event.Data.Priority.HasValue)
        {
            view.Priority = @event.Data.Priority.Value ?? ProjectPriority.Normal;
        }

        if (@event.Data.ClaimGate.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.ClaimGate), @event.Data.ChangedAt))
        {
            view.ClaimGate = @event.Data.ClaimGate.Value ?? ClaimGate.Off;
        }

        // Only here, never on ProjectTeamSettingsChanged: an orchestrator feed level is this
        // operator's own reading preference on this machine, so it stays on the node-scoped
        // event exactly as the models and the local paths beside it do.
        if (@event.Data.OrchestratorFeed.HasValue)
        {
            view.OrchestratorFeed = @event.Data.OrchestratorFeed.Value ?? OrchestratorFeedLevel.Default;
        }

        // The identical "this operator's own reading preference on this machine" reasoning as
        // OrchestratorFeed just above: never on ProjectTeamSettingsChanged.
        if (@event.Data.CourierMaxWaitSeconds.HasValue)
        {
            view.CourierMaxWaitSeconds = @event.Data.CourierMaxWaitSeconds.Value;
        }

        if (@event.Data.TakePolicy.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.TakePolicy), @event.Data.ChangedAt))
        {
            view.TakePolicy = @event.Data.TakePolicy.Value ?? TakePolicy.Auto;
        }

        if (@event.Data.TakeTimeoutMinutes.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.TakeTimeoutMinutes), @event.Data.ChangedAt))
        {
            view.TakeTimeoutMinutes = @event.Data.TakeTimeoutMinutes.Value;
        }

        if (@event.Data.LaunchTexts.HasValue)
        {
            view.LaunchTexts = [.. @event.Data.LaunchTexts.Value ?? []];
        }

        if (@event.Data.CloseLinkedIssue.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.CloseLinkedIssue), @event.Data.ChangedAt))
        {
            view.CloseLinkedIssue = @event.Data.CloseLinkedIssue.Value ?? CloseLinkedIssueRule.WhenAllTasksClose;
        }

        if (@event.Data.NeverCloseLabels.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.NeverCloseLabels), @event.Data.ChangedAt))
        {
            view.NeverCloseLabels = [.. @event.Data.NeverCloseLabels.Value ?? []];
        }

        if (@event.Data.WritingConventions.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.WritingConventions), @event.Data.ChangedAt))
        {
            view.WritingConventions = @event.Data.WritingConventions.Value ?? WritingConventions.Default;
        }

        if (@event.Data.NonExecutablePaths.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.NonExecutablePaths), @event.Data.ChangedAt))
        {
            view.NonExecutablePaths = [.. @event.Data.NonExecutablePaths.Value ?? []];
        }

        view.SettingsChangedAt = @event.Data.ChangedAt;
    }

    /// <summary>Mirrors <see cref="ProjectAggregate.Apply(Events.ProjectTeamSettingsChanged)"/>.</summary>
    public void Apply(IEvent<ProjectTeamSettingsChanged> @event, ProjectDetails view)
    {
        if (@event.Data.VerifyCommands.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.VerifyCommands), @event.Data.ChangedAt))
        {
            view.VerifyCommands = [.. @event.Data.VerifyCommands.Value ?? []];
        }

        if (@event.Data.ReviewRerequest.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.ReviewRerequest), @event.Data.ChangedAt))
        {
            view.ReviewRerequest = @event.Data.ReviewRerequest.Value ?? ReviewRerequestPolicy.Unknown;
        }

        if (@event.Data.JiraProjectKey.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.JiraProjectKey), @event.Data.ChangedAt))
        {
            view.JiraProjectKey = @event.Data.JiraProjectKey.Value ?? JiraProjectKey.None;
        }

        if (@event.Data.BacklogPolicy.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.BacklogPolicy), @event.Data.ChangedAt))
        {
            view.BacklogPolicy = @event.Data.BacklogPolicy.Value ?? BacklogPolicy.None;
        }

        if (@event.Data.BacklogRoutingGuidance.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.BacklogRoutingGuidance), @event.Data.ChangedAt))
        {
            view.BacklogRoutingGuidance = @event.Data.BacklogRoutingGuidance.Value.IsBlank()
                ? null
                : @event.Data.BacklogRoutingGuidance.Value;
        }

        if (@event.Data.MaxComplianceReviewCycles.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.MaxComplianceReviewCycles), @event.Data.ChangedAt))
        {
            view.MaxComplianceReviewCycles = @event.Data.MaxComplianceReviewCycles.Value;
        }

        if (@event.Data.MaxAdversarialReviewCycles.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.MaxAdversarialReviewCycles), @event.Data.ChangedAt))
        {
            view.MaxAdversarialReviewCycles = @event.Data.MaxAdversarialReviewCycles.Value;
        }

        if (@event.Data.MaxFinalFullPassRounds.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.MaxFinalFullPassRounds), @event.Data.ChangedAt))
        {
            view.MaxFinalFullPassRounds = @event.Data.MaxFinalFullPassRounds.Value;
        }

        if (@event.Data.LifetimeReviewCycleBudget.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.LifetimeReviewCycleBudget), @event.Data.ChangedAt))
        {
            view.LifetimeReviewCycleBudget = @event.Data.LifetimeReviewCycleBudget.Value;
        }

        if (@event.Data.ReviewStageComposition.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.ReviewStageComposition), @event.Data.ChangedAt))
        {
            view.ReviewStageComposition = @event.Data.ReviewStageComposition.Value;
        }

        if (@event.Data.BranchNameTemplate.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.BranchNameTemplate), @event.Data.ChangedAt))
        {
            view.BranchNameTemplate = @event.Data.BranchNameTemplate.Value ?? BranchNameTemplate.Default;
        }

        if (@event.Data.AutoPrReview.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.AutoPrReview), @event.Data.ChangedAt))
        {
            view.AutoPrReview = @event.Data.AutoPrReview.Value ?? AutoPrReviewSpeed.Off;
        }

        if (@event.Data.DesignReviewDrive.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.DesignReviewDrive), @event.Data.ChangedAt))
        {
            view.DesignReviewDrive = @event.Data.DesignReviewDrive.Value;
        }

        if (@event.Data.QaReviewDrive.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.QaReviewDrive), @event.Data.ChangedAt))
        {
            view.QaReviewDrive = @event.Data.QaReviewDrive.Value;
        }

        if (@event.Data.ClaimGate.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.ClaimGate), @event.Data.ChangedAt))
        {
            view.ClaimGate = @event.Data.ClaimGate.Value ?? ClaimGate.Off;
        }

        if (@event.Data.TakePolicy.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.TakePolicy), @event.Data.ChangedAt))
        {
            view.TakePolicy = @event.Data.TakePolicy.Value ?? TakePolicy.Auto;
        }

        if (@event.Data.TakeTimeoutMinutes.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.TakeTimeoutMinutes), @event.Data.ChangedAt))
        {
            view.TakeTimeoutMinutes = @event.Data.TakeTimeoutMinutes.Value;
        }

        if (@event.Data.CloseLinkedIssue.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.CloseLinkedIssue), @event.Data.ChangedAt))
        {
            view.CloseLinkedIssue = @event.Data.CloseLinkedIssue.Value ?? CloseLinkedIssueRule.WhenAllTasksClose;
        }

        if (@event.Data.NeverCloseLabels.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.NeverCloseLabels), @event.Data.ChangedAt))
        {
            view.NeverCloseLabels = [.. @event.Data.NeverCloseLabels.Value ?? []];
        }

        if (@event.Data.WritingConventions.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.WritingConventions), @event.Data.ChangedAt))
        {
            view.WritingConventions = @event.Data.WritingConventions.Value ?? WritingConventions.Default;
        }

        if (@event.Data.ContextLinks.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.ContextLinks), @event.Data.ChangedAt))
        {
            view.ContextLinks = [.. @event.Data.ContextLinks.Value ?? []];
        }

        if (@event.Data.CommitStyle.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.CommitStyle), @event.Data.ChangedAt))
        {
            view.CommitStyle = @event.Data.CommitStyle.Value ?? CommitStyle.Unknown;
        }

        if (@event.Data.NonExecutablePaths.HasValue && view.TryStampTeamSetting(nameof(ProjectDetails.NonExecutablePaths), @event.Data.ChangedAt))
        {
            view.NonExecutablePaths = [.. @event.Data.NonExecutablePaths.Value ?? []];
        }
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

    public void Apply(IEvent<ProjectPurgeScheduled> @event, ProjectDetails view)
    {
        view.PurgeAt = @event.Data.PurgeAt;
    }

    public void Apply(IEvent<ProjectPurgeCancelled> @event, ProjectDetails view)
    {
        view.PurgeAt = null;
    }

    // The four events below are last-writer by their own stamp rather than by append order: a
    // replicated one can land behind a newer one already applied (a catch-up answer delivers a
    // pre-switch-on head after the post-switch-on tail), and the projection must read the same
    // whichever order they sit in.
    public void Apply(IEvent<MemberVouched> @event, ProjectDetails view)
    {
        if (view.TryStampMember(@event.Data.RootFingerprint, @event.Data.IssuedAt))
        {
            view.Members[@event.Data.RootFingerprint] = @event.Data.Role;
        }
    }

    public void Apply(IEvent<MemberRemoved> @event, ProjectDetails view)
    {
        if (view.TryStampMember(@event.Data.RootFingerprint, @event.Data.RemovedAt))
        {
            view.Members.Remove(@event.Data.RootFingerprint);
        }
    }

    public void Apply(IEvent<ProjectPromptAddendumSet> @event, ProjectDetails view)
    {
        if (view.TryStampPromptAddendum(@event.Data.BuilderKey, @event.Data.SetAt))
        {
            view.PromptAddenda[@event.Data.BuilderKey] = new ProjectPromptAddendum(
                @event.Data.Content, @event.Data.OverCap, @event.Data.OverCapReason, @event.Data.SetAt, @event.Data.SetByOwnerId);
        }
    }

    public void Apply(IEvent<ProjectPromptAddendumRemoved> @event, ProjectDetails view)
    {
        if (view.TryStampPromptAddendum(@event.Data.BuilderKey, @event.Data.RemovedAt))
        {
            view.PromptAddenda.Remove(@event.Data.BuilderKey);
        }
    }

    public void Apply(IEvent<ProjectKeyAssigned> @event, ProjectDetails view) =>
        view.ProjectKey = @event.Data.ProjectKey;

    public void Apply(IEvent<ProjectRunSkillDiscoveryRequested> @event, ProjectDetails view)
    {
        view.RunSkillDiscoveryRequestedAt = @event.Data.RequestedAt;
        view.RunSkillDiscoveryFailure = null;
    }

    public void Apply(IEvent<ProjectRunSkillDiscoveryDispatched> @event, ProjectDetails view) =>
        view.RunSkillDiscoveryDispatchedAt = @event.Data.DispatchedAt;

    public void Apply(IEvent<ProjectRunSkillRecorded> @event, ProjectDetails view)
    {
        if (view.RunSkill is { } applied && @event.Data.RecordedAt < applied.RecordedAt)
        {
            return;
        }

        view.RunSkill = new ProjectRunSkill(
            @event.Data.Content, RunSkillShape.FromInput(@event.Data.Shape), @event.Data.ComposedAgainstCommit,
            RunSkillAuthor.FromInput(@event.Data.Author), @event.Data.RecordedAt, @event.Data.RecordedByOwnerId);
        view.RunSkillDiscoveryRequestedAt = null;
        view.RunSkillDiscoveryFailure = null;
    }

    public void Apply(IEvent<ProjectRunSkillDiscoveryFailed> @event, ProjectDetails view)
    {
        view.RunSkillDiscoveryRequestedAt = null;
        view.RunSkillDiscoveryFailure = @event.Data.Reason;
    }
}
