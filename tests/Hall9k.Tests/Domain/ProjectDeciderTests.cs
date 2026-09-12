using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class ProjectDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Matches the daemon's Marten setup, so the payload below is the stored shape.</summary>
    private static readonly JsonSerializerOptions StoredJson =
        new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void Register_produces_event_with_main_as_default_base_branch()
    {
        ProjectRegistered @event = ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: null, registeredAt: Now);

        @event.BaseBranch.Should().Be("main");
        @event.Name.Should().Be("hall9k");
    }

    /// <summary>
    /// The direction is load-bearing (Windows field report, 2026-08-31; ruling 2026-09-01): the
    /// default on the event record itself stays false, exactly the way <see cref="ProjectHome"/>
    /// stays <see cref="ProjectHome.None"/> by default on the same event, so a stream written
    /// before this field existed deserializes to the parameter default and replays unchanged
    /// rather than flipping every project that already exists. <c>h9k project add</c> is the one
    /// caller that passes <c>true</c> explicitly — simulated here the same way
    /// <c>ProjectHomeTests</c> simulates "a stream written before homes existed" for its own field:
    /// calling <see cref="ProjectDecider.Register"/> without the parameter at all.
    /// </summary>
    [Fact]
    public void Register_defaults_skip_permissions_to_false_and_a_project_add_call_passes_true_explicitly()
    {
        ProjectRegistered beforeThisChange = ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now);
        ProjectRegistered fromProjectAdd = ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now, skipPermissions: true);

        beforeThisChange.SkipPermissions.Should().BeFalse(
            "a stream written before this change carries no field and must replay as false");
        fromProjectAdd.SkipPermissions.Should().BeTrue();

        ProjectAggregate replayed = new();
        replayed.Apply(beforeThisChange);
        replayed.SkipPermissions.Should().BeFalse(
            "the aggregate replays exactly what an old stream's event carries — the parameter default");
    }

    /// <summary>
    /// The test above proves the C# default; this one proves the wire format still replays it.
    /// A stored <c>ProjectRegistered</c> written before <see cref="ProjectRegistered.SkipPermissions"/>
    /// existed has no <c>skipPermissions</c> property at all, so this deserializes an actual legacy
    /// payload with the repository's own serializer settings and applies the result, rather than
    /// only constructing the record in memory and relying on the C# parameter default to stand in
    /// for what Marten would produce.
    /// </summary>
    [Fact]
    public void An_event_written_before_skip_permissions_existed_replays_as_false()
    {
        const string storedBeforeSkipPermissions =
            """
            {"id":"01a01754-4f0e-7775-af1e-3aca2e67be8b","ownerId":"01a01754-4f0e-7775-af1e-3aca2e67be8c",
            "connectionId":"01a01754-4f0e-7775-af1e-3aca2e67be8d","name":"hall9k",
            "repositoryPath":"/repos/hall9k.git","repositoryUrl":null,"baseBranch":"main",
            "registeredAt":"2026-08-16T12:00:00+00:00"}
            """;

        ProjectRegistered? replayed = JsonSerializer.Deserialize<ProjectRegistered>(storedBeforeSkipPermissions, StoredJson);

        replayed.Should().NotBeNull();
        replayed!.SkipPermissions.Should().BeFalse(
            "what an old stream never recorded deserializes to the parameter default, not a guess");

        ProjectAggregate project = new();
        project.Apply(replayed);
        project.SkipPermissions.Should().BeFalse("the aggregate replays exactly what the legacy payload carries");
    }

    /// <summary>
    /// A later <c>h9k project set --skip-permissions</c> still wins over whatever registration
    /// recorded, in both directions — the regression guard this task's own acceptance criteria
    /// name (the behavior exists today; registration must not weaken it).
    /// </summary>
    [Fact]
    public void ChangeSettings_still_overrides_whatever_registration_recorded_in_either_direction()
    {
        ProjectAggregate registeredOn = new();
        registeredOn.Apply(ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now, skipPermissions: true));
        registeredOn.SkipPermissions.Should().BeTrue();

        registeredOn.Apply(ProjectDecider.ChangeSettings(
            registeredOn, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.Of(false),
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New()));
        registeredOn.SkipPermissions.Should().BeFalse("project set overrides the registration value");

        registeredOn.Apply(ProjectDecider.ChangeSettings(
            registeredOn, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.Of(true),
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New()));
        registeredOn.SkipPermissions.Should().BeTrue("project set overrides it back the other way too");
    }

    [Fact]
    public void Register_without_name_fails_validation()
    {
        Action act = () => ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: " ", repositoryPath: "/repos/x", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Register_without_connection_fails_validation()
    {
        Action act = () => ProjectDecider.Register(
            DomainId.New(), DomainId.New(), Guid.Empty,
            name: "x", repositoryPath: "/repos/x", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now);

        act.Should().Throw<DomainValidationException>();
    }

    /// <summary>
    /// The per-project run ceiling's own floor is 0, not 1 (Decisions Log #140): 0 is the
    /// deliberate pause, which is the one thing this cap can do that no other cap in this decider
    /// can. Below that there is nothing to mean, so it is refused with the rule quoted.
    /// </summary>
    [Fact]
    public void ChangeSettings_takes_a_parallel_task_cap_of_zero_as_a_pause_and_refuses_less()
    {
        ProjectAggregate project = RegisteredProject();

        ProjectSettingsChanged paused = ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            maxParallelTasks: Optional<int?>.Of(0));
        project.Apply(paused);
        project.MaxParallelTasks.Should().Be(0, "0 is the pause, not an invalid ceiling");

        Action act = () => ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            maxParallelTasks: Optional<int?>.Of(-1));

        act.Should().Throw<DomainValidationException>().WithMessage("*must be 0 or more*");
    }

    /// <summary>
    /// The clearing idiom the review caps already use, applied to this one: present-with-null
    /// hands the decision back to the node ceiling, which is what an untouched project runs on.
    /// </summary>
    [Fact]
    public void ChangeSettings_lets_a_cleared_parallel_task_cap_hand_the_decision_back_to_the_node()
    {
        ProjectAggregate project = RegisteredProject();
        project.MaxParallelTasks.Should().BeNull("an untouched project is uncapped");

        project.Apply(ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            maxParallelTasks: Optional<int?>.Of(2)));
        project.MaxParallelTasks.Should().Be(2);

        project.Apply(ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            maxParallelTasks: Optional<int?>.Of(null)));
        project.MaxParallelTasks.Should().BeNull("'default' clears the cap so the node ceiling alone decides");
    }

    /// <summary>
    /// The retired session-denominated ceiling is never written again (Decisions Log #140):
    /// there is no parameter left to write it with, so every event this decider produces from
    /// here on leaves the field absent and a project that recorded one keeps exactly what it
    /// recorded — which is what h9k project show reads to name the retirement.
    /// </summary>
    [Fact]
    public void ChangeSettings_never_writes_the_retired_session_denominated_ceiling_again()
    {
        ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
            RegisteredProject(),
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            maxParallelTasks: Optional<int?>.Of(1));

        changed.MaxParallelAgents.HasValue.Should().BeFalse();
        changed.MaxParallelTasks.Value.Should().Be(1);
    }

    [Fact]
    public void ChangeSettings_rejects_a_review_cap_below_one()
    {
        ProjectAggregate project = RegisteredProject();

        Action act = () => ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            maxAdversarialReviewCycles: Optional<int?>.Of(0));

        act.Should().Throw<DomainValidationException>().WithMessage("*--max-adversarial-review-cycles*");
    }

    [Fact]
    public void ChangeSettings_clears_a_review_cap_back_to_the_node_with_a_present_null()
    {
        ProjectAggregate project = RegisteredProject();
        project.Apply(ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            maxComplianceReviewCycles: Optional<int?>.Of(1)));
        project.MaxComplianceReviewCycles.Should().Be(1);

        project.Apply(ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            maxComplianceReviewCycles: Optional<int?>.Of(null)));

        project.MaxComplianceReviewCycles.Should().BeNull("present-with-null clears the override back to the node");
    }

    [Fact]
    public void ChangeSettings_rejects_a_commit_style_outside_the_vocabulary()
    {
        ProjectAggregate project = RegisteredProject();

        Action act = () => ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            commitStyle: Optional<CommitStyle>.Of("Squash"));

        act.Should().Throw<DomainValidationException>().WithMessage("*Narrative*Append*");
    }

    [Fact]
    public void ChangeSettings_accepts_unknown_commit_style_as_clearing_the_override()
    {
        ProjectAggregate project = RegisteredProject();
        project.Apply(new ProjectSettingsChanged(
            project.Id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now, ChangedByOwnerId: DomainId.New(),
            CommitStyle: CommitStyle.Append));
        project.CommitStyle.Should().Be(CommitStyle.Append);

        ProjectSettingsChanged cleared = ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            commitStyle: Optional<CommitStyle>.Of(CommitStyle.Unknown));
        project.Apply(cleared);

        project.CommitStyle.Should().Be(CommitStyle.Unknown, "the platform default applies again");
    }

    [Fact]
    public void Aggregate_applies_settings_only_where_optionals_are_present()
    {
        ProjectAggregate project = RegisteredProject();
        project.Apply(new ProjectSettingsChanged(
            project.Id,
            VerifyCommands: new List<VerifyCommand> { new("test", "dotnet test") },
            SkipPermissions: true,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now, ChangedByOwnerId: DomainId.New()));

        project.VerifyCommands.Should().ContainSingle(c => c.Name == "test");
        project.SkipPermissions.Should().BeTrue();
        project.MaxParallelAgents.Should().Be(3, "absent optionals leave settings unchanged");
    }

    /// <summary>
    /// A registered project cuts branches exactly as the platform always has, so nothing changes
    /// for anybody who never states a convention.
    /// </summary>
    [Fact]
    public void A_project_that_sets_no_branch_template_carries_the_platform_default()
    {
        RegisteredProject().BranchNameTemplate.Should().Be(BranchNameTemplate.Default);
    }

    [Fact]
    public void ChangeSettings_records_a_branch_template_and_clears_it_back_to_the_default()
    {
        ProjectAggregate project = RegisteredProject();

        project.Apply(ChangeBranchTemplate(project, BranchNameTemplate.Parse("{key}-{slug}")));
        project.BranchNameTemplate.Value.Should().Be("{key}-{slug}");

        project.Apply(ChangeBranchTemplate(project, BranchNameTemplate.Default));
        project.BranchNameTemplate.Should().Be(BranchNameTemplate.Default,
            "'none' restores the platform default, which is what the CLI maps the word to");
    }

    /// <summary>
    /// The refusal lands here, at the settings change, rather than at the dispatch that would fail
    /// on it — the whole point of validating a template a human can still see and fix.
    /// </summary>
    [Fact]
    public void ChangeSettings_refuses_a_template_that_renders_an_illegal_git_ref()
    {
        ProjectAggregate project = RegisteredProject();

        Action act = () => ChangeBranchTemplate(project, BranchNameTemplate.FromInput("task/{slug}:{shortid}"));

        act.Should().Throw<DomainValidationException>().WithMessage("*git does not allow*");
    }

    [Fact]
    public void ChangeSettings_refuses_a_template_naming_a_token_that_does_not_exist()
    {
        ProjectAggregate project = RegisteredProject();

        Action act = () => ChangeBranchTemplate(project, BranchNameTemplate.FromInput("task/{epic}-{slug}"));

        act.Should().Throw<DomainValidationException>().WithMessage("*is not a token*");
    }

    /// <summary>
    /// A project that never states a house style still has one: the platform's own, which is what
    /// every composition prompt pastes (task 412afe6c).
    /// </summary>
    [Fact]
    public void A_project_that_states_no_writing_conventions_carries_the_platform_default()
    {
        RegisteredProject().WritingConventions.Should().Be(WritingConventions.Default);
    }

    [Fact]
    public void ChangeSettings_records_writing_conventions_and_clears_them_back_to_the_default()
    {
        ProjectAggregate project = RegisteredProject();

        project.Apply(ChangeWritingConventions(project, WritingConventions.Parse("Terse. British spelling.")));
        project.WritingConventions.Value.Should().Be("Terse. British spelling.");

        project.Apply(ChangeWritingConventions(project, WritingConventions.Default));
        project.WritingConventions.Should().Be(WritingConventions.Default,
            "'default' restores the platform's own, which is what the CLI maps the word to");
    }

    /// <summary>
    /// The refusal lands at the settings change, the same discipline the branch template follows:
    /// what reaches the stream is what h9k project show prints and what every prompt pastes, so a
    /// text nothing can render is refused where a human can still see and fix it.
    /// </summary>
    [Fact]
    public void ChangeSettings_refuses_writing_conventions_a_terminal_cannot_show()
    {
        ProjectAggregate project = RegisteredProject();

        Action act = () => ChangeWritingConventions(
            project, WritingConventions.Parse("Write plainly.").Value + (char)0x200B);

        act.Should().Throw<DomainValidationException>().WithMessage("*U+200B*");
    }

    private static ProjectSettingsChanged ChangeWritingConventions(
        ProjectAggregate project, WritingConventions conventions) =>
        ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            writingConventions: Optional<WritingConventions>.Of(conventions));

    private static ProjectSettingsChanged ChangeBranchTemplate(ProjectAggregate project, BranchNameTemplate template) =>
        ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            branchNameTemplate: Optional<BranchNameTemplate>.Of(template));

    private static ProjectAggregate RegisteredProject()
    {
        ProjectAggregate project = new();
        project.Apply(ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: null, registeredAt: Now));
        return project;
    }

    /// <summary>
    /// A project's model default is the third link in the chain (Decisions Log #33), and
    /// Unknown is a legal explicit value: it clears the override so the node's per-role and
    /// platform defaults decide again, exactly how CommitStyle behaves.
    /// </summary>
    [Fact]
    public void Change_settings_carries_a_model_default_and_lets_unknown_clear_it()
    {
        ProjectAggregate project = Registered();

        ProjectSettingsChanged set = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            model: Optional<AgentModel>.Of(AgentModel.FromInput("claude-sonnet-5")));
        project.Apply(set);
        project.Model.Value.Should().Be("claude-sonnet-5");

        ProjectSettingsChanged cleared = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            model: Optional<AgentModel>.Of(AgentModel.FromInput("default")));
        project.Apply(cleared);
        project.Model.Should().Be(AgentModel.Unknown, "'default' hands the decision back to the chain");

        ProjectSettingsChanged untouched = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New());
        untouched.Model.HasValue.Should().BeFalse("an option not passed leaves the setting alone");
    }

    [Fact]
    public void Change_settings_rejects_a_model_that_could_not_be_handed_to_the_executors_shell()
    {
        Action act = () => ProjectDecider.ChangeSettings(
            Registered(), Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            model: Optional<AgentModel>.Of(AgentModel.FromInput("$(id)")));

        act.Should().Throw<DomainValidationException>().WithMessage("*not a usable model name*");
    }

    /// <summary>
    /// The orchestrator-window model override (task: an operator starts a lean node or project
    /// orchestrator window) is independent of the agent-dispatch <c>Model</c> above — the same
    /// clearing idiom, but a distinct field, so raising or lowering the model dispatched agents
    /// run on never silently moves the operator's own window.
    /// </summary>
    [Fact]
    public void Change_settings_carries_an_orchestrator_model_override_independent_of_the_agent_dispatch_model()
    {
        ProjectAggregate project = Registered();

        ProjectSettingsChanged set = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            model: Optional<AgentModel>.Of(AgentModel.FromInput("claude-sonnet-5")),
            orchestratorModel: Optional<AgentModel>.Of(AgentModel.FromInput("claude-opus-5")));
        project.Apply(set);
        project.Model.Value.Should().Be("claude-sonnet-5");
        project.OrchestratorModel.Value.Should().Be("claude-opus-5");

        ProjectSettingsChanged cleared = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            orchestratorModel: Optional<AgentModel>.Of(AgentModel.FromInput("default")));
        project.Apply(cleared);
        project.OrchestratorModel.Should().Be(AgentModel.Unknown, "'default' hands the decision back to the chain");
        project.Model.Value.Should().Be("claude-sonnet-5", "clearing the orchestrator override leaves the dispatch model untouched");
    }

    [Fact]
    public void Change_settings_rejects_an_orchestrator_model_that_could_not_be_handed_to_the_executors_shell()
    {
        Action act = () => ProjectDecider.ChangeSettings(
            Registered(), Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            orchestratorModel: Optional<AgentModel>.Of(AgentModel.FromInput("$(id)")));

        act.Should().Throw<DomainValidationException>().WithMessage("*not a usable model name*");
    }

    /// <summary>
    /// Task: the review pipeline's stage composition becomes configuration recorded per run —
    /// the project-level door, h9k project set, canonicalizes an alias and 'default' clears it,
    /// the same shape --model already has.
    /// </summary>
    [Fact]
    public void Change_settings_carries_a_review_stage_composition_override_and_lets_default_clear_it()
    {
        ProjectAggregate project = Registered();

        ProjectSettingsChanged set = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            reviewStageComposition: Optional<string?>.Of("conformance-only"), reviewStageCompositionAcknowledged: true);
        project.Apply(set);
        project.ReviewStageComposition?.Value.Should().Be("ConformanceOnly");

        ProjectSettingsChanged cleared = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            reviewStageComposition: Optional<string?>.Of("default"));
        project.Apply(cleared);
        project.ReviewStageComposition.Should().BeNull("'default' hands the decision back to the node");

        ProjectSettingsChanged untouched = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New());
        untouched.ReviewStageComposition.HasValue.Should().BeFalse("an option not passed leaves the setting alone");
    }

    [Fact]
    public void Change_settings_refuses_none_without_acknowledgment_naming_decision_92()
    {
        Action act = () => ProjectDecider.ChangeSettings(
            Registered(), Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            reviewStageComposition: Optional<string?>.Of("none"));

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*Decisions Log #92*")
            .WithMessage("*--accept-reduced-review*");
    }

    [Fact]
    public void Change_settings_accepts_none_when_acknowledged_and_records_the_attestation()
    {
        ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
            Registered(), Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            reviewStageComposition: Optional<string?>.Of("none"), reviewStageCompositionAcknowledged: true);

        changed.ReviewStageComposition.Value?.Value.Should().Be("None");
        changed.ReviewStageCompositionAcknowledged.Should().BeTrue();
    }

    /// <summary>
    /// Never assert an unobserved fact (AGENTS.md) — the TaskPublished.UntrackedAttested clamp
    /// idiom: an acknowledgment passed alongside a composition that never needed one is not
    /// recorded, so the stream never claims a guarantee was traded away when none was.
    /// </summary>
    [Fact]
    public void Change_settings_never_records_an_attestation_for_full_pipeline_even_if_acknowledgment_was_passed()
    {
        ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
            Registered(), Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            reviewStageComposition: Optional<string?>.Of("full-pipeline"), reviewStageCompositionAcknowledged: true);

        changed.ReviewStageComposition.Value?.Value.Should().Be("FullPipeline");
        changed.ReviewStageCompositionAcknowledged.Should().BeFalse();
    }

    /// <summary>
    /// The AcceptedBrokenGate idiom mirrors ReviewStageCompositionAcknowledged's own clamp
    /// (independent pre-PR review, conformance lens, low): a caller cannot write an unobserved
    /// acceptance to the stream by passing true on a change that carries no VerifyCommands at all.
    /// </summary>
    [Fact]
    public void Change_settings_never_records_an_accepted_broken_gate_when_no_verify_commands_are_carried()
    {
        ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
            Registered(), Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            acceptedBrokenGate: true);

        changed.AcceptedBrokenGate.Should().BeFalse(
            "there is no gate on this change for the acceptance to be about");
    }

    [Fact]
    public void Change_settings_records_an_accepted_broken_gate_alongside_the_verify_commands_it_describes()
    {
        ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
            Registered(),
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.Of([new VerifyCommand("test", "dotnet test")]),
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            acceptedBrokenGate: true);

        changed.AcceptedBrokenGate.Should().BeTrue();
    }

    private static ProjectAggregate Registered()
    {
        ProjectAggregate project = new();
        project.Apply(ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now));
        return project;
    }

    [Fact]
    public void ChangeSettings_rejects_a_backlog_policy_outside_the_vocabulary()
    {
        Action act = () => ProjectDecider.ChangeSettings(
            Registered(), Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            backlogPolicy: Optional<BacklogPolicy>.Of("trello"));

        act.Should().Throw<DomainValidationException>().WithMessage("*None*GitHubIssues*Jira*");
    }

    [Fact]
    public void ChangeSettings_carries_a_backlog_policy_and_lets_none_clear_it()
    {
        ProjectAggregate project = Registered();

        ProjectSettingsChanged set = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            backlogPolicy: Optional<BacklogPolicy>.Of(BacklogPolicy.GitHubIssues));
        project.Apply(set);
        project.BacklogPolicy.Should().Be(BacklogPolicy.GitHubIssues);

        ProjectSettingsChanged cleared = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            backlogPolicy: Optional<BacklogPolicy>.Of(BacklogPolicy.None));
        project.Apply(cleared);
        project.BacklogPolicy.Should().Be(BacklogPolicy.None, "none is both the default and the explicit stop");

        ProjectSettingsChanged untouched = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New());
        untouched.BacklogPolicy.HasValue.Should().BeFalse("an option not passed leaves the setting alone");
    }

    [Fact]
    public void ChangeSettings_rejects_an_auto_pr_review_speed_outside_the_vocabulary()
    {
        Action act = () => ProjectDecider.ChangeSettings(
            Registered(), Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            autoPrReview: Optional<AutoPrReviewSpeed>.Of("fast"));

        act.Should().Throw<DomainValidationException>().WithMessage("*Off*Normal*First*Now*");
    }

    [Fact]
    public void ChangeSettings_carries_an_auto_pr_review_speed_and_lets_off_clear_it()
    {
        ProjectAggregate project = Registered();
        project.AutoPrReview.Should().Be(AutoPrReviewSpeed.Off, "off is the platform's original behavior, byte-for-byte");

        ProjectSettingsChanged set = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            autoPrReview: Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Now));
        project.Apply(set);
        project.AutoPrReview.Should().Be(AutoPrReviewSpeed.Now);

        ProjectSettingsChanged cleared = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            autoPrReview: Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Off));
        project.Apply(cleared);
        project.AutoPrReview.Should().Be(AutoPrReviewSpeed.Off);

        ProjectSettingsChanged untouched = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New());
        untouched.AutoPrReview.HasValue.Should().BeFalse("an option not passed leaves the setting alone");
    }

    [Fact]
    public void ChangeSettings_lets_a_blank_routing_guidance_clear_it()
    {
        ProjectAggregate project = Registered();
        project.Apply(ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            backlogRoutingGuidance: Optional<string>.Of("epic-first")));
        project.BacklogRoutingGuidance.Should().Be("epic-first");

        project.Apply(ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            backlogRoutingGuidance: Optional<string>.Of(string.Empty)));
        project.BacklogRoutingGuidance.Should().BeNull("present but empty clears it, the ContextLinks/JiraProjectKey idiom");
    }

    [Fact]
    public void ChangeSettings_records_launch_texts_normalizing_the_cli_name()
    {
        ProjectAggregate project = RegisteredProject();

        project.Apply(ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            launchTexts: Optional<IReadOnlyList<LaunchText>>.Of([new LaunchText("Claude-Code", "claude --strict-mcp-config")])));

        project.LaunchTexts.Should().ContainSingle();
        project.LaunchTexts[0].Cli.Should().Be("claude-code", "the stored key is normalized so a later lookup by any casing finds it");
        project.LaunchTexts[0].Text.Should().Be("claude --strict-mcp-config");
    }

    [Fact]
    public void ChangeSettings_refuses_a_launch_text_with_no_cli_or_no_text()
    {
        ProjectAggregate project = RegisteredProject();

        Action noCli = () => ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            launchTexts: Optional<IReadOnlyList<LaunchText>>.Of([new LaunchText(" ", "some text")]));
        noCli.Should().Throw<DomainValidationException>();

        Action noText = () => ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            launchTexts: Optional<IReadOnlyList<LaunchText>>.Of([new LaunchText("claude-code", " ")]));
        noText.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Archive_produces_an_event_and_the_aggregate_replays_it_archived()
    {
        ProjectAggregate project = RegisteredProject();

        ProjectArchived archived = ProjectDecider.Archive(project, "Accidental registration", Now, DomainId.New());
        project.Apply(archived);

        project.IsArchived.Should().BeTrue();
        project.ArchivedAt.Should().Be(Now);
        project.ArchivedReason.Should().Be("Accidental registration");
    }

    [Fact]
    public void Archive_leaves_the_reason_unknown_when_blank_never_inferred()
    {
        ProjectArchived archived = ProjectDecider.Archive(RegisteredProject(), reason: "  ", Now, DomainId.New());

        archived.Reason.Should().BeNull("a blank reason is recorded as unknown, never inferred (h9k task abandon's own discipline)");
    }

    [Fact]
    public void Archive_refuses_a_project_already_archived()
    {
        ProjectAggregate project = RegisteredProject();
        project.Apply(ProjectDecider.Archive(project, null, Now, DomainId.New()));

        Action archiveAgain = () => ProjectDecider.Archive(project, null, Now.AddDays(1), DomainId.New());

        archiveAgain.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Reactivate_clears_the_archive_in_place_leaving_id_and_settings_untouched()
    {
        ProjectAggregate project = RegisteredProject();
        Guid originalId = project.Id;
        project.Apply(ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            priority: Optional<ProjectPriority>.Of(ProjectPriority.High)));
        project.Apply(ProjectDecider.Archive(project, "temporary", Now, DomainId.New()));

        ProjectReactivated reactivated = ProjectDecider.Reactivate(project, Now.AddDays(1), DomainId.New());
        project.Apply(reactivated);

        project.IsArchived.Should().BeFalse();
        project.ArchivedAt.Should().BeNull();
        project.ArchivedReason.Should().BeNull();
        project.Id.Should().Be(originalId, "reactivation is the same stream, the same id — never a new registration");
        project.Priority.Should().Be(ProjectPriority.High, "every other setting is untouched by an archive/reactivate round trip");
    }

    [Fact]
    public void Reactivate_refuses_a_project_that_is_not_archived()
    {
        Action reactivate = () => ProjectDecider.Reactivate(RegisteredProject(), Now, DomainId.New());

        reactivate.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Rename_changes_the_name_only()
    {
        ProjectAggregate project = RegisteredProject();
        string originalRepositoryPath = project.RepositoryPath;
        Guid originalId = project.Id;

        ProjectRenamed renamed = ProjectDecider.Rename(project, "hall9k-old", Now, DomainId.New());
        project.Apply(renamed);

        renamed.PreviousName.Should().Be("hall9k");
        renamed.NewName.Should().Be("hall9k-old");
        project.Name.Should().Be("hall9k-old");
        project.Id.Should().Be(originalId);
        project.RepositoryPath.Should().Be(originalRepositoryPath, "NAME IS NOT AN IDENTIFIER — nothing else on the aggregate changes");
    }

    [Fact]
    public void Rename_refuses_a_blank_name_and_refuses_the_projects_own_current_name()
    {
        ProjectAggregate project = RegisteredProject();

        Action blank = () => ProjectDecider.Rename(project, " ", Now, DomainId.New());
        blank.Should().Throw<DomainValidationException>();

        Action sameName = () => ProjectDecider.Rename(project, "hall9k", Now, DomainId.New());
        sameName.Should().Throw<DomainValidationException>();
    }
}
