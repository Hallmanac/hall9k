using Hall9k.Cli.Commands;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Orchestrator;
using Hall9k.Connectors.Orchestrator;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using JasperFx;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Diagnostics;

/// <summary>What <see cref="CourierDoctor"/> knows about one project with an orchestrator window registered on this node.</summary>
/// <param name="ProjectName">The project's name.</param>
/// <param name="WindowLive">Whether the registered window's process is still running.</param>
/// <param name="LaunchedAt">When the window launched, or last re-registered with a changed address.</param>
/// <param name="Streak">The project's failure streak document, if one exists.</param>
/// <param name="DayCounter">The project's day spawn counter document, if one exists.</param>
/// <param name="OldestUndrainedAt">When the oldest undrained feed item happened, null when nothing is undrained or the feed was not read.</param>
/// <param name="FeedCheckIncomplete">Whether the feed read gave up before reaching the end of the log, so a null <paramref name="OldestUndrainedAt"/> does not mean nothing is undrained.</param>
/// <param name="Elevation">The window's and the daemon's token elevation, null when the comparison does not apply (off Windows, no daemon running, or a window that is not live).</param>
/// <param name="AutostartRegistered">Whether the daemon's autostart is registered, which picks the fix a block names.</param>
internal sealed record CourierDoctorFacts(
    string ProjectName,
    bool WindowLive,
    DateTimeOffset? LaunchedAt,
    CourierFailureStreak? Streak,
    CourierDaySpawnCounter? DayCounter,
    DateTimeOffset? OldestUndrainedAt,
    bool FeedCheckIncomplete = false,
    CourierDoctorElevation? Elevation = null,
    bool AutostartRegistered = false);

/// <summary>A live window's token elevation beside the running daemon's, for the doctor's elevation line.</summary>
internal sealed record CourierDoctorElevation(ProcessElevation Window, ProcessElevation Daemon)
{
    public bool Blocked => CourierGate.ElevationBlocks(Daemon, Window);
}

/// <summary>What the doctor reads once for the whole section to compare each window's elevation against.</summary>
/// <param name="Reader">The elevation reader; its <see cref="IProcessElevationReader.IsSupported"/> decides whether the comparison is shown at all.</param>
/// <param name="Daemon">The running daemon from <c>DaemonProcess.Probe()</c>, null when none is running.</param>
/// <param name="AutostartRegistered">Read only when a window is blocked, since on Windows it asks Task Scheduler.</param>
internal sealed record CourierDoctorElevationSource(
    IProcessElevationReader Reader, DaemonProcessDescriptor? Daemon, Func<bool> AutostartRegistered);

/// <summary>
/// <c>h9k doctor</c>'s courier section: the feed courier's health for every project with an
/// orchestrator window registered on this node. It warns on a failure streak (the thing the
/// backoff makes survivable, so the operator still has to be told), on a spent day cap, and on a
/// feed item that has sat undrained for over an hour under a window that is live and has been for
/// as long, which is the symptom nothing else names when couriers never run at all. On Windows it
/// also gives each live window's token elevation beside the daemon's, and warns for a window the
/// daemon cannot reach because the window is elevated and the daemon is not. Reads the same
/// documents <c>h9k status</c> does and words them the same way.
/// </summary>
public static class CourierDoctor
{
    /// <summary>How many failed couriers in a row it takes to warn.</summary>
    public const int StreakWarningThreshold = 5;

    /// <summary>How long an item may sit undrained, under a live window, before it warns.</summary>
    public static readonly TimeSpan UndrainedWarningAge = TimeSpan.FromHours(1);

    /// <summary>
    /// How many capped scans the undrained-item check will page through before it reports the
    /// check as incomplete, so a log with a very long run of unrelated events cannot make
    /// <c>h9k doctor</c> read without end.
    /// </summary>
    public const int MaxFeedPages = 10;

    public static async Task RunAsync(string connectionString, CancellationToken cancellationToken)
    {
        // Opened CreateOnly like every ordinary command: the streak document's table is created by
        // the first write to it, so a node that never failed a courier may not have it yet.
        using DocumentStore store = CliStore.Open(connectionString, AutoCreate.CreateOnly);
        CourierDoctorElevationSource elevation = new(
            ProcessElevationReader.ForCurrentPlatform(),
            DaemonProcess.Probe(),
            () => DaemonAutostart.ForCurrentPlatform().IsEnabled);
        await RunAsync(store, new OrchestratorProcessTableProbe(), elevation, DateTimeOffset.UtcNow, cancellationToken);
    }

