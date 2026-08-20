using System.ComponentModel;
using System.Reflection;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The --help tree is how agents discover what h9k can do (AGENTS.md, CLI command standards),
/// so an option that carries no description — or one that describes a behaviour the command no
/// longer has — is a defect. Origin incident (2026-08-20): the pre-PR review of the noun-first
/// CLI branch found --max-parallel with no description at all, and --project on task add /
/// project set still promising "name or id" after both moved to <see cref="ProjectResolver"/>,
/// which also accepts an unambiguous fragment and fails on an ambiguous one.
/// </summary>
public sealed class CommandHelpTests
{
    public static TheoryData<Type, string> ProjectSelectingSettings() => new()
    {
        { typeof(TaskAddCommand.Settings), nameof(TaskAddCommand.Settings.Project) },
        { typeof(TaskListCommand.Settings), nameof(TaskListCommand.Settings.Project) },
        { typeof(ProjectSetCommand.Settings), nameof(ProjectSetCommand.Settings.Project) },
        { typeof(ProjectShowCommand.Settings), nameof(ProjectShowCommand.Settings.Project) },
    };

    [Fact]
    public void Every_option_and_argument_carries_a_description()
    {
        string[] undescribed =
        [
            .. from settings in typeof(ProjectResolver).Assembly.GetTypes()
               where settings.IsAssignableTo(typeof(CommandSettings))
               from property in settings.GetProperties(BindingFlags.Public | BindingFlags.Instance)
               where property.GetCustomAttribute<CommandOptionAttribute>() is not null
                   || property.GetCustomAttribute<CommandArgumentAttribute>() is not null
               where property.GetCustomAttribute<DescriptionAttribute>() is null
               select $"{settings.DeclaringType?.Name ?? settings.Name}.{property.Name}",
        ];

        undescribed.Should().BeEmpty("every option and argument teaches from --help");
    }

    [Theory]
    [MemberData(nameof(ProjectSelectingSettings))]
    public void Every_project_selector_describes_the_resolver_it_actually_uses(Type settings, string property)
    {
        // All four resolve through ProjectResolver, so all four must say so the same way:
        // a fragment names the project, and only an unambiguous one does.
        string description = settings.GetProperty(property)!.GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().Contain("unambiguous fragment");
    }
}
