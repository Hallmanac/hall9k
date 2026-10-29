using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Groups one-repo projects into a solution. The ladder is solution, project, epic, task: a
/// solution is a set of projects (each one repository), and it exists only as a membership team
/// setting written onto every member, so this one command mints the key and writes the membership
/// into each named project's team settings together, with no stream of its own.
/// </summary>
public sealed class SolutionAddCommand : Hall9kAsyncCommand<SolutionAddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("The solution's name. Follows the rules a project name follows: unique on this node, case-insensitive")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--project <PROJECT>")]
        [Description(
            "A member project: its name, an unambiguous fragment of it, or its id. Repeatable. A project "
            + "needs a recorded project key (h9k project join, or h9k project assign-key for the genesis "
            + "owner) and a recorded repository URL, and belongs to at most one open solution")]
        public string[] Project { get; init; } = [];

        [CommandOption("--yes")]
        [Description(
            "Answer the warning that a member's repository owner or organization differs from every other "
            + "member's. Required in a session that cannot prompt")]
        public bool Yes { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Project.Length == 0)
        {
            throw new DomainValidationException(
                "A solution groups projects: pass --project <project> once for each member (repeatable). "
                + "The ladder is solution, project, epic, task; see them with h9k project list.");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        SolutionContext context = await SolutionContext.LoadAsync(session, cancellationToken);
        SolutionRegistry.RequireUniqueName(context.Solutions, settings.Name, ownKey: null);

        List<ProjectDetails> projects = [];
        foreach (string reference in settings.Project)
        {
            ProjectDetails project = await ProjectResolver.ResolveAsync(session, reference, cancellationToken);
            if (projects.Any(chosen => chosen.Id == project.Id))
            {
                throw new DomainValidationException($"Project '{project.Name}' is named twice; a project appears in a solution once.");
            }

            projects.Add(project);
        }

        SolutionMember[] members = [.. projects.Select(SolutionDecider.MemberOf)];
        SolutionMembership membership = SolutionDecider.Create(SolutionDecider.NewKey(), settings.Name, members);

        if (!SolutionOwnerConfirmation.Confirm(
                SolutionDecider.OwnerMismatches([], members), membership.Name, settings.Yes, new ConsoleInteractiveConfirmation()))
        {
            AnsiConsole.MarkupLine("[dim]Nothing written.[/]");
            return ExitCodes.Ok;
        }

        await SolutionWriter.WriteAsync(
            session, [.. projects.Select(project => new SolutionWrite(project, membership))], cancellationToken);

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Solution '{membership.Name}' created[/] [dim]({membership.Key})[/] over {string.Join(", ", members.Select(member => member.Name))}.");
        AnsiConsole.MarkupLineInterpolated($"[dim]See its board: h9k solution show \"{membership.Name}\"[/]");
        return ExitCodes.Ok;
    }
}
