using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class LogsCommand : Hall9kAsyncCommand<LogsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--run <RUN_ID>")]
        [Description("A specific run (default: the task's latest)")]
        public string? Run { get; init; }

        [CommandOption("--raw")]
        [Description("Dump the raw stream-json instead of the rendered transcript")]
        public bool Raw { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        IReadOnlyList<RunListItem> runs = await session.Query<RunListItem>()
            .Where(r => r.TaskId == taskId)
            .OrderByDescending(r => r.DispatchedAt)
            .ToListAsync(cancellationToken);

        if (runs.Count == 0)
        {
            throw new DomainNotFoundException($"Task {taskId} has no runs yet.");
        }

        RunListItem run = SelectRun(runs, settings.Run)
            ?? throw new DomainNotFoundException($"No run matching '{settings.Run}' on task {taskId}.");

        // run.RunDirectory is recorded once, at dispatch, and never updated (RunDispatched doc
        // comment); a task that has since reached true closeout or been abandoned has had its
        // whole directory relocated into or out of tasks/_archive/ by the render sweep, taking
        // this run's directory with it (backlog 51). Reading a merged task's transcript is
        // exactly when that recorded path is stale, so resolve where it actually is first.
        string streamFile = RunPaths.StreamFile(RunPaths.ResolveCurrentDirectory(run.RunDirectory));
        if (!File.Exists(streamFile))
        {
            // An attached interactive session (h9k task work) never writes stream.jsonl on any
            // machine — it runs in the operator's own terminal, not through ClaudeExecutor's
            // redirected --output-format stream-json — so InteractiveSessionCount (recorded once
            // and never cleared, unlike NodeId, which delivery can reassign) is what actually
            // distinguishes that from a headless run whose transcript merely lives on a node this
            // one cannot read (conformance review, cycle 4: the other-node hypothesis is false by
            // construction for a run that was, or still is, an interactive claim). Loaded only
            // here, on the single selected run, rather than eagerly on every run on the task —
            // RunDetails is the heavyweight projection and this command otherwise never needs it.
            int interactiveSessionCount = (await session.LoadAsync<RunDetails>(run.Id, cancellationToken))
                ?.InteractiveSessionCount ?? 0;
            throw new DomainNotFoundException(interactiveSessionCount > 0
                ? $"No stream file for run {run.Id} ({streamFile}). It was worked interactively " +
                  "(h9k task work) — an attached session runs in the operator's own terminal and is " +
                  "never recorded to a transcript file. h9k task show shows its status instead."
                : $"No stream file for run {run.Id} on this machine ({streamFile}). " +
                  "It may have run on another node.");
        }

        AnsiConsole.MarkupLine(
            $"[dim]run {run.Id} · {run.State.Value} · dispatched {run.DispatchedAt.ToLocalTime():g}[/]\n");

        IEnumerable<string> lines = File.ReadLines(streamFile);
        if (settings.Raw)
        {
            foreach (string line in lines)
            {
                Console.WriteLine(line);
            }
        }
        else
        {
            foreach (string rendered in StreamRenderer.Render(lines))
            {
                AnsiConsole.MarkupLine(rendered);
            }
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// Picks which of a task's runs this command actually reads. <paramref name="runOption"/> is
    /// an explicit ask, matched by its id's trailing fragment. With no explicit ask, the newest run
    /// is not automatically right: CloseoutEngine's missing-run sweep can reconstruct a stub run
    /// (<see cref="RunListItem.IsReconstructed"/>) that never actually dispatched and so never
    /// wrote a transcript, and a reconstruction always sorts newest by construction — it exists
    /// because an earlier, transcript-bearing run already dispatched and finished. Skipping a
    /// reconstructed run in favor of the newest one that is not is what makes this command resolve
    /// to the run that actually produced a transcript instead of an honest 404 on the stub
    /// (independent pre-PR review, cycle 1, conformance) — falling back to the stub itself only
    /// when every run on the task is one, so a stub-only task still gets the same honest "no
    /// stream file" answer it always has.
    /// </summary>
    internal static RunListItem? SelectRun(IReadOnlyList<RunListItem> runsNewestFirst, string? runOption) =>
        runOption.IsBlank()
            ? runsNewestFirst.FirstOrDefault(r => !r.IsReconstructed) ?? runsNewestFirst[0]
            : runsNewestFirst.FirstOrDefault(r => r.Id.ToString("N")
                .EndsWith(runOption.Replace("-", ""), StringComparison.OrdinalIgnoreCase));
}
