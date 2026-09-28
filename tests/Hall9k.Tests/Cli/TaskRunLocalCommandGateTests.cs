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
}
