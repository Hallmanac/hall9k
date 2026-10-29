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
/// Takes projects out of a solution: the remaining members are rewritten without them and each
/// removed project's own membership is cleared, in one command. The way a project in an open
/// solution is freed to join another.
/// </summary>
public sealed class SolutionUnlinkCommand : Hall9kAsyncCommand<SolutionUnlinkCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SOLUTION>")]
        [Description("The solution: its name, or a prefix of its key (h9k solution list shows them)")]
        public string Solution { get; init; } = string.Empty;

        [CommandOption("--project <PROJECT>")]
        [Description(
            "A member project to take out: its name, an unambiguous fragment of it, or its id. Repeatable. "
            + "The last member cannot be unlinked; close the solution instead (h9k solution close)")]
        public string[] Project { get; init; } = [];
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Project.Length == 0)
        {
            throw new DomainValidationException("Pass --project <project> once for each member to take out of the solution (repeatable).");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        SolutionContext context = await SolutionContext.LoadAsync(session, cancellationToken);
        SolutionView view = SolutionRegistry.Match(context.Solutions, settings.Solution);

        SolutionMembership membership = view.Membership;
        List<ProjectDetails> removed = [];
        foreach (string reference in settings.Project)
        {
            ProjectDetails project = await ProjectResolver.ResolveAsync(session, reference, cancellationToken);
            membership = SolutionDecider.Unlink(
                membership,
                project.ProjectKey ?? throw new DomainValidationException(
                    $"Project '{project.Name}' has no recorded project key, so it is not a member of '{view.Membership.Name}'."));
            removed.Add(project);
        }

        SolutionDecider.RequireEveryMemberHeld(view.Membership, context.Projects, context.RootFingerprintOf, "unlink a project from");
        SolutionDecider.RequireConsistent(view, context.Projects, context.RootFingerprintOf, "unlink a project from");

        await SolutionWriter.WriteAsync(session, SolutionWriter.Rewrite(membership, context, removed), cancellationToken);

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Unlinked {string.Join(", ", removed.Select(project => project.Name))} from '{membership.Name}'.[/] It now has {membership.Members.Count} members.");
        return ExitCodes.Ok;
    }
}
