using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// JiraProjectKey is the project's binding to a board (backlog 18): the key new cards are
/// created under and the key an agent-reported card is checked against. Optional like every
/// other setting here — absent means left alone, and present-but-empty clears the binding.
/// Appended with a default so streams written before Jira existed replay unchanged.
/// <para>
/// HomeDirectory is where the project lives on disk (backlog 47). It is a setting like the
/// rest, because the location is the owner's call while the shape inside it is the platform's;
/// <see cref="ProjectHome.None"/> clears it, which is what a project that no longer has a home
/// on this machine reads as. RepositoryPath moves with it: a home whose repo/ was materialised
/// is the repository the daemon cuts worktrees from, and a recorded path still naming the old
/// clone would leave the two disagreeing about where this project's code is.
/// </para>
/// <para>
/// BacklogPolicy declares where a published task's work becomes visible outside Hall9k;
/// <see cref="Project.BacklogPolicy.None"/> is both the default and the explicit "don't", the
/// same clearing idiom as everything else here. BacklogRoutingGuidance is free text handed
/// verbatim to the Jira publication agent and, for github-issues, treated as a comma-separated
/// label list — nothing more, because a deterministic issue author cannot interpret prose.
/// </para>
/// <para>
/// BranchNameTemplate is the name a task's branch is cut under, so a team whose convention keys on
/// its tracker (ARX-14-short-slug) states that convention here instead of forking the platform;
/// <see cref="Project.BranchNameTemplate.Default"/> is both the untouched default and what 'none'
/// restores, and it renders exactly the name the platform cut before this setting existed.
/// </para>
/// </summary>
public sealed record ProjectSettingsChanged(
    Guid Id,
    Optional<IReadOnlyList<VerifyCommand>> VerifyCommands,
    Optional<bool> SkipPermissions,
    Optional<int> MaxParallelAgents,
    Optional<IReadOnlyList<ContextLink>> ContextLinks,
    DateTimeOffset ChangedAt,
    Guid ChangedByOwnerId,
    Optional<CommitStyle> CommitStyle = default,
    Optional<AgentModel> Model = default,
    Optional<ReviewRerequestPolicy> ReviewRerequest = default,
    Optional<JiraProjectKey> JiraProjectKey = default,
    Optional<ProjectHome> HomeDirectory = default,
    Optional<string> RepositoryPath = default,
    Optional<BacklogPolicy> BacklogPolicy = default,
    Optional<string> BacklogRoutingGuidance = default,
    /// <summary>
    /// This project's override of the conformance review track's cycle cap (Decisions Log #63,
    /// task: review cycle caps become settable): present-with-null clears the override, so the
    /// node's own setting (or the compiled default) decides again — the same idiom
    /// <see cref="Tasks.Events.TaskRevised.EpicId"/> already uses for a clearable value. Task
    /// overrides this project value; this project value overrides the node.
    /// </summary>
    Optional<int?> MaxComplianceReviewCycles = default,
    /// <summary>This project's override of the adversarial review track's cycle cap; present-with-null clears it.</summary>
    Optional<int?> MaxAdversarialReviewCycles = default,
    /// <summary>This project's override of the mandatory final-full-pass round cap; present-with-null clears it.</summary>
    Optional<int?> MaxFinalFullPassRounds = default,
    /// <summary>
    /// This project's override of the task-lifetime review-cycle budget (cycles summed across
    /// every run and follow-up a task has had, immune to per-run resets); present-with-null
    /// clears it.
    /// </summary>
    Optional<int?> LifetimeReviewCycleBudget = default,
    Optional<BranchNameTemplate> BranchNameTemplate = default);
