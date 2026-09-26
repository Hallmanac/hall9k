using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class ProjectDetailsProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_then_apply_settings_builds_the_read_model_without_a_database()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git",
            new Uri("https://github.com/Hallmanac/hall9k"), "main", Now)));

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: new List<VerifyCommand> { new("build", "dotnet build"), new("test", "dotnet test") },
            SkipPermissions: true,
            MaxParallelAgents: 2,
            ContextLinks: new List<ContextLink> { new("jira", new Uri("https://example.atlassian.net")) },
            ChangedAt: Now.AddMinutes(5), ChangedByOwnerId: DomainId.New(),
            CommitStyle: CommitStyle.Append)), view);

        view.Name.Should().Be("hall9k");
        view.BaseBranch.Should().Be("main");
        view.VerifyCommands.Should().HaveCount(2);
        view.SkipPermissions.Should().BeTrue();
        view.MaxParallelAgents.Should().Be(2);
        view.ContextLinks.Should().ContainSingle(l => l.Name == "jira");
        view.CommitStyle.Should().Be(CommitStyle.Append);
        view.SettingsChangedAt.Should().Be(Now.AddMinutes(5));
    }

    /// <summary>
    /// The projection's <c>Create</c> reads <see cref="ProjectRegistered.SkipPermissions"/> the
    /// same way it already reads <see cref="ProjectRegistered.HomeDirectory"/>, so a project
    /// <c>h9k project add</c> registers with skip-permissions on reads that way with no
    /// <c>h9k project set</c> step in between — and a stream from before this field existed still
    /// reads false, the parameter's own default.
    /// </summary>
    [Fact]
    public void Create_reads_skip_permissions_straight_off_registration()
    {
        ProjectDetailsProjection projection = new();

        ProjectDetails registeredOn = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            DomainId.New(), DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git",
            null, "main", Now, HomeDirectory: null, SkipPermissions: true)));
        registeredOn.SkipPermissions.Should().BeTrue();

        ProjectDetails beforeThisChange = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            DomainId.New(), DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));
        beforeThisChange.SkipPermissions.Should().BeFalse();
    }

    /// <summary>
    /// The writing conventions read the platform default on a document nobody set one on, and an
    /// operator's own text survives a later change that leaves the field absent (task 412afe6c) —
    /// the same absent-means-left-alone contract every other setting on this event holds.
    /// </summary>
    [Fact]
    public void Writing_conventions_default_to_the_platform_text_and_survive_updates_that_leave_them_absent()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));
        view.WritingConventions.Should().Be(
            WritingConventions.Default, "a project nobody configured still has a house style");

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(1), ChangedByOwnerId: DomainId.New(),
            WritingConventions: Optional<WritingConventions>.Of(
                WritingConventions.Parse("Terse. British spelling.")))), view);
        view.WritingConventions.Value.Should().Be("Terse. British spelling.");

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: true,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(2), ChangedByOwnerId: DomainId.New())), view);
        view.WritingConventions.Value.Should().Be(
            "Terse. British spelling.", "a change that says nothing about them leaves them alone");
    }

    [Fact]
    public void Commit_style_defaults_to_unknown_and_survives_updates_that_leave_it_absent()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));
        view.CommitStyle.Should().Be(CommitStyle.Unknown, "an unset project uses the platform default");

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(1), ChangedByOwnerId: DomainId.New(),
            CommitStyle: CommitStyle.Narrative)), view);
        view.CommitStyle.Should().Be(CommitStyle.Narrative);

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: true,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(2), ChangedByOwnerId: DomainId.New())), view);
        view.CommitStyle.Should().Be(CommitStyle.Narrative, "an absent optional leaves the setting unchanged");
    }

    /// <summary>
    /// The per-project run ceiling the dispatcher reads (Decisions Log #140): absent leaves it
    /// alone, 0 is the pause the projection must carry as a real value rather than lose to a
    /// falsy reading, and present-with-null clears it back to uncapped.
    /// </summary>
    [Fact]
    public void The_parallel_task_cap_carries_zero_as_a_pause_and_a_cleared_value_as_uncapped()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));
        view.MaxParallelTasks.Should().BeNull("an untouched project is uncapped");

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(1), ChangedByOwnerId: DomainId.New(),
            MaxParallelTasks: Optional<int?>.Of(0))), view);
        view.MaxParallelTasks.Should().Be(0);

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: true,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(2), ChangedByOwnerId: DomainId.New())), view);
        view.MaxParallelTasks.Should().Be(0, "an absent optional leaves the pause standing");

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(3), ChangedByOwnerId: DomainId.New(),
            MaxParallelTasks: Optional<int?>.Of(null))), view);
        view.MaxParallelTasks.Should().BeNull("present-with-null clears the cap so the node ceiling decides");
    }

    /// <summary>
    /// A stream that recorded the retired session-denominated ceiling keeps exactly what it
    /// recorded, and it does not become a run cap: that is the whole retirement (Decisions Log
    /// #140) — the old number was never enforced, so it is named rather than converted.
    /// </summary>
    [Fact]
    public void The_retired_session_denominated_ceiling_replays_without_becoming_a_run_cap()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: 6,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(1), ChangedByOwnerId: DomainId.New())), view);

        view.MaxParallelAgents.Should().Be(6);
        view.MaxParallelTasks.Should().BeNull("the retired value is not carried into the enforced ceiling");
    }

    [Fact]
    public void Archive_then_reactivate_round_trips_the_read_model()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));

        projection.Apply(new FakeEvent<ProjectArchived>(
            new ProjectArchived(id, "Accidental registration", Now.AddMinutes(5), DomainId.New())), view);
        view.IsArchived.Should().BeTrue();
        view.ArchivedAt.Should().Be(Now.AddMinutes(5));
        view.ArchivedReason.Should().Be("Accidental registration");

        projection.Apply(new FakeEvent<ProjectReactivated>(
            new ProjectReactivated(id, Now.AddMinutes(10), DomainId.New())), view);
        view.IsArchived.Should().BeFalse();
        view.ArchivedAt.Should().BeNull();
        view.ArchivedReason.Should().BeNull();
        view.Name.Should().Be("hall9k", "reactivation touches only the archive fields");
    }

    [Fact]
    public void Rename_changes_only_the_name()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));

        projection.Apply(new FakeEvent<ProjectRenamed>(
            new ProjectRenamed(id, "hall9k", "hall9k-old", Now.AddMinutes(5), DomainId.New())), view);

        view.Name.Should().Be("hall9k-old");
        view.RepositoryPath.Should().Be("/repos/hall9k.git", "NAME IS NOT AN IDENTIFIER — nothing else changes");
    }

    [Fact]
    public void Purge_scheduled_then_cancelled_round_trips_the_deadline()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));

        DateTimeOffset deadline = Now.AddDays(1);
        projection.Apply(new FakeEvent<ProjectPurgeScheduled>(
            new ProjectPurgeScheduled(id, Now, deadline, DomainId.New())), view);
        view.PurgeAt.Should().Be(deadline);

        projection.Apply(new FakeEvent<ProjectPurgeCancelled>(
            new ProjectPurgeCancelled(id, Now.AddHours(1), DomainId.New())), view);
        view.PurgeAt.Should().BeNull();
    }

    /// <summary>
    /// Task: a delivered diff that touches no buildable or testable source skips the build and
    /// test gates. A project's own additions land on <see cref="ProjectDetails.NonExecutablePaths"/>
    /// exactly as recorded, and <see cref="ProjectDetails.EffectiveNonExecutablePaths"/> always
    /// layers the compiled defaults ahead of them — there is no event field, and so no
    /// command, that can ever remove one of them, which is what makes "a project can only add
    /// to the set" hold by construction.
    /// </summary>
    [Fact]
    public void Non_executable_path_additions_are_always_layered_on_top_of_the_compiled_defaults()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));

        view.NonExecutablePaths.Should().BeEmpty("an untouched project has made no additions");
        view.EffectiveNonExecutablePaths.Should().BeEquivalentTo(NonExecutablePathDefaults.Rules,
            options => options.WithStrictOrdering(),
            "the compiled defaults apply even with no project additions at all");

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(5), ChangedByOwnerId: DomainId.New(),
            NonExecutablePaths: new List<string> { "assets/**/*.png" })), view);

        view.NonExecutablePaths.Should().ContainSingle().Which.Should().Be("assets/**/*.png");
        view.EffectiveNonExecutablePaths.Should().Equal(
            [.. NonExecutablePathDefaults.Rules, "assets/**/*.png"],
            "the project's own addition rides on top of the compiled defaults, never in place of them");
    }

    private static ProjectDetails Registered(ProjectDetailsProjection projection) =>
        projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            DomainId.New(), DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));

    private static ProjectTeamSettingsChanged TeamChanged(
        DateTimeOffset changedAt, Optional<ClaimGate> claimGate = default, Optional<int?> maxComplianceCycles = default) =>
        new(DomainId.New(), changedAt, DomainId.New(), ClaimGate: claimGate, MaxComplianceReviewCycles: maxComplianceCycles);

    /// <summary>
    /// A replicated team change can be appended behind a newer one already applied: the ordinary
    /// post-switch-on flush lands the tail first and a catch-up answer serves the older head
    /// afterward. Each field keeps the value of the newest change that carried it, and an older
    /// change still fills in a field the newer one never touched.
    /// </summary>
    [Fact]
    public void An_older_team_change_arriving_second_never_overwrites_a_newer_field_but_fills_an_untouched_one()
    {
        ProjectDetailsProjection projection = new();
        ProjectDetails view = Registered(projection);

        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(TeamChanged(
            Now.AddDays(5), claimGate: Optional<ClaimGate>.Of(ClaimGate.Off))), view);
        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(TeamChanged(
            Now, claimGate: Optional<ClaimGate>.Of(ClaimGate.TrackerAssignee),
            maxComplianceCycles: Optional<int?>.Of(2))), view);

        view.ClaimGate.Should().Be(ClaimGate.Off, "the newer stamp already decided this field");
        view.MaxComplianceReviewCycles.Should().Be(2, "the newer change never carried this field");
    }

    [Fact]
    public void Team_changes_read_the_same_in_either_arrival_order()
    {
        ProjectTeamSettingsChanged newer = TeamChanged(Now.AddDays(5), claimGate: Optional<ClaimGate>.Of(ClaimGate.Off));
        ProjectTeamSettingsChanged older = TeamChanged(
            Now, claimGate: Optional<ClaimGate>.Of(ClaimGate.TrackerAssignee), maxComplianceCycles: Optional<int?>.Of(2));
        ProjectDetailsProjection projection = new();

        ProjectDetails inOrder = Registered(projection);
        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(older), inOrder);
        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(newer), inOrder);

        ProjectDetails reversed = Registered(projection);
        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(newer), reversed);
        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(older), reversed);

        reversed.ClaimGate.Should().Be(inOrder.ClaimGate);
        reversed.MaxComplianceReviewCycles.Should().Be(inOrder.MaxComplianceReviewCycles);
    }

    /// <summary>The node-scoped half writes the same fields, so it takes part in the same ordering.</summary>
    [Fact]
    public void An_older_team_change_never_overwrites_a_newer_local_settings_change()
    {
        ProjectDetailsProjection projection = new();
        ProjectDetails view = Registered(projection);

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            view.Id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddDays(5), ChangedByOwnerId: DomainId.New(),
            ClaimGate: Optional<ClaimGate>.Of(ClaimGate.Off))), view);
        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(TeamChanged(
            Now, claimGate: Optional<ClaimGate>.Of(ClaimGate.TrackerAssignee))), view);

        view.ClaimGate.Should().Be(ClaimGate.Off);
    }

    /// <summary>A removal stamped later than a vouch wins even when the vouch is applied second, and the reverse holds too.</summary>
    [Fact]
    public void A_member_is_last_writer_by_the_events_own_stamp_whatever_order_they_arrive_in()
    {
        ProjectDetailsProjection projection = new();
        const string Fingerprint = "SHA256:member";
        MemberVouched vouch = new(DomainId.New(), Fingerprint, ProjectMemberRole.Owner, Now);
        MemberRemoved laterRemoval = new(vouch.ProjectId, Fingerprint, Now.AddHours(1));

        ProjectDetails removalFirst = Registered(projection);
        projection.Apply(new FakeEvent<MemberRemoved>(laterRemoval), removalFirst);
        projection.Apply(new FakeEvent<MemberVouched>(vouch), removalFirst);
        removalFirst.Members.Should().NotContainKey(Fingerprint, "the removal is the later fact, whichever arrived first");

        ProjectDetails vouchFirst = Registered(projection);
        projection.Apply(new FakeEvent<MemberVouched>(vouch), vouchFirst);
        projection.Apply(new FakeEvent<MemberRemoved>(laterRemoval), vouchFirst);
        vouchFirst.Members.Should().NotContainKey(Fingerprint);

        ProjectDetails revouched = Registered(projection);
        MemberVouched laterVouch = vouch with { IssuedAt = Now.AddHours(2) };
        projection.Apply(new FakeEvent<MemberVouched>(laterVouch), revouched);
        projection.Apply(new FakeEvent<MemberRemoved>(laterRemoval), revouched);
        revouched.Members.Should().ContainKey(Fingerprint, "a vouch stamped after the removal stands");
    }

    [Fact]
    public void A_vouch_for_one_fingerprint_is_never_ordered_against_another()
    {
        ProjectDetailsProjection projection = new();
        ProjectDetails view = Registered(projection);

        projection.Apply(new FakeEvent<MemberVouched>(
            new MemberVouched(view.Id, "SHA256:newer", ProjectMemberRole.Owner, Now.AddDays(3))), view);
        projection.Apply(new FakeEvent<MemberVouched>(
            new MemberVouched(view.Id, "SHA256:older", ProjectMemberRole.Owner, Now)), view);

        view.Members.Keys.Should().BeEquivalentTo(["SHA256:newer", "SHA256:older"]);
    }

    [Fact]
    public void A_prompt_addendum_change_is_applied_only_when_its_stamp_is_not_older_than_the_applied_one()
    {
        ProjectDetailsProjection projection = new();
        ProjectDetails view = Registered(projection);
        Guid ownerId = DomainId.New();

        projection.Apply(new FakeEvent<ProjectPromptAddendumSet>(new ProjectPromptAddendumSet(
            view.Id, "builder", "newer text", false, null, Now.AddDays(2), ownerId)), view);
        projection.Apply(new FakeEvent<ProjectPromptAddendumSet>(new ProjectPromptAddendumSet(
            view.Id, "builder", "older text", false, null, Now, ownerId)), view);
        view.PromptAddenda["builder"].Content.Should().Be("newer text");

        projection.Apply(new FakeEvent<ProjectPromptAddendumRemoved>(
            new ProjectPromptAddendumRemoved(view.Id, "builder", Now.AddDays(3), ownerId)), view);
        projection.Apply(new FakeEvent<ProjectPromptAddendumSet>(new ProjectPromptAddendumSet(
            view.Id, "builder", "stale text", false, null, Now.AddDays(1), ownerId)), view);
        view.PromptAddenda.Should().NotContainKey("builder", "the removal is newer than the set that arrived after it");
    }

    [Fact]
    public void A_run_skill_recording_is_applied_only_when_its_stamp_is_not_older_than_the_applied_one()
    {
        ProjectDetailsProjection projection = new();
        ProjectDetails view = Registered(projection);
        Guid ownerId = DomainId.New();

        projection.Apply(new FakeEvent<ProjectRunSkillRecorded>(new ProjectRunSkillRecorded(
            view.Id, "newer skill", "full-text", "abc123", "discovery-session", Now.AddDays(2), ownerId)), view);
        projection.Apply(new FakeEvent<ProjectRunSkillRecorded>(new ProjectRunSkillRecorded(
            view.Id, "older skill", "full-text", "abc123", "discovery-session", Now, ownerId)), view);

        view.RunSkill!.Content.Should().Be("newer skill");
    }
}
