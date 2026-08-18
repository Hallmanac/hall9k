using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class ProjectSetCommand : Hall9kAsyncCommand<ProjectSetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name or id")]
        public string Project { get; init; } = string.Empty;

        [CommandOption("--skip-permissions <BOOL>")]
        [Description("Agents run with --dangerously-skip-permissions (log #9)")]
        public bool? SkipPermissions { get; init; }

        [CommandOption("--max-parallel <N>")]
        public int? MaxParallelAgents { get; init; }

        [CommandOption("--verify <NAME=COMMAND>")]
        [Description("Verification gate, e.g. --verify \"test=dotnet test\"; repeat for more. Replaces the whole list.")]
        public string[] Verify { get; init; } = [];

        [CommandOption("--link <NAME=URL>")]
        [Description("Context link injected into agent prompts; repeat for more. Replaces the whole list.")]
        public string[] Links { get; init; } = [];

        [CommandOption("--commit-style <STYLE>")]
        [Description(
            "How follow-up runs land review fixes on the PR branch (Decisions Log #26): "
            + "'narrative' folds each fix into its owning commit (fixup + autosquash + force-with-lease "
            + "push, the AGENTS.md authored-history rule), 'append' stacks fix commits on top, "
            + "'default' clears the project override so the platform default applies "
            + "(DaemonOptions.DefaultCommitStyle; narrative unless configured otherwise)")]
        public string? CommitStyle { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails details = await ResolveAsync(session, settings.Project, cancellationToken);
        ProjectAggregate project = (await session.Events.AggregateStreamAsync<ProjectAggregate>(details.Id, token: cancellationToken))!;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
            project,
            verifyCommands: settings.Verify.Length > 0
                ? Optional<IReadOnlyList<VerifyCommand>>.Of([.. settings.Verify.Select(ParseVerify)])
                : Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: settings.SkipPermissions is { } skip ? skip : Optional<bool>.None,
            maxParallelAgents: settings.MaxParallelAgents is { } max ? max : Optional<int>.None,
            contextLinks: settings.Links.Length > 0
                ? Optional<IReadOnlyList<ContextLink>>.Of([.. settings.Links.Select(ParseLink)])
                : Optional<IReadOnlyList<ContextLink>>.None,
            DateTimeOffset.UtcNow,
            context.OwnerId,
            commitStyle: settings.CommitStyle is { } commitStyle
                ? Optional<CommitStyle>.Of(ParseCommitStyle(commitStyle))
                : Optional<CommitStyle>.None);

        session.Events.Append(details.Id, changed);
        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"project-changed:{details.Id}", cancellationToken);

        AnsiConsole.MarkupLine($"[green]Project '{details.Name.EscapeMarkup()}' settings updated.[/]");
        return ExitCodes.Ok;
    }

    private static VerifyCommand ParseVerify(string value)
    {
        int separator = value.IndexOf('=');
        return separator <= 0
            ? throw new DomainValidationException($"--verify expects name=command, got '{value}'.")
            : new VerifyCommand(value[..separator].Trim(), value[(separator + 1)..].Trim());
    }

    private static CommitStyle ParseCommitStyle(string value) => value.Trim().ToLowerInvariant() switch
    {
        "narrative" => CommitStyle.Narrative,
        "append" => CommitStyle.Append,
        "default" => CommitStyle.Unknown,
        _ => throw new DomainValidationException(
            $"--commit-style expects narrative, append, or default; got '{value}'. Narrative folds "
            + "follow-up fixes into their owning commits (fixup + autosquash, the AGENTS.md "
            + "authored-history rule); append stacks fix commits on top of the existing history; "
            + "default clears the project override so the platform default applies."),
    };

    private static ContextLink ParseLink(string value)
    {
        int separator = value.IndexOf('=');
        return separator <= 0
            ? throw new DomainValidationException($"--link expects name=url, got '{value}'.")
            : new ContextLink(value[..separator].Trim(), new Uri(value[(separator + 1)..].Trim()));
    }

    private static async Task<ProjectDetails> ResolveAsync(IDocumentSession session, string nameOrId, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(nameOrId, out Guid id))
        {
            return await session.LoadAsync<ProjectDetails>(id, cancellationToken)
                ?? throw new DomainNotFoundException($"No project with id {id}.");
        }

        return (await session.Query<ProjectDetails>().Where(p => p.Name == nameOrId).Take(1).ToListAsync(cancellationToken)).FirstOrDefault()
            ?? throw new DomainNotFoundException($"No project named '{nameOrId}'.");
    }
}
