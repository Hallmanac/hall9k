using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Orchestrator;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One project's orchestrator feed (idea 89471598, piece 2): everything past the cursor that the
/// project's own <c>--orchestrator-feed</c> band admits, oldest first, grouped by task. This is
/// what an orchestrator window runs at start-up to learn what happened while no window was live,
/// in place of tallying the daemon's log.
/// <para>
/// Printing and draining are separate on purpose. A plain read shows the same items again on the
/// next invocation, which is what makes it safe to run twice; <c>--drain</c> is the explicit act
/// that says "I have read these" and moves the cursor. <c>--since</c> reads history and never
/// touches the cursor at all, so a second window catching up cannot silently consume what the
/// first one has not read yet.
/// </para>
/// </summary>
public sealed class OrchestratorFeedCommand : Hall9kAsyncCommand<OrchestratorFeedCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description(
            "Which project's feed to read: its name, an unambiguous fragment, or its full id. Omit "
            + "it when exactly one project is registered.")]
        public string? Project { get; init; }

        [CommandOption("--drain")]
        [Description(
            "Advance this project's cursor past everything this read inspected and settled, after "
            + "printing it. Without it the same items are shown again next time, which is what makes "
            + "a plain read safe to repeat. The last few seconds of the log are held back even with "
            + "it, so an event still committing behind one already shown cannot be skipped — those "
            + "items print now and come back once.")]
        public bool Drain { get; init; }

        [CommandOption("--since <TIME>")]
        [Description(
            "Read history from a point in time instead of from the cursor, and leave the cursor "
            + "exactly where it is: a duration back from now (45m, 6h, 3d, 2w) or an instant "
            + "(2026-09-19, 2026-09-19T14:00:00-04:00). Cannot be combined with --drain — a read "
            + "that ignores the cursor has no business moving it.")]
        public string? Since { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Since is not null && settings.Drain)
        {
            throw new DomainValidationException(
                "--since reads history without moving the cursor, so it cannot be combined with "
                + "--drain. Run them separately: h9k orchestrator feed --since 6h to look back, and "
                + "h9k orchestrator feed --drain to take what is undrained.");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails project = await ResolveProjectAsync(session, settings.Project, cancellationToken);
        OrchestratorFeedLevel level = project.OrchestratorFeed;
        OrchestratorFeedReader reader = new(new ReplicationProjectResolver());
        // One clock for the whole invocation: what --since is measured back from, what decides
        // which events are settled enough to drain, and what the drain itself is stamped with.
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // A short courtesy lease over this project's cursor (idea 89471598, piece 3): held only
        // for --drain, so the feed courier's own spawn gate never dispatches into the identical
        // window a human is reading and draining by hand. A plain read never moves the cursor, so
        // it needs no lease at all.
        if (settings.Drain)
        {
            session.Store(OrchestratorFeedDrainLease.Held(project.Id, now));
            await session.SaveChangesAsync(cancellationToken);
        }

        try
        {
            OrchestratorFeedRead read = settings.Since is { } since
                ? await reader.ReadSinceAsync(
                    session, project.Id, level,
                    OrchestratorFeedSince.Parse(since, now), now, cancellationToken)
                : await reader.ReadUndrainedAsync(session, project.Id, level, now, cancellationToken);

            await PrintAsync(session, project, level, read, settings, cancellationToken);

            if (settings.Drain)
            {
                bool moved = await OrchestratorFeedReader.DrainAsync(
                    session, project.Id, read.DrainableThroughSequence, now, cancellationToken);

                // Where a drain moved nothing but the reader had reason to expect it to, say so
                // rather than leaving silence: the way past the scan's cap is to run this again, and
                // a caller repeating it has to be able to tell a pass that advanced from one that
                // cannot yet, instead of looping on the same 2000 events. An empty uncapped read is
                // the one case that needs no line — "Nothing undrained" above already said it.
                if (moved)
                {
                    AnsiConsole.MarkupLineInterpolated(
                        $"[dim]Drained through sequence {read.DrainableThroughSequence}.[/]");
                }
                else if (read.ScanWasCapped || read.Items.Count > 0)
                {
                    AnsiConsole.MarkupLine(
                        "[dim]Cursor unchanged: nothing this pass inspected has settled past where it "
                        + "already stood, or another window drained further while this one printed.[/]");
                }
            }
        }
        finally
        {
            // Released as soon as this command is done rather than left to its own expiry, so an
            // ordinary fast drain does not hold a courier off for the rest of the lease's window.
            if (settings.Drain)
            {
                session.Delete<OrchestratorFeedDrainLease>(project.Id);
                await session.SaveChangesAsync(cancellationToken);
            }
        }

        return ExitCodes.Ok;
    }

    private static async Task PrintAsync(
        IQuerySession session,
        ProjectDetails project,
        OrchestratorFeedLevel level,
        OrchestratorFeedRead read,
        Settings settings,
        CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]{project.Name} — orchestrator feed at the {level.Value.ToLowerInvariant()} level[/]");

        if (read.Items.Count == 0)
        {
            // Never "nothing" full stop when the scan filled its cap: a pass whose whole 2000
            // events belonged to other projects admits no items and is the furthest thing from
            // caught up. Saying "nothing undrained" there and stopping is what would end a
            // window's start-up loop with the project's real history still past the cap, so the
            // empty line names only what this pass saw and the cap notice below still prints.
            AnsiConsole.MarkupLine((settings.Since is null, read.ScanWasCapped) switch
            {
                (_, true) => "[dim]Nothing your band admits in the events this pass inspected.[/]",
                (true, false) => "[dim]Nothing undrained.[/]",
                (false, false) => "[dim]Nothing in that window.[/]",
            });
        }
        else
        {
            await PrintItemsAsync(session, read, cancellationToken);
        }

        if (read.ScanWasCapped)
        {
            // The way past the cap differs by which read this was, and naming the wrong one is
            // worse than naming none: a --since read cannot be drained at all, so telling its
            // reader to drain points them at the one combination this same command refuses.
            string wayPast = settings.Since is null
                ? "Drain and run it again to find out."
                : "Move --since closer to now and run it again to read past it.";
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]This read filled its {OrchestratorFeedReader.MaxEventsPerRead}-event cap, so there may be more past it.[/] {wayPast}");
        }
    }

    private static async Task PrintItemsAsync(
        IQuerySession session, OrchestratorFeedRead read, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, string> headings = await HeadingsAsync(session, read.Items, cancellationToken);
        IReadOnlyList<OrchestratorFeedGroup> groups = OrchestratorFeedRenderer.Group(
            read.Items,
            taskId => headings.TryGetValue(taskId, out string? heading)
                ? heading
                // A task id with no projection behind it on this node: replication can carry a
                // task's own events here before its document lands, and a purged project leaves
                // the same shape. Named as unreadable rather than printed bare, so the line still
                // says what it is (AGENTS.md, never guess at unobserved facts).
                : $"{TaskListCommand.ShortId(taskId)}  (a task this node has no record of yet)");

        foreach (string line in PrintableLines(groups, TimeZoneInfo.Local))
        {
            // Plain, never markup: every item quotes text a person or an agent wrote, and a body
            // containing square brackets must reach the terminal as itself.
            AnsiConsole.WriteLine(line);
        }
    }

    /// <summary>
    /// The rendered lines, each one made safe to hand a terminal
    /// (<see cref="ExternalText.OneLine"/>). This is the feed's own application of the rule every
    /// other surface that prints outside text already follows, and it belongs here rather than in
    /// <see cref="OrchestratorFeedRenderer"/> because the character list lives in Connectors,
    /// which the Domain cannot reference.
    /// <para>
    /// It has to be applied, because almost every character in a feed line was written by
    /// somebody other than Hall9k: a heading carries a task objective, which adoption
    /// (PLAN.md §3.1a) seeds from an issue title anyone who can file an issue wrote, and an item
    /// quotes a message body from another node, an agent's own park or failure reason, or an
    /// idea's text. <c>OrchestratorFeedDescription</c>'s own flattening folds whitespace and
    /// nothing else, so an escape sequence or a bidirectional override survives it intact — and
    /// the orchestrator recipe makes this command a window's mandatory start-up step, so a forged
    /// feed printed there is read as the platform's own account of what happened.
    /// </para>
    /// <para>
    /// The whole line goes through rather than each part as it is composed, so a part added later
    /// cannot be the one that was forgotten. <see cref="ExternalText.OneLine"/> rather than
    /// <see cref="ExternalText.ForTerminal"/>: one item is one line, and a body free to emit a
    /// newline could otherwise print feed items of its own choosing underneath itself.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> PrintableLines(
        IReadOnlyList<OrchestratorFeedGroup> groups, TimeZoneInfo zone) =>
        [.. OrchestratorFeedRenderer.Render(groups, zone).Select(ExternalText.OneLine)];

    /// <summary>
    /// How each task in this read is named: its short id and its objective, never the bare id.
    /// One batched load rather than one per group.
    /// <para>
    /// The objective is flattened before it is cut, not after: a cut counted over characters the
    /// terminal never shows would spend the heading's budget on invisible ones and clip the words
    /// a reader came for. <see cref="PrintableLines"/> is what makes the line safe either way.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> HeadingsAsync(
        IQuerySession session,
        IReadOnlyList<OrchestratorFeedItem> items,
        CancellationToken cancellationToken)
    {
        Guid[] taskIds = [.. items.Select(item => item.TaskId).OfType<Guid>().Distinct()];
        if (taskIds.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        IReadOnlyList<TaskDetails> tasks = await session.LoadManyAsync<TaskDetails>(cancellationToken, taskIds);
        return tasks.ToDictionary(
            task => task.Id,
            task => $"{TaskListCommand.ShortId(task.Id)}  "
                + TaskListCommand.Truncate(ExternalText.OneLine(task.Objective), 90));
    }

    /// <summary>
    /// The named project, or the only one there is. Unlike <c>h9k orchestrator project</c>, which
    /// prints a block per project when several are registered, this refuses: a feed has a cursor
    /// per project and <c>--drain</c> would otherwise advance several of them from one command
    /// nobody aimed at any of them.
    /// </summary>
    private static async Task<ProjectDetails> ResolveProjectAsync(
        IQuerySession session, string? named, CancellationToken cancellationToken)
    {
        if (named.IsNotBlank())
        {
            return await ProjectResolver.ResolveAsync(session, named, cancellationToken);
        }

        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        return projects switch
        {
            [ProjectDetails single] => single,
            [] => throw new DomainNotFoundException(
                "No projects are registered yet. Register one: "
                + "h9k project add --name <name> --repo-url <url>"),
            _ => throw new DomainValidationException(
                $"{projects.Count} projects are registered, so --project is required: each one keeps its "
                + "own feed cursor. Name one: "
                + string.Join(", ", projects.Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase))),
        };
    }
}
