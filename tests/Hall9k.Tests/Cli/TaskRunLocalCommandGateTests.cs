using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="TaskRunLocalCommand.ResolveApproval"/> is the pure half of <c>h9k task run-local</c>'s
/// step-approval gate (security review idea 6be68ee2, process-injection finding 3): interactive
/// versus non-interactive, and what each accepts as a yes, seamed on
/// <see cref="Hall9k.Cli.Infrastructure.IInteractiveConfirmation"/> so every combination is
/// exercised here with no session and no real terminal — the store-bound half (printing, appending
/// <c>ProjectRunSkillStepsApproved</c>) is <c>RequiresDocker</c> integration coverage instead.
/// </summary>
public sealed class TaskRunLocalCommandGateTests
{
    private static readonly RunSkillStep Step = new(1, RunSkillStepKind.Command, "Launch", "start it", "npm start");
    private static readonly string Fingerprint = LocalLaunchStepApproval.Fingerprint([Step]);

    [Fact]
    public void An_interactive_yes_approves()
    {
        FakeInteractiveConfirmation confirmation = new(isInteractive: true, confirmResult: true);

        TaskRunLocalCommand.ResolveApproval(confirmation, approveFlag: null, Fingerprint)
            .Should().Be(TaskRunLocalCommand.StepApprovalDecision.Approve);
    }

    [Fact]
    public void An_interactive_no_declines()
    {
        FakeInteractiveConfirmation confirmation = new(isInteractive: true, confirmResult: false);

        TaskRunLocalCommand.ResolveApproval(confirmation, approveFlag: null, Fingerprint)
            .Should().Be(TaskRunLocalCommand.StepApprovalDecision.Decline);
    }

    [Fact]
    public void Non_interactive_with_a_matching_approve_approves()
    {
        FakeInteractiveConfirmation confirmation = new(isInteractive: false, confirmResult: true);

        TaskRunLocalCommand.ResolveApproval(confirmation, LocalLaunchStepApproval.ShortFingerprint(Fingerprint), Fingerprint)
            .Should().Be(TaskRunLocalCommand.StepApprovalDecision.Approve);
    }

    [Fact]
    public void Non_interactive_with_a_stale_approve_refuses()
    {
        string staleFingerprint = LocalLaunchStepApproval.Fingerprint(
            [new RunSkillStep(1, RunSkillStepKind.Command, "Launch", "start it", "npm run dev")]);
        FakeInteractiveConfirmation confirmation = new(isInteractive: false, confirmResult: true);

        TaskRunLocalCommand.ResolveApproval(confirmation, staleFingerprint, Fingerprint)
            .Should().Be(TaskRunLocalCommand.StepApprovalDecision.Refuse);
    }

    [Fact]
    public void Non_interactive_with_no_approve_refuses()
    {
        FakeInteractiveConfirmation confirmation = new(isInteractive: false, confirmResult: true);

        TaskRunLocalCommand.ResolveApproval(confirmation, approveFlag: null, Fingerprint)
            .Should().Be(TaskRunLocalCommand.StepApprovalDecision.Refuse,
                "there is no --yes: a bare skip-the-prompt flag cannot bind approval to the exact steps shown");
    }

    [Fact]
    public void A_gated_headless_run_that_is_live_is_working_in_the_worktree()
    {
        TaskRunLocalCommand.GatedRunIsWorking(humanGate: true, interactiveClaim: false, RunState.Running)
            .Should().BeTrue();
        TaskRunLocalCommand.GatedRunIsWorking(humanGate: true, interactiveClaim: false, RunState.UnderReview)
            .Should().BeTrue();
        TaskRunLocalCommand.GatedRunIsWorking(humanGate: true, interactiveClaim: false, RunState.ReviewParked)
            .Should().BeFalse("the park at the gate is where the worktree is the person's");
    }

    [Fact]
    public void The_owners_own_build_on_a_gated_task_may_run_the_product_but_the_pipeline_after_delivery_may_not()
    {
        TaskRunLocalCommand.GatedRunIsWorking(humanGate: true, interactiveClaim: true, RunState.Running)
            .Should().BeFalse("no platform session is in the worktree during the owner's own build");
        TaskRunLocalCommand.GatedRunIsWorking(humanGate: true, interactiveClaim: true, RunState.Dispatched)
            .Should().BeFalse();
        TaskRunLocalCommand.GatedRunIsWorking(humanGate: true, interactiveClaim: true, RunState.UnderReview)
            .Should().BeTrue("the gates and review sessions own the worktree once the build is delivered");
        TaskRunLocalCommand.GatedRunIsWorking(humanGate: false, interactiveClaim: false, RunState.Running)
            .Should().BeFalse();
    }
}
