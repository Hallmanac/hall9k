using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k project show</c> and <c>h9k project set</c> say about the per-project run ceiling
/// (Decisions Log #140) — including the migration itself, which is named in the commands' own
/// output because the retired session-denominated value is not carried over: an operator who set
/// it learns from here that their project is uncapped until they say otherwise.
/// </summary>
public sealed class ProjectCapCommandSurfaceTests
{
    [Fact]
    public void An_uncapped_project_says_so_and_names_the_option_that_caps_it()
    {
        string row = ProjectShowCommand.MaxParallelTasksRow(Project(cap: null));

        row.Should().Contain("not capped");
        row.Should().Contain("other projects' activity leave free", "a ceiling reserves nothing");
        row.Should().Contain("h9k project set alpha --max-parallel-tasks 1");
    }

    [Fact]
    public void A_capped_project_states_that_review_sessions_do_not_count_separately()
    {
        // The one thing a reader would otherwise have to work out for themselves, and the
        // reason the setting is denominated in runs at all.
        string row = ProjectShowCommand.MaxParallelTasksRow(Project(cap: 2));

        row.Should().StartWith("2 ");
        row.Should().Contain("at most 2 of this project's task runs are live at once");
        row.Should().Contain("a ceiling, never a reservation");
        row.Should().Contain("review sessions do not count separately");
    }

    [Fact]
    public void A_paused_project_reads_as_paused_rather_than_as_a_zero()
    {
        string row = ProjectShowCommand.MaxParallelTasksRow(Project(cap: 0));

        row.Should().Contain("0 — paused");
        row.Should().Contain("held even while this node sits idle");
        row.Should().Contain("nothing raises the cap on its own");
        row.Should().Contain("h9k project set alpha --max-parallel-tasks <n>");
    }

    [Fact]
    public void A_project_carrying_the_retired_value_is_told_it_is_retired_rather_than_converted()
    {
        ProjectDetails project = Project(cap: null);
        project.MaxParallelAgents = 6;

        string row = ProjectShowCommand.RetiredMaxParallelAgentsRow(project)!;

        row.Should().Contain("6 — retired");
        row.Should().Contain("nothing ever enforced");
        row.Should().Contain("retired rather than converted");
        row.Should().Contain("h9k project set alpha --max-parallel-tasks <n>");
    }

    [Fact]
    public void The_retired_row_is_absent_once_the_runs_denominated_cap_is_set()
    {
        ProjectDetails project = Project(cap: 1);
        project.MaxParallelAgents = 6;

        ProjectShowCommand.RetiredMaxParallelAgentsRow(project).Should().BeNull(
            "the migration is done; repeating it would be noise on every future read");
    }

    [Fact]
    public void The_retired_row_is_absent_for_the_one_value_it_could_not_honestly_claim_was_set()
    {
        // A recorded 3 is indistinguishable from the old default of 3, and both retire to the
        // identical enforced behaviour — so there is nothing the notice could add (AGENTS.md:
        // never guess at unobserved facts).
        ProjectDetails untouched = Project(cap: null);

        untouched.MaxParallelAgents.Should().Be(ProjectAggregate.LegacyMaxParallelAgentsDefault);
        ProjectShowCommand.RetiredMaxParallelAgentsRow(untouched).Should().BeNull();
    }

    [Fact]
    public void Project_set_names_the_migration_in_its_own_output()
    {
        ProjectDetails project = Project(cap: null);
        project.MaxParallelAgents = 2;

        IReadOnlyList<string> notes = ProjectSetCommand.ParallelTasksNotes(new ProjectSetCommand.Settings(), project);

        notes.Should().ContainSingle().Which.Should().Contain("Migration:")
            .And.Contain("--max-parallel 2")
            .And.Contain("retired rather than converted")
            .And.Contain("h9k project set alpha --max-parallel-tasks <n>");
    }

    [Fact]
    public void Project_set_says_which_setting_the_quiet_alias_wrote()
    {
        // The alias kept the old name but changed denomination, so the one thing an operator
        // using muscle memory needs told is what it just counted.
        IReadOnlyList<string> notes = ProjectSetCommand.ParallelTasksNotes(
            new ProjectSetCommand.Settings { MaxParallelAlias = "2" }, Project(cap: 2));

        notes.Should().ContainSingle().Which.Should().Contain("TASK RUNS");
    }

    [Fact]
    public void Project_set_states_the_pause_it_just_agreed_to()
    {
        IReadOnlyList<string> notes = ProjectSetCommand.ParallelTasksNotes(
            new ProjectSetCommand.Settings { MaxParallelTasks = "0" }, Project(cap: 0));

        notes.Should().ContainSingle().Which.Should().Contain("is paused")
            .And.Contain("nothing raises it on its own")
            .And.Contain("Runs already live finish normally");
    }

    [Fact]
    public void Project_set_says_nothing_about_the_cap_when_this_change_had_nothing_to_do_with_it()
    {
        ProjectSetCommand.ParallelTasksNotes(new ProjectSetCommand.Settings(), Project(cap: 2))
            .Should().BeEmpty("an unrelated project set must not repeat a settled setting's story");

        // Including the pause: it is a standing state h9k project show and h9k status report, so
        // an unrelated setting change on a project paused a week ago does not re-announce it.
        ProjectSetCommand.ParallelTasksNotes(new ProjectSetCommand.Settings { Model = "opus" }, Project(cap: 0))
            .Should().BeEmpty();
    }

    private static ProjectDetails Project(int? cap) => new()
    {
        Id = DomainId.New(),
        Name = "alpha",
        MaxParallelTasks = cap,
    };
}
