using System.Text;
using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The anchor a human's own session is handed when it drives a task. Everything the card names is
/// asserted by the sentence that carries it, because a session reads these sentences as rules.
/// </summary>
public sealed class TaskDriveAnchorBuilderTests
{
    private static readonly Guid TaskId = Guid.Parse("01a18010-1cf3-7210-a7df-387dbd533657");
    private const string RecipeFile = "/home/projects/web/recipes/task-drive.md";
    private const string DraftFile = "/runs/r1/pr-summary-draft.md";

    [Fact]
    public void The_anchor_tells_the_session_to_register_first_and_read_the_record_before_every_move()
    {
        string anchor = Build();

        anchor.Should().Contain($"Your first action is `h9k task register-session {TaskId}`");
        anchor.Should().Contain($"Run `h9k task show {TaskId}` before you do anything that changes the task");
    }

    [Fact]
    public void The_anchor_forbids_advancing_a_phase_without_the_owners_word()
    {
        Build().Should().Contain("Never advance a phase without the owner's word");
    }

    [Fact]
    public void The_anchor_maps_each_instruction_to_its_verb()
    {
        string anchor = Build();

        anchor.Should().Contain("**\"Do the build\"** means build it here in this worktree");
        anchor.Should().Contain($"run `h9k task delegate {TaskId} --note");
        anchor.Should().Contain("and do not build it yourself");
        anchor.Should().Contain($"`h9k task deliver {TaskId}");
        anchor.Should().Contain($"`h9k review proceed {TaskId}`");
        anchor.Should().Contain($"`h9k review fixed {TaskId}`");
    }

    [Fact]
    public void A_delegated_contractor_replacing_the_sessions_record_is_said_to_need_a_new_registration()
    {
        Build().Should().Contain($"`h9k task register-session {TaskId}` again before you do anything else");
    }

    [Fact]
    public void Delivering_is_said_to_change_the_sessions_job_from_builder_to_driver()
    {
        string anchor = Build();

        anchor.Should().Contain("you stop being the builder and become the driver");
        anchor.Should().Contain("Do not tell the owner you are done");
    }

    [Fact]
    public void The_anchor_says_when_the_worktree_may_be_touched_and_where_the_owners_commits_stay()
    {
        string anchor = Build();

        anchor.Should().Contain("edit the worktree only at the park that follows a review verdict, and only on\n  the owner's word");
        anchor.Should().Contain("leave the worktree alone until the contractor reports it is\n  done");
        anchor.Should().Contain("The owner's own commits stay in this worktree on this node");
    }

    [Fact]
    public void The_anchor_names_the_worktree_the_branch_and_the_summary_draft()
    {
        string anchor = Build();

        anchor.Should().Contain("Its worktree is `/wt/task` on branch `task/abc12345-thing`");
        anchor.Should().Contain($"write the result to `{DraftFile}`");
        anchor.Should().Contain("`pr-summary` skill");
        anchor.Should().Contain("with `--pr-summary` when you deliver");
    }

    [Fact]
    public void The_anchor_carries_the_projects_writing_conventions_as_a_headless_prompt_pastes_them()
    {
        WritingConventions conventions = WritingConventions.Parse("Prefer short sentences. Never exclaim.");

        string anchor = Build(conventions);

        StringBuilder expected = new();
        WorkPromptBuilder.AppendWritingConventions(
            expected, string.Empty, conventions, PromptTemplates.Load("task-drive-anchor/anchor.md", "writing-conventions-lead"));
        anchor.Should().Contain(expected.ToString().Replace("\r\n", "\n"));
        anchor.Should().Contain("  > Prefer short sentences. Never exclaim.");
        anchor.Should().Contain(WritingConventions.PlatformRule);
    }

    [Fact]
    public void Without_a_task_drive_recipe_the_anchor_says_nothing_about_one()
    {
        string anchor = Build(recipeFile: null);

        anchor.Should().NotContain("recipe");
    }

    [Fact]
    public void With_a_task_drive_recipe_the_anchor_tells_the_session_to_read_and_follow_it()
    {
        string anchor = Build(recipeFile: RecipeFile);

        anchor.Should().Contain($"`{RecipeFile}`");
        anchor.Should().Contain("Read it now and\nfollow it");
    }

    [Fact]
    public void The_anchor_is_lf_terminated_and_free_of_unfilled_placeholders()
    {
        string anchor = Build(recipeFile: RecipeFile);

        anchor.Should().NotContain("\r");
        anchor.Should().NotContain("{{");
    }

    private static string Build(WritingConventions? conventions = null, string? recipeFile = null) =>
        TaskDriveAnchorBuilder.Build(
            TaskId, "/wt/task", "task/abc12345-thing", DraftFile, conventions ?? WritingConventions.Default, recipeFile);
}
