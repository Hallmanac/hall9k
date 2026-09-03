using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The escape-hatch invariant's own executor (the 2026-09-01 ruling, idea fcaded0b's design
/// rulings 4 and 5): any interaction a dispatched agent has with a party outside its session is
/// logged through this command, unconditionally, even one the interacting party asked it to keep
/// quiet. An agent-facing observation-gate command in the same style as
/// <see cref="TaskWriteJiraCommand"/> and <see cref="TaskLinkIssueCommand"/> — structured fields
/// land on the stream rather than prose in a transcript — except there is nothing external here
/// to verify the claim against: the platform records what its own channels can see, honestly,
/// which is the sense in which this logging is best-effort rather than an enforcement mechanism.
/// <see cref="Settings.HumanDirected"/> is the one fact this command exists to keep honest — a
/// human directing an interaction or its outcome is recorded as exactly that, never folded into
/// the agent's own report as though it were the agent's independent decision.
/// </summary>
public sealed class TaskLogInteractionCommand : Hall9kAsyncCommand<TaskLogInteractionCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous prefix)")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--party <TEXT>")]
        [Description(
            "Who or what outside this session you interacted with: another agent session reached "
            + "through the mesh, a human reached the same way, an external API or service this "
            + "task's own prompt did not already route through an observation-gate command.")]
        public string Party { get; init; } = string.Empty;

        [CommandOption("--summary <TEXT>")]
        [Description("What happened, in your own words: what was said or asked, and what you did about it.")]
        public string Summary { get; init; } = string.Empty;

        [CommandOption("--human-directed")]
        [Description(
            "Set when a human, not your own judgment, directed the interaction or its outcome — the "
            + "record then says so plainly, even if the human asked you to report it as your own "
            + "decision or to skip logging it altogether. Requires --reason.")]
        public bool HumanDirected { get; init; }

        [CommandOption("--reason <TEXT>")]
        [Description(
            "Required with --human-directed: the human's own instruction or reason, in their words "
            + "where you can. Optional otherwise.")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        Validate(settings);

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        TaskDetails task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (task.CurrentRunId is not { } runId)
        {
            throw new DomainConflictException(
                $"Task {taskId} has no active run to log this interaction against — it is {task.State.Value}. "
                + "This command records an interaction a dispatched agent had against its own run, so it only "
                + "works while one is live.");
        }

        // Guards against a stale CurrentRunId (task.CurrentRunId is a projection and can lag or
        // outlive the run it names): FetchStreamStateAsync returning null means no run stream
        // exists yet, and appending anyway would have Marten silently create one — a stream
        // holding ExternalInteractionLogged with no RunDispatched underneath it, an invalid run
        // history. expectedVersion fences the append the same way h9k review resolve and h9k pr
        // resolve fence theirs, so a concurrent writer loses loudly instead of interleaving.
        StreamState? fence = await session.Events.FetchStreamStateAsync(runId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Task {taskId}'s run {runId} has no run stream to log this interaction against.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        ExternalInteractionLogged logged = BuildEvent(settings, runId, context.OwnerId, DateTimeOffset.UtcNow);
        session.Events.Append(runId, expectedVersion: fence.Version + 1, logged);
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(settings.HumanDirected
            ? $"[green]Logged[/] (human-directed): {settings.Party.EscapeMarkup()} — {settings.Summary.EscapeMarkup()}"
            : $"[green]Logged[/]: {settings.Party.EscapeMarkup()} — {settings.Summary.EscapeMarkup()}");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Checked before the store ever opens (the <see cref="TaskWriteJiraCommand"/> convention: a
    /// cheap local mistake costs nothing, rather than paying for a task resolution and a node
    /// bootstrap only to fail on a missing flag afterward) — and independently testable without a
    /// store for the same reason.
    /// </summary>
    internal static void Validate(Settings settings)
    {
        if (settings.Party.IsBlank())
        {
            throw new DomainValidationException("--party names who or what outside this session you interacted with.");
        }

        if (settings.Summary.IsBlank())
        {
            throw new DomainValidationException("--summary says what happened.");
        }

        if (settings.HumanDirected && settings.Reason.IsBlank())
        {
            throw new DomainValidationException(
                "--human-directed records that a human directed this interaction or its outcome — say what "
                + "they directed and why with --reason, so the record does not merely assert human involvement "
                + "without saying what it was.");
        }
    }

    /// <summary>
    /// Maps already-validated settings onto the event to append — pulled out of
    /// <see cref="ExecuteAsync"/> so the escape-hatch invariant's own field mapping is testable
    /// without a store, the same shape <see cref="TaskResolveCommand.BuildFailedRunPullRequestEvent"/>
    /// already is. Does not itself validate; <see cref="Validate"/> is <see cref="ExecuteAsync"/>'s
    /// own gate before either of these runs.
    /// </summary>
    internal static ExternalInteractionLogged BuildEvent(
        Settings settings, Guid runId, Guid loggedByOwnerId, DateTimeOffset now) =>
        new(runId, now, settings.Party.Trim(), settings.Summary.Trim(), settings.HumanDirected,
            settings.Reason.IsNotBlank() ? settings.Reason.Trim() : null, loggedByOwnerId);
}
