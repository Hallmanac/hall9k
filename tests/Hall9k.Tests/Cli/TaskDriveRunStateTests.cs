using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The states <c>h9k task drive</c> claims or re-enters at, and <c>h9k task register-session</c>
/// registers at. Pure rules, so no store and no worktree are involved.
/// </summary>
public sealed class TaskDriveRunStateTests
{
    private static readonly Guid TaskId = Guid.Parse("01a18010-1cf3-7210-a7df-387dbd533657");
    private static readonly Guid RunId = Guid.Parse("01a18010-aaaa-7210-a7df-387dbd533657");

    [Theory]
    [InlineData("Dispatched")]
    [InlineData("Running")]
    [InlineData("Verifying")]
    [InlineData("UnderReview")]
    [InlineData("ReviewParked")]
    public void A_run_at_any_stage_before_the_pull_request_opens_is_admitted(string state)
    {
        TaskDriveCommand.RunStateRefusal(state, TaskId, RunId).Should().BeNull();
    }

    [Theory]
    [InlineData("AwaitingReview")]
    [InlineData("ChecksFailing")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Superseded")]
    public void A_run_past_the_review_loop_is_refused_with_the_way_to_see_where_it_stands(string state)
    {
        string? refusal = TaskDriveCommand.RunStateRefusal(state, TaskId, RunId);

        refusal.Should().Contain($"is already {state}").And.Contain($"h9k task show {TaskId}");
    }

    [Theory]
    [InlineData("Verifying")]
    [InlineData("UnderReview")]
    [InlineData("ReviewParked")]
    public void The_build_prompt_is_refused_once_the_pipeline_owns_the_worktree(string state)
    {
        TaskDriveCommand.PastBuildPromptRefusal(state, TaskId).Should().Contain($"h9k task drive {TaskId}").And.Contain("neither flag");
    }

    [Theory]
    [InlineData("Dispatched")]
    [InlineData("Running")]
    public void The_build_prompt_is_handed_over_while_the_build_is_still_the_work(string state)
    {
        TaskDriveCommand.PastBuildPromptRefusal(state, TaskId).Should().BeNull();
    }

    [Fact]
    public void A_run_that_moves_on_while_the_launch_is_prepared_says_so()
    {
        string? refusal = TaskDriveCommand.RunStateRefusal(RunState.Completed, TaskId, RunId, "while this session was preparing to launch");

        refusal.Should().Contain("moved out of those stages while this session was preparing to launch");
    }

    [Fact]
    public void A_done_task_is_refused_because_iterating_on_an_open_pull_request_is_not_supported_yet()
    {
        string advice = TaskDriveCommand.UnclaimableStateAdvice(TaskState.Done, TaskId);

        advice.Should().Contain("Iterating on an open pull request with h9k task drive is not supported yet");
    }

    [Fact]
    public void The_other_unclaimable_states_keep_their_advice()
    {
        TaskDriveCommand.UnclaimableStateAdvice(TaskState.Draft, TaskId).Should().Contain($"h9k task publish {TaskId}");
        TaskDriveCommand.UnclaimableStateAdvice(TaskState.Claimed, TaskId).Should().Contain("headless work already");
        TaskDriveCommand.UnclaimableStateAdvice(TaskState.Failed, TaskId).Should().Contain("moved past dispatch");
    }
}
