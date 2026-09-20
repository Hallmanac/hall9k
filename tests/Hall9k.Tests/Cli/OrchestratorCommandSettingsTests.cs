using FluentAssertions;
using Hall9k.Cli.Commands;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The one thing Spectre's own binding cannot say for these two commands (idea 89471598,
/// piece 1): <c>--project</c> is required. Left blank it reaches <c>ProjectResolver</c> as a
/// fragment that matches every project, which on a single-project install silently registers or
/// deregisters that one, and on a multi-project install reports an ambiguity rather than the
/// missing option — either way, a mistyped flag never looks like the mistake it is.
/// </summary>
public sealed class OrchestratorCommandSettingsTests
{
    [Fact]
    public void Register_refuses_a_missing_project()
    {
        OrchestratorRegisterCommand.Settings settings = new() { Session = "hall9k-orchestrator", ProcessId = 48213 };

        settings.Validate().Successful.Should().BeFalse();
        settings.Validate().Message.Should().Contain("--project");
    }

    [Fact]
    public void Register_accepts_a_named_project()
    {
        OrchestratorRegisterCommand.Settings settings =
            new() { Project = "hall9k", Session = "hall9k-orchestrator", ProcessId = 48213 };

        settings.Validate().Successful.Should().BeTrue();
    }

    [Fact]
    public void Deregister_refuses_a_missing_project_the_same_way()
    {
        new OrchestratorDeregisterCommand.Settings { ProcessId = 48213 }.Validate().Successful.Should().BeFalse();
        new OrchestratorDeregisterCommand.Settings { Project = "hall9k", ProcessId = 48213 }
            .Validate().Successful.Should().BeTrue();
    }

    [Fact]
    public void Deregister_refuses_a_missing_pid_so_it_can_never_end_somebody_else_s_registration()
    {
        ValidationResult result = new OrchestratorDeregisterCommand.Settings { Project = "hall9k" }.Validate();

        result.Successful.Should().BeFalse(
            "without the leaving window's own pid the only thing this could end is whoever holds the "
            + "registration, which is the live replacement a stale window's close step must not touch");
        result.Message.Should().Contain("--pid");
    }
}
