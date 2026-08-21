using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Capture: one command, one argument (Decisions Log #35). Everything else an idea might
/// eventually need — a project, a shape, criteria — is discovery's job, and demanding any of
/// it here would turn a thirty-second transaction into a commitment to think.
/// </summary>
public sealed class IdeaAddCommand : Hall9kAsyncCommand<IdeaAddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TEXT>")]
        [Description(
            "The thought, in whatever words you had it in. This is the whole of capture: quote it "
            + "and you are done. Sharpen it later with h9k idea revise as discovery tells you what "
            + "it actually is")]
        public string Text { get; init; } = string.Empty;

        [CommandOption("--project <PROJECT>")]
        [Description(
            "Optional, and only when you already know: the project's name, an unambiguous fragment "
            + "of it, or its id. An idea may precede its project — or become one — so leaving this "
            + "off records an honest absence rather than a guess. Set it later with h9k idea assign")]
        public string? Project { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails? project = settings.Project.IsNotBlank()
            ? await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken)
            : null;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        Guid ideaId = DomainId.New();
        IdeaCaptured captured = IdeaDecider.Capture(
            ideaId, context.OwnerId, settings.Text, project?.Id, DateTimeOffset.UtcNow);
        session.Events.StartStream<IdeaAggregate>(ideaId, captured);
        await session.SaveChangesAsync(cancellationToken);

        // The workspace is made real the moment the idea exists: a directory you have to create
        // before you can use it is a directory nobody drops a file into. No doorbell — nothing
        // dispatches from an idea, by design.
        string workspace = IdeaPaths.EnsureWorkspace(ideaId);

        string shortId = TaskListCommand.ShortId(ideaId);
        AnsiConsole.MarkupLine(
            $"[blue]Idea captured[/] {TaskListCommand.Truncate(captured.Text, 72).EscapeMarkup()} [dim]({shortId})[/]");
        AnsiConsole.MarkupLine(project is null
            ? "[dim]  project:[/] none yet [dim]— set one when you know it:[/] "
              + $"h9k idea assign {shortId} --project <name>"
            : $"[dim]  project:[/] {project.Name.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"[dim]  workspace:[/] {workspace.EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            "[dim]Discovery is what happens next: research it, gather files into the workspace, "
            + "prototype. When it has intent, it is a task:[/]");
        AnsiConsole.MarkupLine(
            $"  h9k idea promote {shortId}" + (project is null ? " --project <name>" : string.Empty));
        return ExitCodes.Ok;
    }
}
