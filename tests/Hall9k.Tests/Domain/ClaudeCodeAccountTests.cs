using FluentAssertions;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The domain half of running a project's Claude Code sessions under a node-local claude-code connection:
/// the one path rule the directory is recorded under, what a claude-code connection may record, where a
/// project's binding lives, and which account each piece of work resolves to.
/// </summary>
public sealed class ClaudeCodeAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Home = Path.TrimEndingDirectorySeparator(Path.Combine(Path.GetTempPath(), "h9k-home"));
    private static readonly string Directory = Path.Join(Home, ".claude-alt");

    // ---- the path rule ----

    [Fact]
    public void A_tilde_path_is_expanded_against_the_home_directory()
    {
        ClaudeConfigDirectory.Normalize("~/.claude-alt", Home).Should().Be(Directory);
        ClaudeConfigDirectory.Normalize("~", Home).Should().Be(Home);
    }

    [Fact]
    public void A_trailing_separator_is_trimmed_so_the_recorded_string_is_the_one_every_spawn_passes()
    {
        ClaudeConfigDirectory.Normalize(Directory + Path.DirectorySeparatorChar, Home).Should().Be(Directory);
        ClaudeConfigDirectory.Normalize("  " + Directory + "  ", Home).Should().Be(Directory);
    }

    [Fact]
    public void The_path_is_never_resolved_through_a_symlink_or_canonicalized()
    {
        // Claude Code keys the macOS Keychain item to the exact string, so a path naming the same directory
        // another way stays a different string rather than being rewritten to the target.
        string dotted = Path.Join(Home, "links", "..", ".claude-alt");

        ClaudeConfigDirectory.Normalize(dotted, Home).Should().Be(dotted);
    }

    [Fact]
    public void A_relative_path_is_refused()
    {
        Action act = () => ClaudeConfigDirectory.Normalize(".claude-alt", Home);

        act.Should().Throw<DomainValidationException>().WithMessage("*not an absolute path*~/.claude-alt*");
    }

    [Fact]
    public void A_filesystem_root_is_refused()
    {
        string root = Path.GetPathRoot(Home) ?? Path.DirectorySeparatorChar.ToString();

        Action act = () => ClaudeConfigDirectory.Normalize(root, Home);

        act.Should().Throw<DomainValidationException>().WithMessage("*filesystem root*");
    }

    [Fact]
    public void A_blank_directory_is_refused_with_an_example()
    {
        Action act = () => ClaudeConfigDirectory.Normalize("  ", Home);

        act.Should().Throw<DomainValidationException>().WithMessage("*~/.claude-alt*");
    }

    // ---- the connection ----

    private static ConnectionRegistered RegisterClaudeCode(string? directory = null, CredentialReference? credential = null) =>
        ConnectionDecider.Register(
            DomainId.New(), DomainId.New(), WorkItemProvider.ClaudeCode, directory ?? Directory,
            credential ?? CredentialReference.ClaudeConfigDirectory, Now);

    [Fact]
    public void A_claude_code_connection_records_its_directory_once_and_no_secret()
    {
        ConnectionRegistered registered = RegisterClaudeCode();

        registered.ExternalAccountId.Should().Be(Directory);
        registered.CredentialReference.Kind.Should().Be(CredentialKind.ClaudeConfigDirectory);
        registered.CredentialReference.Identifier.Should().BeNull();
    }

    [Fact]
    public void A_claude_code_connection_with_any_other_credential_kind_is_refused()
    {
        Action act = () => RegisterClaudeCode(credential: CredentialReference.File("claude"));

        act.Should().Throw<DomainValidationException>().WithMessage("*claude-config-dir*");
    }

    [Fact]
    public void Another_provider_may_not_use_a_claude_config_directory_credential()
    {
        Action act = () => ConnectionDecider.Register(
            DomainId.New(), DomainId.New(), WorkItemProvider.GitHub, "someone",
            CredentialReference.ClaudeConfigDirectory, Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*belongs to claude-code connections alone*");
    }

    [Fact]
    public void A_claude_code_connection_records_only_an_absolute_directory_with_no_trailing_separator()
    {
        Action relative = () => RegisterClaudeCode(directory: ".claude-alt");
        Action trailing = () => RegisterClaudeCode(directory: Directory + Path.DirectorySeparatorChar);

        relative.Should().Throw<DomainValidationException>();
        trailing.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void A_github_connection_still_registers_through_gh_unchanged()
    {
        ConnectionRegistered registered = ConnectionDecider.Register(
            DomainId.New(), DomainId.New(), WorkItemProvider.GitHub, "someone", CredentialReference.GhCli, Now);

        registered.CredentialReference.Should().Be(CredentialReference.GhCli);
    }

    private static ConnectionAggregate Connection()
    {
        ConnectionAggregate aggregate = new();
        aggregate.Apply(RegisterClaudeCode());
        return aggregate;
    }

    [Fact]
    public void An_observation_records_the_email_org_and_subscription_and_repeats_as_nothing()
    {
        ConnectionAggregate connection = Connection();

        ConnectionClaudeCodeAccountObserved? first = ConnectionDecider.ObserveClaudeCodeAccount(
            connection, "dev@example.com", "org-1", "Example Org", "max", Now);
        first.Should().NotBeNull();
        connection.Apply(first!);

        ConnectionDecider.ObserveClaudeCodeAccount(connection, "dev@example.com", "org-1", "Example Org", "max", Now)
            .Should().BeNull("the same account observed again changes nothing");
    }

    [Theory]
    [InlineData("", "org-1")]
    [InlineData("dev@example.com", "")]
    public void An_observation_without_an_email_or_an_org_id_is_refused(string email, string organizationId)
    {
        Action act = () => ConnectionDecider.ObserveClaudeCodeAccount(Connection(), email, organizationId, null, null, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void The_account_a_connection_names_carries_the_exact_recorded_directory_and_identity()
    {
        ConnectionDetails details = new()
        {
            Id = DomainId.New(),
            Provider = WorkItemProvider.ClaudeCode,
            ExternalAccountId = Directory,
            ClaudeCodeEmail = "dev@example.com",
            ClaudeCodeOrganizationId = "org-1",
        };

        ClaudeCodeAccount account = ClaudeCodeAccount.For(details);

        account.ConfigDirectory.Should().Be(Directory);
        account.Email.Should().Be("dev@example.com");
        account.OrganizationId.Should().Be("org-1");
        account.IsDefault.Should().BeFalse();
        ClaudeCodeAccount.Default.IsDefault.Should().BeTrue();
    }

    // ---- the binding lives on node-local settings ----

    private static ProjectAggregate Project()
    {
        ProjectAggregate project = new();
        project.Apply(ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "sample", repositoryPath: "/repos/sample.git", repositoryUrl: null, baseBranch: "main", registeredAt: Now));
        return project;
    }

    private static ProjectSettingsChanged Bind(ProjectAggregate project, Guid? connectionId, bool alsoChangeCommitStyle = false) =>
        ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            DomainId.New(),
            commitStyle: alsoChangeCommitStyle ? Optional<CommitStyle>.Of(CommitStyle.Narrative) : Optional<CommitStyle>.None,
            claudeCodeConnectionId: Optional<Guid?>.Of(connectionId));

    [Fact]
    public void The_binding_rides_on_project_settings_changed_and_its_projection()
    {
        ProjectAggregate project = Project();
        Guid connectionId = DomainId.New();

        ProjectSettingsChanged changed = Bind(project, connectionId);
        project.Apply(changed);
        ProjectDetails view = new() { Id = project.Id };
        new ProjectDetailsProjection().Apply(new FakeEvent<ProjectSettingsChanged>(changed), view);

        changed.ClaudeCodeConnectionId.Value.Should().Be(connectionId);
        project.ClaudeCodeConnectionId.Should().Be(connectionId);
        view.ClaudeCodeConnectionId.Should().Be(connectionId);

        ProjectSettingsChanged cleared = Bind(project, null);
        new ProjectDetailsProjection().Apply(new FakeEvent<ProjectSettingsChanged>(cleared), view);
        view.ClaudeCodeConnectionId.Should().BeNull("'default' clears the binding");
    }

    [Fact]
    public void The_team_settings_companion_never_carries_the_binding()
    {
        ProjectAggregate project = Project();

        ProjectTeamSettingsChanged.From(Bind(project, DomainId.New()))
            .Should().BeNull("a binding alone touches no team field, so nothing replicates");

        ProjectTeamSettingsChanged? team = ProjectTeamSettingsChanged.From(Bind(project, DomainId.New(), alsoChangeCommitStyle: true));
        team.Should().NotBeNull();
        typeof(ProjectTeamSettingsChanged).GetProperties()
            .Select(property => property.Name)
            .Should().NotContain(name => name.Contains("ClaudeCode"), "the binding names a directory on this node alone");
    }

    // ---- which account each piece of work runs under ----

    private static RunDetails Run(Guid? frozen, bool recorded = true)
    {
        RunDetails run = new() { Id = DomainId.New(), State = RunState.Running };
        if (recorded)
        {
            new RunDetailsProjection().Apply(
                new FakeEvent<RunClaudeCodeAccountRecorded>(new RunClaudeCodeAccountRecorded(run.Id, frozen, Now)), run);
        }

        return run;
    }

    [Fact]
    public void A_run_keeps_the_account_it_froze_when_its_project_is_rebound_mid_run()
    {
        Guid original = DomainId.New();
        Guid rebound = DomainId.New();
        ProjectDetails project = new() { Id = DomainId.New(), ClaudeCodeConnectionId = original };
        RunDetails run = Run(ClaudeCodeAccountBinding.ForProject(project));

        project.ClaudeCodeConnectionId = rebound;

        ClaudeCodeAccountBinding.ForRun(run).Should().Be(original);
        ClaudeCodeAccountBinding.ForProject(project).Should().Be(rebound, "only work dispatched afterwards moves");
    }

    [Fact]
    public void A_run_dispatched_before_the_binding_existed_uses_the_default_login()
    {
        ClaudeCodeAccountBinding.ForRun(Run(frozen: null, recorded: false)).Should().BeNull();
    }

    [Fact]
    public void A_run_frozen_on_the_default_login_stays_there_after_its_project_is_bound()
    {
        RunDetails run = Run(frozen: null);

        ClaudeCodeAccountBinding.ForRun(run).Should().BeNull();
    }

    [Fact]
    public void A_pre_flight_uses_its_dispatching_runs_account_and_the_project_before_that_run_exists()
    {
        Guid frozen = DomainId.New();
        ProjectDetails project = new() { Id = DomainId.New(), ClaudeCodeConnectionId = DomainId.New() };

        ClaudeCodeAccountBinding.ForPreflight(Run(frozen), project).Should().Be(frozen);
        ClaudeCodeAccountBinding.ForPreflight(null, project).Should().Be(project.ClaudeCodeConnectionId);
        ClaudeCodeAccountBinding.ForPreflight(Run(null, recorded: false), project).Should().BeNull(
            "a run dispatched before the binding existed keeps the default login, pre-flight included");
    }

    [Fact]
    public void A_courier_uses_the_binding_of_the_project_its_own_dispatch_names()
    {
        ProjectDetails project = new() { Id = DomainId.New(), ClaudeCodeConnectionId = DomainId.New() };
        CourierRunDispatched dispatched = new(DomainId.New(), project.Id, DomainId.New(), AgentModel.Sonnet, Now);
        CourierRunDispatched elsewhere = new(DomainId.New(), DomainId.New(), DomainId.New(), AgentModel.Sonnet, Now);

        ClaudeCodeAccountBinding.ForCourier(dispatched, project).Should().Be(project.ClaudeCodeConnectionId);
        ClaudeCodeAccountBinding.ForCourier(elsewhere, project).Should().BeNull();
        ClaudeCodeAccountBinding.ForCourier(dispatched, null).Should().BeNull();
    }

    [Fact]
    public void A_solution_follows_its_members_only_when_they_all_agree()
    {
        Guid shared = DomainId.New();

        ClaudeCodeAccountBinding.ForSolution([shared, shared]).Should().Be(shared);
        ClaudeCodeAccountBinding.ForSolution([shared, DomainId.New()]).Should().BeNull();
        ClaudeCodeAccountBinding.ForSolution([shared, null]).Should().BeNull();
        ClaudeCodeAccountBinding.ForSolution([]).Should().BeNull();
    }

    // ---- a refused account holds a run ----

    [Fact]
    public void An_account_hold_parks_the_run_where_it_was_and_a_node_hold_clears_the_account_it_named()
    {
        Guid connectionId = DomainId.New();
        RunDetails run = new() { Id = DomainId.New(), State = RunState.Verifying };
        RunDetailsProjection projection = new();

        projection.Apply(new FakeEvent<RunAccountHeld>(new RunAccountHeld(run.Id, connectionId, "logged out", Now)), run);

        run.State.Should().Be(RunState.LaunchHeld);
        run.AccountHeldConnectionId.Should().Be(connectionId);
        run.AccountHeldFromState.Should().Be(RunState.Verifying, "the resume re-enters the gates it was held at");
        run.ParkedReason.Should().Contain("Claude Code account").And.Contain("logged out");

        projection.Apply(new FakeEvent<RunAccountHeld>(new RunAccountHeld(run.Id, connectionId, "still logged out", Now)), run);
        run.AccountHeldFromState.Should().Be(RunState.Verifying, "a second hold does not overwrite where the run was");
    }

    [Fact]
    public void A_refused_primary_session_resumes_by_starting_that_session_even_after_the_run_reached_verifying()
    {
        // CompleteRunAsync appends AgentSessionCompleted (which moves the run to Verifying) in the same commit as
        // the hold, so the hold has to say it was the primary session or the resume would skip straight to gates.
        RunDetails run = new() { Id = DomainId.New(), State = RunState.Verifying };

        new RunDetailsProjection().Apply(
            new FakeEvent<RunAccountHeld>(new RunAccountHeld(run.Id, DomainId.New(), "logged out", Now, PrimarySession: true)), run);

        run.State.Should().Be(RunState.LaunchHeld);
        run.AccountHeldFromState.Should().BeNull();
    }

    [Fact]
    public void A_node_wide_hold_clears_where_an_earlier_account_hold_left_the_run()
    {
        RunDetails run = new() { Id = DomainId.New(), State = RunState.UnderReview };
        RunDetailsProjection projection = new();
        projection.Apply(new FakeEvent<RunAccountHeld>(new RunAccountHeld(run.Id, DomainId.New(), "logged out", Now)), run);

        projection.Apply(new FakeEvent<RunLaunchHeld>(new RunLaunchHeld(run.Id, "no work done", Now)), run);

        run.AccountHeldConnectionId.Should().BeNull();
        run.AccountHeldFromState.Should().BeNull();
    }

    [Fact]
    public void A_run_held_for_its_account_gives_its_slot_back_wherever_it_was_held_and_takes_it_again_on_resume()
    {
        Guid connectionId = DomainId.New();
        RunListItem primary = new() { Id = DomainId.New(), State = RunState.Running };
        RunListItem review = new() { Id = DomainId.New(), State = RunState.UnderReview };
        RunDetails details = new() { Id = review.Id, State = RunState.UnderReview };
        RunListItemProjection rows = new();
        RunDetailsProjection detailsProjection = new();

        rows.Apply(new FakeEvent<RunAccountHeld>(
            new RunAccountHeld(primary.Id, connectionId, "logged out", Now, PrimarySession: true)), primary);
        rows.Apply(new FakeEvent<RunAccountHeld>(new RunAccountHeld(review.Id, connectionId, "logged out", Now)), review);
        detailsProjection.Apply(new FakeEvent<RunAccountHeld>(new RunAccountHeld(review.Id, connectionId, "logged out", Now)), details);

        primary.State.Should().Be(RunState.LaunchHeld, "no process runs while the account stays refused");
        review.State.Should().Be(RunState.LaunchHeld, "a run held at its review holds no slot either");

        RunAccountHoldResumed resumed = new(review.Id, connectionId, RunState.UnderReview, Now);
        rows.Apply(new FakeEvent<RunAccountHoldResumed>(resumed), review);
        detailsProjection.Apply(new FakeEvent<RunAccountHoldResumed>(resumed), details);

        review.State.Should().Be(RunState.UnderReview, "the resumed review counts against the ceiling from its start");
        details.State.Should().Be(RunState.UnderReview, "the account monitor's own ceiling count reads this row");
        details.AccountHeldConnectionId.Should().BeNull("the run is no longer held, so no later sweep resumes it again");
        details.AccountHeldFromState.Should().BeNull();
        details.ParkedReason.Should().BeNull();
    }
}
