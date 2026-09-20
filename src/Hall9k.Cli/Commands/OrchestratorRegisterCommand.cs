using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Exceptions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// An orchestrator window declaring itself live for one project on this node (idea 89471598,
/// piece 1) — the first step of the launch anchor's own start-up sequence, so the record exists
/// without the operator ever typing this.
/// <para>
/// Presence cannot be discovered, which is why this command exists: Claude Code's own session
/// registry cannot tell the orchestrator window from the discovery, refinement, and planning
/// sessions that also run in the same project home, and another vendor's CLI may keep no registry
/// at all. A process id is the one identity all of them share, and it is what the daemon's own
/// liveness check reads.
/// </para>
/// </summary>
public sealed class OrchestratorRegisterCommand : Hall9kAsyncCommand<OrchestratorRegisterCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description("The project this window drives: its name, an unambiguous fragment, or its full id.")]
        public string Project { get; init; } = string.Empty;

        [CommandOption("--session <NAME>")]
        [Description(
            "The window's own session name, the name the session mesh and h9k status address it by. "
            + "In Claude Code it is the name field of ~/.claude/sessions/<pid>.json.")]
        public string Session { get; init; } = string.Empty;

        [CommandOption("--pid <PID>")]
        [Description(
            "The window's own process id. Liveness is checked against this alone, so it works for any "
            + "vendor's CLI; Claude Code sets CLAUDE_PID in every Bash tool it spawns.")]
        public int ProcessId { get; init; }

        [CommandOption("--cli <NAME>")]
        [Description("The agent CLI the window runs under (claude-code, codex, and so on). Defaults to claude-code.")]
        public string Cli { get; init; } = LaunchText.DefaultCli;

        [CommandOption("--replace")]
        [Description(
            "Take over from an orchestrator already live for this project on this node, recording that "
            + "one as shut down first. Without it, a second live window is refused naming the first.")]
        public bool Replace { get; init; }

        /// <summary>
        /// <c>--project</c> is required rather than inferred. Left to
        /// <see cref="ProjectResolver"/> as a blank string it would match every project by
        /// fragment, which on a single-project install silently registers against that one and
        /// on a multi-project install reports an ambiguity rather than the missing option: a
        /// mistyped flag would look like either, and never like the mistake it is.
        /// </summary>
        public override ValidationResult Validate() => Project.IsBlank()
            ? ValidationResult.Error("--project is required: name the project this orchestrator window drives.")
            : ValidationResult.Success();
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        Guid streamId = OrchestratorPresenceStreamId.For(context.NodeId, project.Id);

        // Fetched before the aggregate this decision is made against, and used unmodified as the
        // append's own expectedVersion, exactly as TaskRegisterSessionCommand fences its own
        // check-then-act (see that command's own comment for why a late refetch fences nothing).
        // The refusal below is this feature's whole guarantee, and without a fence two windows
        // registering in the same instant would each read no live orchestrator, each pass it, and
        // each append a launch: the second silently overwrites the first, which is precisely what
        // the refusal exists to prevent. Only this command is fenced — the other two writers
        // (h9k orchestrator deregister and the daemon's own loss sweep) end a registration rather
        // than granting one, and an ending that lands after a newer window registered is already
        // ignored by OrchestratorPresenceAggregate's own superseded-ending guard, so neither can
        // clobber a live registration whatever order they commit in.
        StreamState? fence = await session.Events.FetchStreamStateAsync(streamId, cancellationToken);
        OrchestratorPresenceAggregate? presence = await session.Events
            .AggregateStreamAsync<OrchestratorPresenceAggregate>(streamId, token: cancellationToken);

        OrchestratorRegistrationDecision decision = OrchestratorPresenceDecider.Register(
            presence, context.NodeId, project.Id, settings.Session, settings.ProcessId, settings.Cli,
            settings.Replace, new OrchestratorProcessTableProbe(), DateTimeOffset.UtcNow);

        if (decision.Launched is not { } launched)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Already registered[/] as the live orchestrator for {project.Name} (pid {settings.ProcessId}) — nothing recorded.");
            return ExitCodes.Ok;
        }

        // The displaced window's ending and this launch land in one SaveChangesAsync, so the
        // stream can never show the window that was replaced, or the one this register found
        // gone, still live beside the one that took over.
        OrchestratorShutDown? replaced = decision.Replaced;
        OrchestratorLost? lost = decision.Lost;
        List<object> ordered = [];
        if (replaced is not null)
        {
            ordered.Add(replaced);
        }

        if (lost is not null)
        {
            ordered.Add(lost);
        }

        ordered.Add(launched);
        object[] appended = [.. ordered];
        if (fence is null)
        {
            session.Events.StartStream<OrchestratorPresenceAggregate>(streamId, appended);
        }
        else
        {
            session.Events.Append(streamId, expectedVersion: fence.Version + appended.Length, appended);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is EventStreamUnexpectedMaxEventIdException
            or ExistingStreamIdCollisionException)
        {
            throw new DomainConflictException(
                $"Another orchestrator registration for {project.Name} landed on this machine at the same "
                + "moment, so nothing was recorded here. h9k orchestrator status --project "
                + $"{project.Name} to see which window won.");
        }

        if (replaced is not null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Replaced[/] '{replaced.SessionName}' (pid {replaced.ProcessId}), recorded as shut down.");
        }

        if (lost is not null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Lost[/] '{lost.SessionName}' (pid {lost.ProcessId}): it was still registered here and its process is gone, so it is recorded as lost first.");
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Registered[/] '{launched.SessionName}' ({launched.Cli}, pid {launched.ProcessId}) as the orchestrator for {project.Name}.");
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]h9k status and h9k orchestrator status name it from here. Deregister it when this window closes or restarts: h9k orchestrator deregister --project {project.Name} --pid {launched.ProcessId}[/]");
        return ExitCodes.Ok;
    }
}