    internal static async Task RunAsync(
        IDocumentStore store,
        IOrchestratorProcessProbe probe,
        CourierDoctorElevationSource? elevation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold]Courier[/]");
        try
        {
            foreach (string line in await ReadAsync(store, probe, elevation, now, cancellationToken))
            {
                AnsiConsole.MarkupLine(line);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]  Could not read the courier state ({exception.Message}).[/]");
        }

        AnsiConsole.WriteLine();
    }

    internal static async Task<IReadOnlyList<string>> ReadAsync(
        IDocumentStore store,
        IOrchestratorProcessProbe probe,
        CourierDoctorElevationSource? elevation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        if (await OrchestratorStatusCommand.ThisNodeIdAsync(session, cancellationToken) is not { } nodeId)
        {
            return [NoWindowLine];
        }

        IReadOnlyList<OrchestratorPresenceDetails> presences = await session.Query<OrchestratorPresenceDetails>()
            .Where(presence => presence.NodeId == nodeId && presence.Registered)
            .ToListAsync(cancellationToken);

        List<string> lines = [];
        bool compareElevation = elevation is { Reader.IsSupported: true };
        ProcessElevation? daemonElevation = compareElevation && elevation is { Daemon: { } daemon }
            ? elevation.Reader.Read(daemon.ProcessId)
            : null;
        if (compareElevation && daemonElevation is null && presences.Count > 0)
        {
            lines.Add(NoDaemonElevationLine);
        }

        OrchestratorFeedReader reader = new(new ReplicationProjectResolver());
        foreach (OrchestratorPresenceDetails presence in presences.OrderBy(presence => presence.ProjectId))
        {
            if (await session.LoadAsync<ProjectDetails>(presence.ProjectId, cancellationToken) is not { } project)
            {
                continue;
            }

            CourierFailureStreak? streak = await session.LoadAsync<CourierFailureStreak>(project.Id, cancellationToken);
            bool windowLive = OrchestratorLiveness.IsStillRunning(presence.ProcessId, presence.ProcessStartedAt, probe);

            CourierDoctorElevation? windowElevation = windowLive && daemonElevation is { } daemonLevel && elevation is not null
                ? new CourierDoctorElevation(elevation.Reader.Read(presence.ProcessId), daemonLevel)
                : null;

            DateTimeOffset? oldestUndrainedAt = null;
            bool feedCheckIncomplete = false;
            if (windowElevation is not { Blocked: true } && ShouldReadFeed(windowLive, presence.LaunchedAt, streak, now))
            {
                (oldestUndrainedAt, feedCheckIncomplete) = await OldestUndrainedAsync(
                    reader, session, project, now, cancellationToken);
            }

            lines.AddRange(Evaluate(
                new CourierDoctorFacts(
                    project.Name,
                    windowLive,
                    presence.LaunchedAt,
                    streak,
                    await session.LoadAsync<CourierDaySpawnCounter>(project.Id, cancellationToken),
                    oldestUndrainedAt,
                    feedCheckIncomplete,
                    windowElevation,
                    windowElevation is { Blocked: true } && elevation is not null && elevation.AutostartRegistered()),
                now));
        }

        return lines.Count == 0 ? [NoWindowLine] : lines;
    }

    /// <summary>
    /// When the oldest undrained item happened. One read scans at most
    /// <see cref="OrchestratorFeedReader.MaxEventsPerRead"/> events before filtering by project, so
    /// a capped read that admitted nothing says nothing about the events past it. This pages on
    /// from where the capped read stopped, without touching the project's real cursor, up to
    /// <see cref="MaxFeedPages"/> pages; the second value is true when it ran out of pages (or
    /// stopped making progress) before reaching the end of the log.
    /// </summary>
    private static async Task<(DateTimeOffset? OldestAt, bool Incomplete)> OldestUndrainedAsync(
        OrchestratorFeedReader reader,
        IQuerySession session,
        ProjectDetails project,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long after = await OrchestratorFeedReader.CursorAsync(session, project.Id, cancellationToken);
        for (int page = 0; page < MaxFeedPages; page++)
        {
            OrchestratorFeedRead read = await reader.ReadUndrainedAfterAsync(
                session, project.Id, project.OrchestratorFeed, CliVersion.Current, after, now, cancellationToken);
            if (read.Items.Count > 0)
            {
                return (read.Items.Min(item => item.At), false);
            }

            if (!read.ScanWasCapped)
            {
                return (null, false);
            }

            if (read.DrainableThroughSequence <= after)
            {
                return (null, true);
            }

            after = read.DrainableThroughSequence;
        }

        return (null, true);
    }

    internal const string NoDaemonElevationLine =
        "[dim]  No daemon is running, so window elevation is not compared with it.[/]";

    internal const string NoWindowLine = "[dim]  No orchestrator window is registered on this node, so there is no courier to check.[/]";

    /// <summary>
    /// Whether the undrained-item check applies at all, so the feed is only read when it could
    /// warn: a window that is live and has been registered at least an hour, for a project whose
    /// streak has not already warned.
    /// </summary>
    internal static bool ShouldReadFeed(
        bool windowLive, DateTimeOffset? launchedAt, CourierFailureStreak? streak, DateTimeOffset now) =>
        windowLive
        && launchedAt is { } launched && now - launched >= UndrainedWarningAge
        && !StreakWarns(streak, launchedAt);

    private static bool StreakWarns(CourierFailureStreak? streak, DateTimeOffset? launchedAt) =>
        streak is { Count: >= StreakWarningThreshold } && CourierFailureStreak.IsRunning(streak, launchedAt);

    /// <summary>
    /// The doctor's lines for one project, as markup: one warning per problem, or a single ok line
    /// for a healthy or quiet project. The undrained-item warning is withheld from a project the
    /// streak already warned for, since the streak is why its items are not draining.
    /// </summary>
    internal static IReadOnlyList<string> Evaluate(CourierDoctorFacts facts, DateTimeOffset now)
    {
        string name = ExternalText.OneLineMarkup(facts.ProjectName);
        List<string> warnings = [];
        string? elevationLine = facts.Elevation is { } elevation
            ? $"[dim]  {name}: window {CourierElevationCheck.Describe(elevation.Window)}, "
                + $"daemon {CourierElevationCheck.Describe(elevation.Daemon)}[/]"
            : null;

        bool elevationWarned = facts.Elevation is { Blocked: true };
        if (elevationWarned)
        {
            warnings.Add(Warn(
                $"{name}: courier {CourierElevationFix.BlockedState}; {CourierElevationFix.For(facts.AutostartRegistered)}"));
        }

        bool streakWarned = StreakWarns(facts.Streak, facts.LaunchedAt);
        if (streakWarned && facts.Streak is { } streak)
        {
            warnings.Add(Warn($"{name}: courier {ExternalText.OneLineMarkup(CourierStatusLine.FailingText(streak, now))}"));
        }

        if (CourierDaySpawnCounter.CapHeldBackOn(facts.DayCounter, DateOnly.FromDateTime(now.UtcDateTime)))
        {
            warnings.Add(Warn($"{name}: courier {ExternalText.OneLineMarkup(CourierStatusLine.DayCapText(now))}"));
        }

        if (!streakWarned
            && !elevationWarned
            && ShouldReadFeed(facts.WindowLive, facts.LaunchedAt, facts.Streak, now)
            && facts.OldestUndrainedAt is { } oldest
            && now - oldest > UndrainedWarningAge)
        {
            warnings.Add(Warn(
                $"{name}: a feed item has sat undrained for {DurationFormat.Short(now - oldest)} while this project's "
                + "orchestrator window is live; the daemon log says why no courier delivered it"));
        }

        IReadOnlyList<string> verdict = (warnings.Count, facts.FeedCheckIncomplete && !streakWarned && !elevationWarned) switch
        {
            (> 0, _) => warnings,
            (_, true) => [$"[dim]  {name}: the undrained-item check could not reach the end of a very long feed[/]"],
            _ => [$"[dim]  {name}: courier ok[/]"],
        };

        return elevationLine is null
            ? verdict
            : [elevationLine, .. verdict];
    }

    private static string Warn(string text) => $"[yellow]  warn:[/] {text}";
}
