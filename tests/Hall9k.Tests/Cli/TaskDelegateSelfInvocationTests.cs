using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k task delegate</c> run by the claim's own registered session, which is the session
/// <c>h9k task drive</c> starts and tells to delegate the build on the owner's word, and the
/// milestones the contractor it dispatches sends back to that session.
/// </summary>
[Collection("Environment")]
public sealed class TaskDelegateSelfInvocationTests
{
    private static readonly Guid TaskId = Guid.Parse("01a18010-1cf3-7210-a7df-387dbd533657");

    [Fact]
    public void The_claims_own_registered_session_may_delegate()
    {
        using Process current = Process.GetCurrentProcess();
        RunDetails run = RunWithSessionAt(current.Id, InteractiveSessionLiveness.ReadStartedAt(current));
        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.InteractiveRunEnvironmentVariable, null),
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, current.Id.ToString()));

        Action delegating = () => TaskDelegateCommand.EnsureNoOtherSessionAttached(run, TaskId, force: false);

        delegating.Should().NotThrow("the recorded live session is the caller itself, waiting on this command");
    }

    [Fact]
    public void A_second_live_session_still_refuses()
    {
        using Process current = Process.GetCurrentProcess();
        RunDetails run = RunWithSessionAt(current.Id, InteractiveSessionLiveness.ReadStartedAt(current));
        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.InteractiveRunEnvironmentVariable, null),
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, null));

        Action delegating = () => TaskDelegateCommand.EnsureNoOtherSessionAttached(run, TaskId, force: false);

        delegating.Should().Throw<DomainConflictException>().WithMessage("*still attached*delegate*");
    }

    [Fact]
    public void A_session_that_has_exited_does_not_block_a_delegate()
    {
        RunDetails run = RunWithSessionAt(processId: int.MaxValue - 1, startedAt: DateTimeOffset.UtcNow.AddHours(-1));
        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.InteractiveRunEnvironmentVariable, null),
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, null));

        Action delegating = () => TaskDelegateCommand.EnsureNoOtherSessionAttached(run, TaskId, force: false);

        delegating.Should().NotThrow();
    }

    [Fact]
    public void The_contractor_prompt_carries_the_registered_sessions_address_and_its_milestones()
    {
        string prompt = ContractorPrompt(registeredSessionName: "brians-terminal");

        prompt.Should().Contain("Address: `brians-terminal`");
        prompt.Should().Contain("the human's own registered session");
        prompt.Should().NotContain("No registered human session is on record");
    }

    [Fact]
    public void The_contractor_prompt_names_no_address_when_nobody_ever_registered()
    {
        string prompt = ContractorPrompt(registeredSessionName: null);

        prompt.Should().Contain("No registered human session is on record for this run.");
        prompt.Should().NotContain("Address: `");
    }

    private static RunDetails RunWithSessionAt(int processId, DateTimeOffset startedAt) => new()
    {
        Id = DomainId.New(),
        SessionName = "abcd1234-" + SessionRoleName.InteractiveClaim,
        ActiveSessions = [new ActiveSession(AgentRole.Interactive, ReviewLens.Unknown, processId, startedAt, Environment.MachineName)],
    };

    private static string ContractorPrompt(string? registeredSessionName) =>
        WorkPromptBuilder.Build(
            new TaskDetails { Id = TaskId, Objective = "Do the thing", InteractiveModeEnabled = true },
            new ProjectDetails { Name = "web" }, branch: "task/abcd1234-thing", worktreePath: "/wt/task",
            isInteractive: false, isDeliberateHeadlessStart: true, requiresSelfRegistration: false,
            isDelegatedContractor: true, delegationNote: "Drafted the migration.",
            interactiveMilestoneAddress: registeredSessionName);
}
