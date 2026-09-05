using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="TaskDelegateCommand.Validate"/> is the cheap, DB-free half of h9k task delegate
/// (task 15f889e3-h9k, design ruling R6): a contractor dispatched with no handoff note at all is
/// the failure mode --note exists to prevent, checked before the store ever opens (the
/// <c>h9k task log-interaction</c> convention). The store round trip, the state refusals, and the
/// worktree read behind ResumesPreviousWork are <see cref="TaskDelegateCommand.PrepareAsync"/>'s
/// own integration-tier concern.
/// </summary>
public sealed class TaskDelegateCommandTests
{
    private static TaskDelegateCommand.Settings Settings(string note = "Drafted the migration.", bool force = false) =>
        new()
        {
            Id = "28b19893",
            Note = note,
            Force = force,
        };

    [Fact]
    public void Refuses_a_blank_note()
    {
        Action act = () => TaskDelegateCommand.Validate(Settings(note: ""));

        act.Should().Throw<DomainValidationException>().WithMessage("*--note*");
    }

    [Fact]
    public void Refuses_a_whitespace_only_note()
    {
        Action act = () => TaskDelegateCommand.Validate(Settings(note: "   "));

        act.Should().Throw<DomainValidationException>().WithMessage("*--note*");
    }

    [Fact]
    public void Validation_passes_a_well_formed_note()
    {
        Action act = () => TaskDelegateCommand.Validate(Settings());

        act.Should().NotThrow();
    }
}
