using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k project set --linear</c> decides and says: confirming the team through the connection
/// before recording it, setting the branch convention Linear needs only the first time and only when
/// nothing of the operator's own is in the way, and showing the binding wherever the Jira board is
/// shown. Responses are fixtures written from Linear's public schema.
/// </summary>
public sealed class ProjectSetLinearTests : IDisposable
{
    private const string KeyFileName = "linear-set-test-key";
    private static readonly Uri Workspace = new("https://linear.app/hall9k");
    private static readonly LinearTeamKey Eng = LinearTeamKey.Parse("ENG");

    private readonly ScopedTestHome _home = new();
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(1));

    private CancellationToken Token => _cancellation.Token;

    public ProjectSetLinearTests()
    {
        Directory.CreateDirectory(CredentialVault.Directory);
        File.WriteAllText(CredentialVault.FileFor(KeyFileName), "lin_api_set_test_key");
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        _home.Dispose();
    }

    private static LinearWorkItemProvider Provider(string body) =>
        new(
            LinearAccount.Registered(Workspace, "Brian Hall", CredentialReference.File(KeyFileName)),
            (_, _) => Task.FromResult(new LinearResponse(200, body)));

    private static ProjectDetails Project(
        string name = "hall9k", LinearTeamKey? team = null, BranchNameTemplate? template = null) =>
        new()
        {
            Id = DomainId.New(),
            Name = name,
            LinearTeam = team ?? LinearTeamKey.None,
            BranchNameTemplate = template ?? BranchNameTemplate.Default,
        };

    [Fact]
    public async Task A_team_the_connection_can_see_is_confirmed_with_its_name()
    {
        LinearWorkItemProvider provider = Provider(
            JsonSerializer.Serialize(new { data = new { teams = new { nodes = new[] { new { id = "t1", key = "ENG", name = "Engineering" } } } } }));

        LinearTeam team = await ProjectSetCommand.ConfirmLinearTeamAsync(provider, Eng, Token);

        team.Name.Should().Be("Engineering");
    }

    [Fact]
    public async Task A_team_nobody_can_see_is_refused_saying_it_may_not_exist_or_the_key_may_be_limited()
    {
        LinearWorkItemProvider provider = Provider("""{"data":{"teams":{"nodes":[]}}}""");

        Func<Task> confirm = () => ProjectSetCommand.ConfirmLinearTeamAsync(provider, LinearTeamKey.Parse("NOPE"), Token);

        DomainNotFoundException refusal = (await confirm.Should().ThrowAsync<DomainNotFoundException>()).Which;
        refusal.Message.Should().Contain("no team with the key NOPE").And.Contain("may not exist")
            .And.Contain("limited to other teams").And.Contain("nothing was bound");
    }

    [Fact]
    public void The_first_binding_on_a_project_still_on_the_default_template_sets_the_Linear_convention() =>
        ProjectSetCommand.SetsLinearConvention(LinearTeamKey.None, Eng, null, BranchNameTemplate.Default)
            .Should().BeTrue();

    [Fact]
    public void A_template_passed_in_the_same_command_is_the_operators_and_is_left_alone() =>
        ProjectSetCommand.SetsLinearConvention(LinearTeamKey.None, Eng, "{shortid}-{slug}", BranchNameTemplate.Default)
            .Should().BeFalse();

    [Fact]
    public void Even_a_none_passed_in_the_same_command_is_the_operators_choice() =>
        ProjectSetCommand.SetsLinearConvention(LinearTeamKey.None, Eng, "none", BranchNameTemplate.Default)
            .Should().BeFalse();

    [Fact]
    public void Changing_the_team_on_an_already_bound_project_leaves_the_template_alone_so_the_undo_stays_undone() =>
        ProjectSetCommand.SetsLinearConvention(Eng, LinearTeamKey.Parse("PLAT"), null, BranchNameTemplate.Default)
            .Should().BeFalse();

    [Fact]
    public void A_project_with_its_own_template_keeps_it() =>
        ProjectSetCommand.SetsLinearConvention(LinearTeamKey.None, Eng, null, BranchNameTemplate.Parse("feature/{slug}"))
            .Should().BeFalse();

    [Fact]
    public void Clearing_the_binding_never_sets_the_convention() =>
        ProjectSetCommand.SetsLinearConvention(Eng, LinearTeamKey.None, null, BranchNameTemplate.Default)
            .Should().BeFalse();

    [Fact]
    public void Setting_the_convention_says_so_and_names_branch_template_none_as_the_undo()
    {
        IReadOnlyList<string> notes = ProjectSetCommand.LinearBindingNotes(
            "hall9k", LinearTeamKey.None, Eng, new LinearTeam("t1", "ENG", "Engineering"),
            setsConvention: true, clearedTeam: false, BranchNameTemplate.Default);

        string said = string.Join("\n", notes);
        said.Should().Contain("{key}-{slug}").And.Contain("it was the default")
            .And.Contain("h9k project set hall9k --branch-template none").And.Contain("one tracker");
    }

    [Fact]
    public void Binding_with_the_template_left_alone_does_not_claim_to_have_set_one()
    {
        IReadOnlyList<string> notes = ProjectSetCommand.LinearBindingNotes(
            "hall9k", Eng, Eng, new LinearTeam("t1", "ENG", "Engineering"),
            setsConvention: false, clearedTeam: false, BranchNameTemplate.Default);

        string.Join("\n", notes).Should().NotContain("Branch template set");
    }

    [Fact]
    public void Clearing_the_binding_leaves_the_template_alone_and_says_so()
    {
        IReadOnlyList<string> notes = ProjectSetCommand.LinearBindingNotes(
            "hall9k", Eng, LinearTeamKey.None, confirmedTeam: null,
            setsConvention: false, clearedTeam: true, BranchNameTemplate.LinearConvention);

        string said = string.Join("\n", notes);
        said.Should().Contain("The branch template is unchanged").And.Contain("{key}-{slug}")
            .And.Contain("--branch-template none").And.Contain("keep their provider");
    }

    [Fact]
    public void A_project_template_without_key_gets_a_warning_naming_the_command_that_fixes_it()
    {
        string? warning = ProjectSetCommand.LinearTemplateWarning(
            Project(team: Eng, template: BranchNameTemplate.Parse("feature/{slug}")));

        warning.Should().NotBeNull();
        warning.Should().Contain("has no {key}").And.Contain("h9k project set hall9k --branch-template \"{key}-{slug}\"");
    }

    [Fact]
    public void A_template_with_key_or_the_default_or_no_team_earns_no_warning()
    {
        ProjectSetCommand.LinearTemplateWarning(Project(team: Eng, template: BranchNameTemplate.Parse("{key}/{slug}")))
            .Should().BeNull();
        ProjectSetCommand.LinearTemplateWarning(Project(team: Eng)).Should().BeNull("an operator who chose none made that choice on purpose");
        ProjectSetCommand.LinearTemplateWarning(Project(template: BranchNameTemplate.Parse("feature/{slug}")))
            .Should().BeNull("a project not on Linear has no reason to carry an identifier");
    }

    [Fact]
    public void The_pane_and_the_home_briefing_show_the_binding_and_its_absence()
    {
        ProjectShowCommand.LinearTeamRow(Project(team: Eng)).Should().Contain("ENG").And.Contain("one tracker");
        ProjectShowCommand.LinearTeamRow(Project()).Should().Contain("none bound").And.Contain("--linear ENG");

        string home = ProjectHomePaths.DefaultFor("hall9k");
        ProjectAgentsDocument.Render(home, Project(team: Eng)).Should()
            .Contain("Linear team: `ENG`").And.Contain("h9k task add --from-linear");
        ProjectAgentsDocument.Render(home, Project()).Should().Contain("Linear team: none bound");
    }
}
