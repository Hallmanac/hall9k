using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k config set</c>'s validation and its mutation of <see cref="OperatingSettings"/>,
/// exercised directly the way <c>ProjectSetCommandTests</c> exercises <c>ParseLink</c> — no
/// database, no filesystem — because both are the actual business logic and the Spectre plumbing
/// around them is not what backlog 59 is testing.
/// </summary>
public sealed class ConfigSetCommandTests
{
    [Fact]
    public void No_options_at_all_is_refused_with_a_teaching_message()
    {
        ConfigSetCommand.Settings settings = new();

        Action act = () => ConfigSetCommand.Validate(settings);

        act.Should().Throw<DomainValidationException>().WithMessage("*Nothing to change*");
    }

    [Fact]
    public void A_ceiling_of_zero_is_refused()
    {
        ConfigSetCommand.Settings settings = new() { MaxConcurrentAgentSessions = 0 };

        Action act = () => ConfigSetCommand.Validate(settings);

        act.Should().Throw<DomainValidationException>().WithMessage("*at least 1*");
    }

    [Fact]
    public void A_negative_ceiling_is_refused()
    {
        ConfigSetCommand.Settings settings = new() { MaxConcurrentAgentSessions = -3 };

        Action act = () => ConfigSetCommand.Validate(settings);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void A_positive_ceiling_alone_validates()
    {
        ConfigSetCommand.Settings settings = new() { MaxConcurrentAgentSessions = 4 };

        Action act = () => ConfigSetCommand.Validate(settings);

        act.Should().NotThrow();
    }

    [Fact]
    public void Applying_the_ceiling_sets_it_and_records_the_change()
    {
        ConfigSetCommand.Settings settings = new() { MaxConcurrentAgentSessions = 5 };
        OperatingSettings operating = new();
        List<string> changed = [];

        ConfigSetCommand.Apply(settings, operating, changed);

        operating.MaxConcurrentAgentSessions.Should().Be(5);
        changed.Should().ContainSingle().Which.Should().Contain("5");
    }

    [Fact]
    public void Applying_a_role_model_sets_only_that_role()
    {
        ConfigSetCommand.Settings settings = new() { ModelReview = "sonnet" };
        OperatingSettings operating = new();
        List<string> changed = [];

        ConfigSetCommand.Apply(settings, operating, changed);

        operating.ModelByRole.Review.Should().Be("sonnet");
        operating.ModelByRole.Build.Should().BeNull("only the role named on the command line changes");
    }

    [Fact]
    public void An_alias_is_canonicalized_the_same_way_every_other_model_option_does()
    {
        ConfigSetCommand.Settings settings = new() { ModelFix = "HAIKU" };
        OperatingSettings operating = new();
        List<string> changed = [];

        ConfigSetCommand.Apply(settings, operating, changed);

        operating.ModelByRole.Fix.Should().Be("haiku");
    }

    [Fact]
    public void The_word_default_clears_an_existing_role_override()
    {
        ConfigSetCommand.Settings settings = new() { ModelReview = "default" };
        OperatingSettings operating = new() { ModelByRole = new RoleModelSettings { Review = "sonnet" } };
        List<string> changed = [];

        ConfigSetCommand.Apply(settings, operating, changed);

        operating.ModelByRole.Review.Should().BeNull();
        changed.Should().ContainSingle().Which.Should().Contain("cleared");
    }

    [Fact]
    public void A_role_not_named_on_the_command_line_is_left_exactly_as_it_was()
    {
        ConfigSetCommand.Settings settings = new() { ModelReview = "sonnet" };
        OperatingSettings operating = new() { ModelByRole = new RoleModelSettings { Fix = "haiku" } };
        List<string> changed = [];

        ConfigSetCommand.Apply(settings, operating, changed);

        operating.ModelByRole.Fix.Should().Be("haiku", "an untouched role is not the same as a cleared one");
    }

    [Fact]
    public void A_not_well_formed_default_model_is_refused_the_same_way_project_set_refuses_it()
    {
        ConfigSetCommand.Settings settings = new() { DefaultModel = "claude-opus-5 (1m)" };
        OperatingSettings operating = new();
        List<string> changed = [];

        Action act = () => ConfigSetCommand.Apply(settings, operating, changed);

        act.Should().Throw<DomainValidationException>().WithMessage("*not a usable model name*");
        operating.DefaultModel.Should().BeNull("a refused value must never reach the config file");
    }

    [Fact]
    public void A_not_well_formed_role_model_is_refused()
    {
        ConfigSetCommand.Settings settings = new() { ModelReview = "claude-opus-5(1m)" };
        OperatingSettings operating = new();
        List<string> changed = [];

        Action act = () => ConfigSetCommand.Apply(settings, operating, changed);

        act.Should().Throw<DomainValidationException>().WithMessage("*not a usable model name*");
    }
}
