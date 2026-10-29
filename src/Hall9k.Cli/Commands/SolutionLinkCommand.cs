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
/// Brings projects into an existing solution, rewriting the membership on every member in one
/// command so each already-held member and each new one carries the same record.
/// </summary>
public sealed class SolutionLinkCommand : Hall9kAsyncCommand<SolutionLinkCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SOLUTION>")]
        [Description("The solution: its name, or a prefix of its key (h9k solution list shows them)")]
        public string Solution { get; init; } = string.Empty;

        [CommandOption("--project <PROJECT>")]
        [Description(
            "A project to bring in: its name, an unambiguous fragment of it, or its id. Repeatable. It needs "
            + "a recorded project key and repository URL, and must not be in another open solution (h9k solution unlink takes it out)")]
        public string[] Project { get; init; } = [];

        [CommandOption("--yes")]
        [Description(
            "Answer the warning that a new member's repository owner or organization differs from every "
            + "existing member's. Required in a session that cannot prompt")]
        public bool Yes { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Project.Length == 0)
        {
            throw new DomainValidationException("Pass --project <project> once for each project to bring into the solution (repeatable).");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        SolutionContext context = await SolutionContext.LoadAsync(session, cancellationToken);
        SolutionView view = SolutionRegistry.Match(context.Solutions, settings.Solution);

        List<ProjectDetails> added = [];
        SolutionMembership membership = view.Membership;
        List<SolutionMember> newMembers = [];
        foreach (string reference in settings.Project)
        {
            ProjectDetails project = await ProjectResolver.ResolveAsync(session, reference, cancellationToken);
            SolutionMember member = SolutionDecider.MemberOf(project);
            membership = SolutionDecider.Link(membership, member);
            added.Add(project);
            newMembers.Add(member);
        }

        SolutionDecider.RequireEveryMemberHeld(membership, context.Projects, context.RootFingerprintOf, "link a project into");
        SolutionDecider.RequireConsistent(view, context.Projects, context.RootFingerprintOf, "link a project into");

        if (!SolutionOwnerConfirmation.Confirm(
                SolutionDecider.OwnerMismatches(view.Membership.Members, newMembers),
                membership.Name, settings.Yes, new ConsoleInteractiveConfirmation()))
        {
            AnsiConsole.MarkupLine("[dim]Nothing written.[/]");
            return ExitCodes.Ok;
        }

        await SolutionWriter.WriteAsync(session, SolutionWriter.Rewrite(membership, context, removed: []), cancellationToken);

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Linked {string.Join(", ", newMembers.Select(member => member.Name))} into '{membership.Name}'.[/] It now has {membership.Members.Count} members.");
        return ExitCodes.Ok;
    }
}
