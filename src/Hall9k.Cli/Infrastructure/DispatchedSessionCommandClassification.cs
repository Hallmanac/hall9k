using Hall9k.Cli.Commands;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// What a command's own <c>CommandSettings</c> means to a session running inside a dispatched run's
/// own worktree (task: a dispatched session cannot drive the project's own lifecycle). Exactly one
/// of the three applies to every command this binary registers, and <c>DispatchedSessionTreeTests</c>
/// walks the live tree to prove none was left out.
/// </summary>
internal enum DispatchedSessionAccess
{
    /// <summary>Never touched: reads nothing this refusal needs to gate.</summary>
    ReadOnly,

    /// <summary>Never touched: a dispatched session's own legitimate act, even inside its own run.</summary>
    Allowed,

    /// <summary>
    /// Refused when <see cref="Hall9k.Domain.Features.Run.DispatchedRunEnvironment.RunIdVariable"/>
    /// is set — a verb that changes the state of a task, idea, epic, run, decision, project, node,
    /// owner, connection, config, daemon, orchestrator, or install, that a dispatched session has no
    /// business calling on its own: the orchestrator or a person owns it.
    /// </summary>
    Refused,
}

/// <summary>
/// Every command this binary registers, classified once, keyed by the CLR type of its own
/// <c>CommandSettings</c> — the one thing <see cref="DispatchedSessionInterceptor"/> can read off
/// Spectre's <c>CommandContext</c> before a command's <c>ExecuteAsync</c> ever runs. The verb string
/// alongside each entry is the exact <c>h9k</c> command line it names, quoted back in a refusal.
/// <para>
/// Read-only verbs (<c>show</c>, <c>list</c>, <c>status</c>, <c>logs</c>, <c>decide list</c>/<c>show</c>,
/// <c>learn list</c>/<c>show</c>, <c>messages</c>, <c>doctor</c>) observe nothing this refusal protects.
/// Allowed verbs (<c>learn record</c>/<c>retire</c>/<c>distill</c>, <c>decide record</c> — which already
/// carries its own attendance refusal (Decisions Log, <c>DecisionDecider</c>) — the task-lifecycle reporting verbs
/// <c>register-session</c>, <c>verify</c>, <c>deliver</c>, <c>handback</c>, <c>release</c>,
/// <c>log-interaction</c>, <c>write-jira</c>, <c>run-local</c>, <c>pr reply-guard</c>,
/// <c>orchestrator feed</c>, and <c>idea add</c>) are exactly what a dispatched session legitimately
/// does with its own run, or the lightest, most reversible act this platform has (capturing a raw
/// idea) — carved out explicitly rather than left to fall through a default.
/// </para>
/// <para>
/// Two verbs outside that carve-out are refused even though nothing names them in the acceptance
/// criteria's own enumeration: <c>task add</c> and <c>epic add</c> both change a task's or an epic's
/// own state (a new stream, visible to every other command that lists or shows one) the same way
/// every other refused verb does, and <c>idea add</c>'s own explicit allowance is the one deliberate
/// exception to that rule, not evidence the rule does not apply elsewhere — an idea is a raw,
/// non-committal note with no dispatch of its own, never itself an addition to the task or epic
/// backlog the way <c>task add</c> and <c>epic add</c> are.
/// </para>
/// </summary>
internal static class DispatchedSessionCommandClassification
{
    public static readonly IReadOnlyDictionary<Type, (DispatchedSessionAccess Access, string Verb)> BySettingsType =
        new Dictionary<Type, (DispatchedSessionAccess, string)>
        {
            // ---- top level ----
            [typeof(StatusCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "status"),
            [typeof(LogsCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "logs"),
            [typeof(DoctorCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "doctor"),
            [typeof(MessagesCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "messages"),
            [typeof(InstallCommand.Settings)] = (DispatchedSessionAccess.Refused, "install"),
            [typeof(UninstallCommand.Settings)] = (DispatchedSessionAccess.Refused, "uninstall"),
            [typeof(UpdateCommand.Settings)] = (DispatchedSessionAccess.Refused, "update"),

            // ---- task ----
            [typeof(TaskAddCommand.Settings)] = (DispatchedSessionAccess.Refused, "task add"),
            [typeof(TaskReviseCommand.Settings)] = (DispatchedSessionAccess.Refused, "task revise"),
            [typeof(TaskSetReviewCapsCommand.Settings)] = (DispatchedSessionAccess.Refused, "task set-review-caps"),
            [typeof(TaskPublishCommand.Settings)] = (DispatchedSessionAccess.Refused, "task publish"),
            [typeof(TaskAssignCommand.Settings)] = (DispatchedSessionAccess.Refused, "task assign"),
            [typeof(TaskSetSessionCapCommand.Settings)] = (DispatchedSessionAccess.Refused, "task set-session-cap"),
            [typeof(TaskSetPreApprovedCommand.Settings)] = (DispatchedSessionAccess.Refused, "task set-pre-approved"),
            [typeof(TaskScopeCommand.Settings)] = (DispatchedSessionAccess.Refused, "task scope"),
            [typeof(TaskShareCommand.Settings)] = (DispatchedSessionAccess.Refused, "task share"),
            [typeof(TaskSetPrivateCommand.Settings)] = (DispatchedSessionAccess.Refused, "task set-private"),
            [typeof(TaskUnassignCommand.Settings)] = (DispatchedSessionAccess.Refused, "task unassign"),
            [typeof(TaskDraftCommand.Settings)] = (DispatchedSessionAccess.Refused, "task draft"),
            [typeof(TaskListCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "task list"),
            [typeof(TaskShowCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "task show"),
            [typeof(TaskPullCommand.Settings)] = (DispatchedSessionAccess.Refused, "task pull"),
            [typeof(TaskPushToJiraCommand.Settings)] = (DispatchedSessionAccess.Refused, "task push-to-jira"),
            [typeof(TaskLinkJiraCommand.Settings)] = (DispatchedSessionAccess.Refused, "task link-jira"),
            [typeof(TaskWriteJiraCommand.Settings)] = (DispatchedSessionAccess.Allowed, "task write-jira"),
            [typeof(TaskLinkIssueCommand.Settings)] = (DispatchedSessionAccess.Refused, "task link-issue"),
            [typeof(TaskLogInteractionCommand.Settings)] = (DispatchedSessionAccess.Allowed, "task log-interaction"),
            [typeof(TaskRunLocalCommand.Settings)] = (DispatchedSessionAccess.Allowed, "task run-local"),
            [typeof(TaskAbandonCommand.Settings)] = (DispatchedSessionAccess.Refused, "task abandon"),
            [typeof(TaskRetryCommand.Settings)] = (DispatchedSessionAccess.Refused, "task retry"),
            [typeof(TaskResolveCommand.Settings)] = (DispatchedSessionAccess.Refused, "task resolve"),
            [typeof(TaskWorkCommand.Settings)] = (DispatchedSessionAccess.Refused, "task work"),
            [typeof(TaskRegisterSessionCommand.Settings)] = (DispatchedSessionAccess.Allowed, "task register-session"),
            [typeof(TaskStartCommand.Settings)] = (DispatchedSessionAccess.Refused, "task start"),
            [typeof(TaskVerifyCommand.Settings)] = (DispatchedSessionAccess.Allowed, "task verify"),
            [typeof(TaskDeliverCommand.Settings)] = (DispatchedSessionAccess.Allowed, "task deliver"),
            [typeof(TaskReleaseCommand.Settings)] = (DispatchedSessionAccess.Allowed, "task release"),
            [typeof(TaskHandbackCommand.Settings)] = (DispatchedSessionAccess.Allowed, "task handback"),
            [typeof(TaskHandoffCommand.Settings)] = (DispatchedSessionAccess.Refused, "task handoff"),
            [typeof(TaskTakeCommand.Settings)] = (DispatchedSessionAccess.Refused, "task take"),
            [typeof(TaskGrantCommand.Settings)] = (DispatchedSessionAccess.Refused, "task grant"),
            [typeof(TaskRefuseCommand.Settings)] = (DispatchedSessionAccess.Refused, "task refuse"),
            [typeof(TaskDelegateCommand.Settings)] = (DispatchedSessionAccess.Refused, "task delegate"),

            // ---- idea ----
            [typeof(IdeaAddCommand.Settings)] = (DispatchedSessionAccess.Allowed, "idea add"),
            [typeof(IdeaListCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "idea list"),
            [typeof(IdeaShowCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "idea show"),
            [typeof(IdeaReviseCommand.Settings)] = (DispatchedSessionAccess.Refused, "idea revise"),
            [typeof(IdeaAssignCommand.Settings)] = (DispatchedSessionAccess.Refused, "idea assign"),
            [typeof(IdeaPromoteCommand.Settings)] = (DispatchedSessionAccess.Refused, "idea promote"),
            [typeof(IdeaConcludeCommand.Settings)] = (DispatchedSessionAccess.Refused, "idea conclude"),
            [typeof(IdeaArchiveCommand.Settings)] = (DispatchedSessionAccess.Refused, "idea archive"),
            [typeof(IdeaScopeCommand.Settings)] = (DispatchedSessionAccess.Refused, "idea scope"),
            [typeof(IdeaShareCommand.Settings)] = (DispatchedSessionAccess.Refused, "idea share"),
            [typeof(IdeaSetPrivateCommand.Settings)] = (DispatchedSessionAccess.Refused, "idea set-private"),

            // ---- epic ----
            [typeof(EpicAddCommand.Settings)] = (DispatchedSessionAccess.Refused, "epic add"),
            [typeof(EpicListCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "epic list"),
            [typeof(EpicShowCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "epic show"),
            [typeof(EpicLinkJiraCommand.Settings)] = (DispatchedSessionAccess.Refused, "epic link-jira"),
            [typeof(EpicCloseCommand.Settings)] = (DispatchedSessionAccess.Refused, "epic close"),

            // ---- run ----
            [typeof(RunKillCommand.Settings)] = (DispatchedSessionAccess.Refused, "run kill"),

            // ---- decide ----
            [typeof(DecideCommand.Settings)] = (DispatchedSessionAccess.Allowed, "decide record"),
            [typeof(DecisionListCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "decide list"),
            [typeof(DecisionShowCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "decide show"),
            [typeof(DecisionSupersedeCommand.Settings)] = (DispatchedSessionAccess.Refused, "decide supersede"),
            [typeof(DecisionImportCommand.Settings)] = (DispatchedSessionAccess.Refused, "decide import"),

            // ---- learn ----
            [typeof(LearnCommand.Settings)] = (DispatchedSessionAccess.Allowed, "learn record"),
            [typeof(LearningListCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "learn list"),
            [typeof(LearningShowCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "learn show"),
            [typeof(LearningRetireCommand.Settings)] = (DispatchedSessionAccess.Allowed, "learn retire"),
            [typeof(LearningDistillCommand.Settings)] = (DispatchedSessionAccess.Allowed, "learn distill"),

            // ---- review ----
            [typeof(ReviewResolveCommand.Settings)] = (DispatchedSessionAccess.Refused, "review resolve"),
            [typeof(ReviewProceedCommand.Settings)] = (DispatchedSessionAccess.Refused, "review proceed"),
            [typeof(ReviewFixedCommand.Settings)] = (DispatchedSessionAccess.Refused, "review fixed"),

            // ---- pr ----
            [typeof(PullRequestResolveCommand.Settings)] = (DispatchedSessionAccess.Refused, "pr resolve"),
            [typeof(PullRequestReplyCommand.Settings)] = (DispatchedSessionAccess.Refused, "pr reply"),
            [typeof(PullRequestReplyGuardCommand.Settings)] = (DispatchedSessionAccess.Allowed, "pr reply-guard"),
            [typeof(PullRequestReviewCommand.Settings)] = (DispatchedSessionAccess.Refused, "pr review"),
            [typeof(PullRequestApproveCommand.Settings)] = (DispatchedSessionAccess.Refused, "pr approve"),
            [typeof(PullRequestRequestChangesCommand.Settings)] = (DispatchedSessionAccess.Refused, "pr request-changes"),

            // ---- message ----
            [typeof(MessageShowCommand.Settings)] = (DispatchedSessionAccess.ReadOnly, "message show"),
            [typeof(MessageSendCommand.Settings)] = (DispatchedSessionAccess.Refused, "message send"),
            [typeof(MessageHandleCommand.Settings)] = (DispatchedSessionAccess.Refused, "message handle"),

            // ---- project (wholesale) ----
            [typeof(ProjectAddCommand.Settings)] = (DispatchedSessionAccess.Refused, "project add"),
            [typeof(ProjectJoinCommand.Settings)] = (DispatchedSessionAccess.Refused, "project join"),
            [typeof(ProjectAssignKeyCommand.Settings)] = (DispatchedSessionAccess.Refused, "project assign-key"),
            [typeof(ProjectInitCommand.Settings)] = (DispatchedSessionAccess.Refused, "project init"),
            [typeof(ProjectListCommand.Settings)] = (DispatchedSessionAccess.Refused, "project list"),
            [typeof(ProjectShowCommand.Settings)] = (DispatchedSessionAccess.Refused, "project show"),
            [typeof(ProjectSetCommand.Settings)] = (DispatchedSessionAccess.Refused, "project set"),
            [typeof(ProjectRemoveCommand.Settings)] = (DispatchedSessionAccess.Refused, "project remove"),
            [typeof(ProjectReactivateCommand.Settings)] = (DispatchedSessionAccess.Refused, "project reactivate"),
            [typeof(ProjectCancelPurgeCommand.Settings)] = (DispatchedSessionAccess.Refused, "project cancel-purge"),
            [typeof(ProjectRenameCommand.Settings)] = (DispatchedSessionAccess.Refused, "project rename"),
            [typeof(ProjectInviteCommand.Settings)] = (DispatchedSessionAccess.Refused, "project invite"),
            [typeof(ProjectPullCommand.Settings)] = (DispatchedSessionAccess.Refused, "project pull"),
            [typeof(ProjectReconcileCommand.Settings)] = (DispatchedSessionAccess.Refused, "project reconcile"),
            [typeof(ProjectMembersCommand.Settings)] = (DispatchedSessionAccess.Refused, "project members"),
            [typeof(ProjectMemberRemoveCommand.Settings)] = (DispatchedSessionAccess.Refused, "project member remove"),
            [typeof(ProjectPromptAddendumSetCommand.Settings)] =
                (DispatchedSessionAccess.Refused, "project prompt-addendum set"),
            [typeof(ProjectPromptAddendumShowCommand.Settings)] =
                (DispatchedSessionAccess.Refused, "project prompt-addendum show"),
            [typeof(ProjectPromptAddendumListCommand.Settings)] =
                (DispatchedSessionAccess.Refused, "project prompt-addendum list"),
            [typeof(ProjectPromptAddendumRemoveCommand.Settings)] =
                (DispatchedSessionAccess.Refused, "project prompt-addendum remove"),
            [typeof(ProjectRunSkillShowCommand.Settings)] = (DispatchedSessionAccess.Refused, "project run-skill show"),
            [typeof(ProjectRunSkillSetCommand.Settings)] = (DispatchedSessionAccess.Refused, "project run-skill set"),

            // ---- owner (wholesale) ----
            [typeof(OwnerShowCommand.Settings)] = (DispatchedSessionAccess.Refused, "owner show"),
            [typeof(OwnerSetCommand.Settings)] = (DispatchedSessionAccess.Refused, "owner set"),

            // ---- node (wholesale) ----
            [typeof(NodeInviteCommand.Settings)] = (DispatchedSessionAccess.Refused, "node invite"),
            [typeof(NodeVouchCommand.Settings)] = (DispatchedSessionAccess.Refused, "node vouch"),
            [typeof(NodeRevokeCommand.Settings)] = (DispatchedSessionAccess.Refused, "node revoke"),

            // ---- connection (wholesale) ----
            [typeof(ConnectionAddJiraCommand.Settings)] = (DispatchedSessionAccess.Refused, "connection add jira"),
            [typeof(ConnectionListCommand.Settings)] = (DispatchedSessionAccess.Refused, "connection list"),

            // ---- config (wholesale) ----
            [typeof(ConfigShowCommand.Settings)] = (DispatchedSessionAccess.Refused, "config show"),
            [typeof(ConfigSetCommand.Settings)] = (DispatchedSessionAccess.Refused, "config set"),

            // ---- daemon (wholesale) ----
            [typeof(DaemonStartCommand.Settings)] = (DispatchedSessionAccess.Refused, "daemon start"),
            [typeof(DaemonStopCommand.Settings)] = (DispatchedSessionAccess.Refused, "daemon stop"),
            [typeof(DaemonStatusCommand.Settings)] = (DispatchedSessionAccess.Refused, "daemon status"),
            [typeof(DaemonAutostartEnableCommand.Settings)] = (DispatchedSessionAccess.Refused, "daemon autostart enable"),
            [typeof(DaemonAutostartDisableCommand.Settings)] = (DispatchedSessionAccess.Refused, "daemon autostart disable"),
            [typeof(DaemonAutostartLaunchCommand.Settings)] = (DispatchedSessionAccess.Refused, "daemon autostart launch"),

            // ---- orchestrator (wholesale except feed) ----
            [typeof(OrchestratorNodeCommand.Settings)] = (DispatchedSessionAccess.Refused, "orchestrator node"),
            [typeof(OrchestratorProjectCommand.Settings)] = (DispatchedSessionAccess.Refused, "orchestrator project"),
            [typeof(OrchestratorRegisterCommand.Settings)] = (DispatchedSessionAccess.Refused, "orchestrator register"),
            [typeof(OrchestratorDeregisterCommand.Settings)] = (DispatchedSessionAccess.Refused, "orchestrator deregister"),
            [typeof(OrchestratorStatusCommand.Settings)] = (DispatchedSessionAccess.Refused, "orchestrator status"),
            [typeof(OrchestratorFeedCommand.Settings)] = (DispatchedSessionAccess.Allowed, "orchestrator feed"),
            [typeof(OrchestratorLaunchTextShowCommand.Settings)] =
                (DispatchedSessionAccess.Refused, "orchestrator launch-text show"),
            [typeof(OrchestratorLaunchTextSetCommand.Settings)] =
                (DispatchedSessionAccess.Refused, "orchestrator launch-text set"),
            [typeof(OrchestratorMeasureCommand.Settings)] = (DispatchedSessionAccess.Refused, "orchestrator measure"),
        };
}
