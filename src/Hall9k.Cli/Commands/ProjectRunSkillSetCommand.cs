using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Replaces this project's run skill by hand (idea b9b09779, piece 4). Appends only
/// <see cref="ProjectRunSkillRecorded"/> — the same event a discovery session's composed markdown
/// comes back through — and never touches the ledger itself: the daemon's own
/// <c>RunSkillSweepEngine</c> is the only thing that ever writes <c>run-skill.md</c>, so a
/// dispatched agent session running this command can correct a wrong run skill without ever
/// holding write access to the ledger.
/// <para>
/// The file is held to the identical contract a session's answer is (<c>ProjectDecider.RecordRunSkill</c>):
/// all six of <see cref="RunSkillDocument.Headings"/>, and under the length cap. A hand-written
/// skill a reader cannot navigate the same way as every other project's would defeat the one
/// thing the shared shape buys.
/// </para>
/// </summary>
public sealed class ProjectRunSkillSetCommand : Hall9kAsyncCommand<ProjectRunSkillSetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;

        [CommandOption("--file <PATH>")]
        [Description(
            "A markdown file whose whole content becomes the run skill. It must carry all six of the "
            + "shared sections: Prerequisites, One-time setup, Launch, How to know it is up, Address or "
            + "entry point, Human steps. The shape line at the top is written for you from --shape.")]
        public string File { get; init; } = string.Empty;

        [CommandOption("--shape <pointer|full-text|none-discoverable>")]
        [Description(
            "Which shape this skill is, stated on its own first line. 'pointer': the repository already "
            + "documents launching it and this skill points at those files by path, adding only what "
            + "they leave out. 'full-text': it does not, so this skill holds the whole procedure and "
            + "cites the file each step derives from. 'none-discoverable': nothing in the repository "
            + "says how to run it, and this skill says so.")]
        public string Shape { get; init; } = string.Empty;

        [CommandOption("--against-commit <SHA>")]
        [Description(
            "The commit this was written against, recorded on the event. Optional, and deliberately "
            + "not defaulted to whatever HEAD happens to be here: an unobserved fact is recorded as "
            + "unknown rather than filled in with a plausible value.")]
        public string? AgainstCommit { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, cancellationToken);
    }

    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.File.IsBlank())
        {
            throw new DomainValidationException("--file is required: the path to the run-skill markdown to set.");
        }

        if (!System.IO.File.Exists(settings.File))
        {
            throw new DomainValidationException($"'{settings.File}' does not exist.");
        }

        RunSkillShape shape = RunSkillShape.Parse(settings.Shape);
        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        string body = await System.IO.File.ReadAllTextAsync(settings.File, cancellationToken);

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        ProjectRunSkillRecorded recorded = ProjectDecider.RecordRunSkill(
            project.Id, RunSkillDocument.Compose(shape, body), shape, RunSkillAuthor.Hand, settings.AgainstCommit,
            context.OwnerId, DateTimeOffset.UtcNow);
        session.Events.Append(project.Id, recorded);
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Set[/] the {shape.Value} run skill for '{project.Name.EscapeMarkup()}'. "
            + "The daemon writes it to the ledger on its next sweep.");
        return ExitCodes.Ok;
    }
}
