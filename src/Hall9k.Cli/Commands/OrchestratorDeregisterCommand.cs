using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// An orchestrator window leaving on purpose (idea 89471598, piece 1) — what the recipe's own
/// restart and close steps call, so a deliberate exit reads as a shutdown rather than waiting to
/// be found gone by the daemon's presence sweep.
/// <para>
/// Idempotent: a project with nothing registered here says so and exits zero. This is the last
/// thing a closing window does, and a close step that can fail is a close step an operator learns
/// to skip. A window drops its own claim and only its own, which is why <c>--pid</c> is required
/// rather than inferred from whatever is registered: the recipe's restart contract leaves the old
/// window open after a replacement has registered, so its later close step would otherwise
/// unregister a window that is running.
/// </para>
/// </summary>
public sealed class OrchestratorDeregisterCommand : Hall9kAsyncCommand<OrchestratorDeregisterCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description(
            "The project whose orchestrator window is leaving: its name, an unambiguous fragment, or its full id.")]
        public string Project { get; init; } = string.Empty;

        [CommandOption("--pid <PID>")]
        [Description(
            "The leaving window's own process id, the one it registered with. A registration held by any "
            + "other process is left standing, so a stale window's close step can never unregister the "
            + "replacement that took over from it; Claude Code sets CLAUDE_PID in every Bash tool it spawns.")]
        public int ProcessId { get; init; }

        /// <summary>
        /// <c>--project</c> is required rather than inferred, the same reasoning
        /// <c>OrchestratorRegisterCommand.Settings.Validate</c> gives: a blank string reaches
        /// <see cref="ProjectResolver"/> as a fragment that matches everything, which on a
        /// single-project install would quietly deregister that one. <c>--pid</c> is required
        /// for the parallel reason one step further in: left off, the only thing this command
        /// could deregister is whoever happens to hold the registration, which is exactly the
        /// live replacement a stale window's close step must not touch. Caught here rather than
        /// left to the decider so the answer needs no database.
        /// </summary>
        public override ValidationResult Validate() => (Project.IsBlank(), ProcessId <= 0) switch
        {
            (true, _) => ValidationResult.Error(
                "--project is required: name the project this orchestrator window drives."),
            (_, true) => ValidationResult.Error(
                "--pid is required: pass this window's own process id, the one it registered with "
                + "($CLAUDE_PID in Claude Code). A window deregisters itself and only itself."),
            _ => ValidationResult.Success(),
        };
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        Guid streamId = OrchestratorPresenceStreamId.For(context.NodeId, project.Id);
        OrchestratorPresenceAggregate? presence = await session.Events
            .AggregateStreamAsync<OrchestratorPresenceAggregate>(streamId, token: cancellationToken);

        OrchestratorDeregistrationDecision decision =
            OrchestratorPresenceDecider.Deregister(presence, settings.ProcessId, DateTimeOffset.UtcNow);

        if (decision.ShutDown is not { } shutDown)
        {
            // Both non-shapes exit zero: this is a close step, and the window it names is gone
            // either way. They read differently because they are different facts — nothing here
            // at all, against somebody else's live claim this window must not touch.
            FormattableString message = (decision.Outcome, presence) switch
            {
                (OrchestratorDeregistrationOutcome.HeldByAnotherWindow, { } holder) =>
                    $"[dim]The orchestrator registration for {project.Name} on this machine belongs to '{holder.SessionName}' (pid {holder.ProcessId}), not to pid {settings.ProcessId} — left as it is.[/]",
                _ =>
                    $"[dim]No orchestrator is registered for {project.Name} on this machine — nothing to deregister.[/]",
            };
            AnsiConsole.MarkupLineInterpolated(message);
            return ExitCodes.Ok;
        }

        session.Events.Append(streamId, shutDown);
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Deregistered[/] '{shutDown.SessionName}' (pid {shutDown.ProcessId}) as the orchestrator for {project.Name}.");
        return ExitCodes.Ok;
    }
}
