using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Where the idea turned out to belong. Capture rarely knows, and an idea that has been
/// sitting unassigned is not incomplete — it is honest.
/// </summary>
public sealed class IdeaAssignCommand : Hall9kAsyncCommand<IdeaAssignCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Idea id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--project <PROJECT>")]
        [Description(
            "The project this idea belongs to: its name, an unambiguous fragment of it, or its id "
            + "(h9k project list shows them all). Sets the project when capture did not know it, and "
            + "changes it when discovery says otherwise")]
        public string? Project { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Project.IsBlank())
        {
            throw new DomainValidationException(
                $"Which project? h9k idea assign {settings.Id} --project <name>. "
                + "If the idea has no home yet, leaving it unassigned is a legitimate answer — "
                + "promotion is where a project becomes required.");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        IdeaAggregate idea = await IdeaIdResolver.LoadAsync(session, settings.Id, cancellationToken);
        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        if (project.PurgeAt is { } purgeDeadline)
        {
            // Same reasoning as h9k task/idea/epic add and h9k idea promote: the sweep re-queries
            // ownership by ProjectId at fire time, so an idea assigned here after scheduling is
            // destroyed along with everything else rather than orphaned — and unlike those, this
            // idea already has its own history, so the loss is not even a freshly-created draft
            // (independent pre-PR review, cycle 3, both lenses, high).
            throw new DomainValidationException(
                $"Project '{project.Name}' is scheduled for permanent deletion at "
                + $"{purgeDeadline.ToLocalTime():g}, so it cannot take this idea. Cancel the purge "
                + $"first: h9k project cancel-purge {project.Name}");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        IdeaAssignedToProject assigned = IdeaDecider.AssignToProject(
            idea, project.Id, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(idea.Id, assigned);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(idea.Id);
        AnsiConsole.MarkupLine(
            $"[blue]Idea {shortId}[/] belongs to '{project.Name.EscapeMarkup()}'"
            + (assigned.PreviousProjectId is null ? string.Empty : " [dim](moved)[/]"));
        AnsiConsole.MarkupLine(
            $"[dim]Cutting a task no longer needs to be told where it goes:[/] "
            + $"h9k task add --from-idea {shortId} --objective \"…\"");
        return ExitCodes.Ok;
    }
}
