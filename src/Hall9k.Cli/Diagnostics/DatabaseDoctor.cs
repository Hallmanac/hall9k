using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using JasperFx;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Diagnostics;

/// <summary>
/// Teaches the CLI to diagnose its own database situation, the same way forever
/// (Decisions Log #58, #73): four questions, answered in order, stopping at the first
/// one that fails. Runs on demand as <c>h9k doctor</c>, and again — automatically —
/// whenever a command that needed a database could not reach one, so the diagnosis is
/// never a raw driver exception.
/// <para>
/// Every probe here is either a raw Npgsql connection attempt (<see cref="DatabaseReachability"/>)
/// or a shell-out to <c>docker</c> (<see cref="ContainerRuntimeProbe"/>) — no Wolverine host,
/// no Marten codegen beyond the one schema-creation offer — cheap enough that running it
/// before every database-touching command survives the thin-CLI rule.
/// </para>
/// </summary>
public static class DatabaseDoctor
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Run the full check, printing teaching messages as it goes, and — when
    /// <paramref name="offerFixes"/> is set — offering to fix what it can (starting
    /// Hall9k's own Postgres, creating the schema): interactively, by asking, or — when
    /// <paramref name="assumeYes"/> is set (<c>h9k doctor --yes</c>) — without asking at
    /// all, the shape a script or a dispatched agent needs. A session that is neither
    /// interactive nor carrying <paramref name="assumeYes"/> gets a named reason for the
    /// skip and the flag to re-run with, never a silent fall-through to advice. Returns the
    /// connection string this process resolved and proved reachable, or <see langword="null"/>
    /// if it could not. A caller like <c>h9k daemon start</c> needs the string itself, not
    /// just a yes/no: the process it spawns runs from a different working directory
    /// (<c>RunPaths.Root</c>), so re-resolving there could walk up for a project override
    /// file from the wrong place and land on a different answer than the one just checked.
    /// </summary>
    public static Task<string?> RunAsync(bool offerFixes, bool assumeYes, CancellationToken cancellationToken) =>
        RunAsync(offerFixes, assumeYes, ExternalProcess.Runner, cancellationToken);

    internal static async Task<string?> RunAsync(
        bool offerFixes, bool assumeYes, ProcessRunner runner, CancellationToken cancellationToken)
    {
        ConnectionStringResolution resolution = Hall9kDatabase.Resolve();
        if (resolution.Origin == ConnectionStringOrigin.PlatformConfigFileMalformed)
        {
            AnsiConsole.MarkupLine(
                $"[red]The platform config file ({resolution.Source!.EscapeMarkup()}) exists but is not valid JSON.[/] "
                + "Fix or delete it, then run h9k doctor again — a broken file is not the same as an unconfigured "
                + "install, so the project override file underneath it in the precedence chain is never consulted "
                + "while this one stays broken.");
            return null;
        }

        if (resolution.Origin == ConnectionStringOrigin.PlatformConfigFileUnreadable)
        {
            AnsiConsole.MarkupLine(
                $"[red]The platform config file ({resolution.Source!.EscapeMarkup()}) exists but could not be read.[/] "
                + "Fix its permissions (or whatever else is holding it, e.g. another process with an exclusive "
                + "lock), then run h9k doctor again — this is not the same as invalid JSON, so deleting the file "
                + "is not the fix, and the project override file underneath it in the precedence chain is never "
                + "consulted while this one stays unreadable.");
            return null;
        }

        if (!resolution.IsConfigured)
        {
            resolution = await DiagnoseNotConfiguredAsync(offerFixes, assumeYes, runner, cancellationToken);
            if (resolution.Value is not { } configured)
            {
                return null;
            }

            return await CheckReachabilityAndSchemaAsync(configured, resolution, offerFixes, assumeYes, runner, cancellationToken);
        }

        return await CheckReachabilityAndSchemaAsync(resolution.Value, resolution, offerFixes, assumeYes, runner, cancellationToken);
    }

    /// <summary>Question 1 failed, so question 4 is what is left to say: what is available to point at.</summary>
    private static async Task<ConnectionStringResolution> DiagnoseNotConfiguredAsync(
        bool offerFixes, bool assumeYes, ProcessRunner runner, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine(
            "[yellow]No connection string is configured.[/] That is the whole problem — nothing else has been checked yet.");
        AnsiConsole.MarkupLine(
            $"[dim]Checked, in order: the {Hall9kDatabase.EnvironmentVariableName} environment variable, "
            + $"the platform config file ({Hall9kDatabase.ConfigFile.EscapeMarkup()}), and a "
            + $"{Hall9kDatabase.ProjectOverrideFileName} file walking up from "
            + $"{Directory.GetCurrentDirectory().EscapeMarkup()}.[/]");

        (ContainerRuntimeStatus runtime, bool containerConfirmed, PostgresContainerStatus container) =
            await ReportContainerRuntimeStatusAsync(runner, cancellationToken);

        if (await ContainerRuntimeProbe.PortListeningAsync("localhost", 5432, cancellationToken))
        {
            AnsiConsole.MarkupLine(
                $"[dim]Something is already listening on localhost:5432 — if that is your Postgres, point "
                + $"{Hall9kDatabase.EnvironmentVariableName} at it.[/]");
        }

        if (offerFixes && containerConfirmed && container == PostgresContainerStatus.Running)
        {
            if (await OfferAndRecordAlreadyRunningContainerAsync(assumeYes, cancellationToken) is { } recorded)
            {
                return recorded;
            }
        }
        else if (runtime == ContainerRuntimeStatus.Running && offerFixes
            && await OfferAndStartAsync(Hall9kDatabase.DefaultConnectionString, containerConfirmed, container, assumeYes, runner, cancellationToken))
        {
            await Hall9kDatabase.WriteConfiguredConnectionStringAsync(Hall9kDatabase.DefaultConnectionString, cancellationToken);
            AnsiConsole.MarkupLine($"[green]Configured[/]: wrote the connection string to {Hall9kDatabase.ConfigFile.EscapeMarkup()}.");
            return Hall9kDatabase.Resolve();
        }

        AnsiConsole.MarkupLine(
            $"[dim]Set one: export {Hall9kDatabase.EnvironmentVariableName}=\"Host=…;Port=…;Database=…;"
            + $"Username=…;Password=…\", or write {{\"connectionString\": \"…\"}} to "
            + $"{Hall9kDatabase.ConfigFile.EscapeMarkup()}.[/]");
        return ConnectionStringResolution.NotConfigured;
    }

    /// <summary>Questions 2 and 3: is it reachable, and is the schema there.</summary>
    private static async Task<string?> CheckReachabilityAndSchemaAsync(
        string connectionString,
        ConnectionStringResolution resolution,
        bool offerFixes,
        bool assumeYes,
        ProcessRunner runner,
        CancellationToken cancellationToken)
    {
        ReachabilityReport reachability = await DatabaseReachability.ProbeAsync(connectionString, cancellationToken);
        switch (reachability.Status)
        {
            case ReachabilityStatus.Reachable:
                break;

            case ReachabilityStatus.RefusedConnection:
                AnsiConsole.MarkupLine(
                    $"[yellow]Configured (from {resolution.Description.EscapeMarkup()}) to connect to "
                    + $"{reachability.Host.EscapeMarkup()}:{reachability.Port}[/], but nothing is listening there. "
                    + $"({reachability.Detail.EscapeMarkup()})");

                bool looksLocal = reachability.Host is "localhost" or "127.0.0.1" && reachability.Port == 5432;
                if (looksLocal)
                {
                    // Question 4's Docker awareness applies here too, not only to the
                    // never-configured path: the boundary is Docker itself, wherever the
                    // check finds an unreachable local Postgres (origin incident,
                    // 2026-08-21 — a machine reboot leaves the connection string configured
                    // but Docker Desktop, and so hall9k-postgres, not yet back up).
                    (ContainerRuntimeStatus runtime, bool containerConfirmed, PostgresContainerStatus container) =
                        await ReportContainerRuntimeStatusAsync(runner, cancellationToken);
                    if (offerFixes && runtime == ContainerRuntimeStatus.Running
                        && await OfferAndStartAsync(connectionString, containerConfirmed, container, assumeYes, runner, cancellationToken))
                    {
                        reachability = await DatabaseReachability.ProbeAsync(connectionString, cancellationToken);
                    }
                }

                if (reachability.Status != ReachabilityStatus.Reachable)
                {
                    AnsiConsole.MarkupLine("[dim]Is Postgres running? Start it, then try again.[/]");
                    return null;
                }

                break;

            case ReachabilityStatus.AuthenticationFailed:
                AnsiConsole.MarkupLine(
                    $"[red]Reached Postgres at {reachability.Host.EscapeMarkup()}:{reachability.Port}[/], but it "
                    + $"rejected the credentials in {resolution.Description.EscapeMarkup()}: {reachability.Detail.EscapeMarkup()}");
                AnsiConsole.MarkupLine(
                    "[dim]Check the username and password in the connection string, or rotate the credential "
                    + "and reconfigure it there.[/]");
                return null;

            case ReachabilityStatus.DatabaseMissing:
                AnsiConsole.MarkupLine(
                    $"[red]Reached Postgres at {reachability.Host.EscapeMarkup()}:{reachability.Port}[/], but the "
                    + $"database '{reachability.Database.EscapeMarkup()}' does not exist there yet. Create it, or "
                    + "point the connection string at one that does.");
                return null;

            default:
                AnsiConsole.MarkupLine(
                    $"[red]Reached Postgres at {reachability.Host.EscapeMarkup()}:{reachability.Port}[/], but it "
                    + $"reported: {reachability.Detail.EscapeMarkup()}");
                return null;
        }

        bool schemaPresent = await DatabaseReachability.SchemaPresentAsync(connectionString, cancellationToken);
        if (!schemaPresent)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Connected to {reachability.Host.EscapeMarkup()}:{reachability.Port}/{reachability.Database.EscapeMarkup()}[/] "
                + $"({resolution.Description.EscapeMarkup()}), but Hall9k's schema is not there yet. Marten creates its own tables.");
            if (offerFixes && (assumeYes
                || (AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Confirm("Shall I set that up now?", defaultValue: true))))
            {
                await ApplySchemaAsync(connectionString);
                AnsiConsole.MarkupLine("[green]Schema created.[/]");
            }
            else if (offerFixes && !AnsiConsole.Profile.Capabilities.Interactive)
            {
                // The AC-3 rule, applied here too: a skipped prompt is never silent
                // advice-only — it names why, and what to run instead of it (origin:
                // Windows install friction log item 3, which surfaced this exact
                // fall-through as "prints advice and exits nonzero silently").
                AnsiConsole.MarkupLine(
                    "[dim]Skipping — stdin is not a terminal, so there is nobody to confirm this. It will be "
                    + "created automatically the next time a command touches the database, or re-run with "
                    + "h9k doctor --yes to create it right now.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[dim]It will be created automatically the next time a command touches the database.[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[green]Postgres is healthy[/]: {reachability.Host.EscapeMarkup()}:{reachability.Port}/"
                + $"{reachability.Database.EscapeMarkup()}, resolved from {resolution.Description.EscapeMarkup()}.");
        }

        return connectionString;
    }

    /// <summary>
    /// Prints question 4's Docker awareness honestly, wherever the check needs it — the
    /// not-configured path and the configured-but-refused path both reach an unreachable
    /// local Postgres, and the boundary (Decisions Log #73) is Docker itself either way.
    /// Reports what it finds unconditionally, independent of <paramref name="runner"/>'s
    /// caller offering to fix anything: a stopped <c>hall9k-postgres</c> container is the
    /// single most useful thing this check can say, and it has to say it on every
    /// invocation — including a non-interactive <c>h9k doctor</c> and every ordinary command
    /// that falls into this diagnosis — not only when an interactive fix offer happens to
    /// follow (origin incident 2026-08-23: the report was reachable only from inside the
    /// interactive start-offer, so scripted and non-interactive runs never saw it).
    /// </summary>
    private static async Task<(ContainerRuntimeStatus Runtime, bool ContainerConfirmed, PostgresContainerStatus Container)> ReportContainerRuntimeStatusAsync(
        ProcessRunner runner, CancellationToken cancellationToken)
    {
        ContainerRuntimeStatus runtime = await ContainerRuntimeProbe.RuntimeStatusAsync(runner, cancellationToken);
        bool containerConfirmed = true;
        PostgresContainerStatus container = PostgresContainerStatus.Absent;
        switch (runtime)
        {
            case ContainerRuntimeStatus.Running:
                AnsiConsole.MarkupLine("[dim]A container runtime (Docker) is running.[/]");
                (containerConfirmed, PostgresContainerStatus status) =
                    await ContainerRuntimeProbe.Hall9kContainerStatusAsync(runner, cancellationToken);
                if (!containerConfirmed)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]Could not confirm {PostgresRuntime.ContainerName}'s status[/] — checking "
                        + "Docker (docker ps -a) itself failed. Retry once Docker is answering reliably.");
                    break;
                }

                container = status;
                if (container == PostgresContainerStatus.Stopped)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]Found a stopped {PostgresRuntime.ContainerName} container from a previous "
                        + "session[/] — your database exists, it is just not running.");
                }

                break;
            case ContainerRuntimeStatus.NotRunning:
                AnsiConsole.MarkupLine(
                    "[dim]Docker is installed but not running — that is a machine-level action, always yours: "
                    + "start Docker Desktop, then run h9k doctor again.[/]");
                break;
            case ContainerRuntimeStatus.NotInstalled:
                string installHint = OperatingSystem.IsWindows()
                    ? "a native install works just as well (winget: winget install PostgreSQL.PostgreSQL, or "
                        + "the installer at https://www.postgresql.org/download/windows/), or Docker Desktop "
                        + "(WSL 2 backend, https://www.docker.com/products/docker-desktop/) if you prefer containers"
                    : "a native install works just as well (Homebrew: brew install postgresql@18; "
                        + "apt: sudo apt install postgresql)";
                AnsiConsole.MarkupLine(
                    $"[dim]No container runtime (docker) found — Postgres does not need one: {installHint}, "
                    + "or point at one you already run elsewhere (Decisions Log #57).[/]");
                break;
        }

        return (runtime, containerConfirmed, container);
    }

    /// <summary>
    /// The not-configured path's other fix, alongside <see cref="OfferAndStartAsync"/>'s
    /// start-something shape: <see cref="PostgresContainerStatus.Running"/> means there is
    /// nothing to start, only something to point at, and <see cref="OfferAndStartAsync"/>
    /// itself refuses that case outright (restarting an already-running container is never the
    /// fix for whatever else is wrong) — which used to leave a machine with a live, confirmed
    /// <c>hall9k-postgres</c> dead-ending on "Set one: export …" advice instead (the finding
    /// this method fixes). Probes <see cref="Hall9kDatabase.DefaultConnectionString"/> directly
    /// — the container is confirmed by name, not by the connection string a caller happens to
    /// have configured, since nothing is configured yet on this path — and, if it answers,
    /// records it exactly the way <see cref="OfferAndStartAsync"/>'s own caller already does.
    /// Offer-never-force still applies: it asks before writing (or is told
    /// <paramref name="assumeYes"/> in its place), because writing the platform config file is
    /// the same kind of fix as starting a container, even though nothing here starts anything.
    /// Takes <paramref name="probe"/> rather than calling <see cref="DatabaseReachability.ProbeAsync"/>
    /// directly so a test can substitute a fake answer instead of depending on a real Postgres
    /// bound to the exact host and port <see cref="Hall9kDatabase.DefaultConnectionString"/>
    /// names.
    /// </summary>
    private static Task<ConnectionStringResolution?> OfferAndRecordAlreadyRunningContainerAsync(
        bool assumeYes, CancellationToken cancellationToken) =>
        OfferAndRecordAlreadyRunningContainerAsync(
            assumeYes,
            token => DatabaseReachability.ProbeAsync(Hall9kDatabase.DefaultConnectionString, token),
            cancellationToken);

    internal static async Task<ConnectionStringResolution?> OfferAndRecordAlreadyRunningContainerAsync(
        bool assumeYes, Func<CancellationToken, Task<ReachabilityReport>> probe, CancellationToken cancellationToken)
    {
        ReachabilityReport report = await probe(cancellationToken);
        if (report.Status != ReachabilityStatus.Reachable)
        {
            return null;
        }

        if (!assumeYes && !AnsiConsole.Profile.Capabilities.Interactive)
        {
            // Same rule as the schema and start offers: a skipped prompt names itself and the
            // flag that answers it, rather than falling through to generic advice as though
            // nothing here could have been fixed automatically (origin: Windows install
            // friction log item 3).
            AnsiConsole.MarkupLine(
                $"[dim]{PostgresRuntime.ContainerName} is already running and answering, but skipping the offer "
                + "to point at it — stdin is not a terminal, so there is nobody to confirm this. Re-run with "
                + "h9k doctor --yes to configure it automatically.[/]");
            return null;
        }

        if (!assumeYes && !AnsiConsole.Confirm(
            $"Found {PostgresRuntime.ContainerName} already running and answering. Configure h9k to use it?",
            defaultValue: true))
        {
            return null;
        }

        await Hall9kDatabase.WriteConfiguredConnectionStringAsync(Hall9kDatabase.DefaultConnectionString, cancellationToken);
        AnsiConsole.MarkupLine($"[green]Configured[/]: wrote the connection string to {Hall9kDatabase.ConfigFile.EscapeMarkup()}.");
        return Hall9kDatabase.Resolve();
    }

    /// <summary>
    /// Offer-never-force (same shape as the auto-assign prompt at publish): asks before
    /// starting anything, and only when there is something Docker can actually do — a
    /// stopped hall9k-postgres container to restart, or the shipped compose definition to
    /// bring up for the first time. Waits for readiness before reporting success, so the
    /// caller never proceeds against a database that answered "starting" and nothing more.
    /// Takes <paramref name="container"/> already resolved by <see cref="ReportContainerRuntimeStatusAsync"/>
    /// rather than re-probing: the report already ran (and already said so, when stopped)
    /// before a caller ever reaches this offer, so this method only ever decides whether to
    /// ask, never what to report. <paramref name="containerConfirmed"/> being <see langword="false"/>
    /// means that report could not tell absent from stopped (<c>docker ps -a</c> itself failed) —
    /// this sits the offer out entirely rather than guess, since picking either branch below
    /// (restart what is presumed stopped, or bring up what is presumed absent) would act on a
    /// fact nobody actually observed this pass. <paramref name="assumeYes"/> (<c>h9k doctor --yes</c>)
    /// takes the place of the interactive confirm — the offer still runs, it just is not asked —
    /// and a non-interactive session carrying neither an answer nor that flag is told exactly
    /// that, rather than skipped without a word.
    /// </summary>
    private static async Task<bool> OfferAndStartAsync(
        string connectionStringToPoll,
        bool containerConfirmed,
        PostgresContainerStatus container,
        bool assumeYes,
        ProcessRunner runner,
        CancellationToken cancellationToken)
    {
        if (!containerConfirmed)
        {
            return false;
        }

        if (container == PostgresContainerStatus.Running)
        {
            // Already running, so whatever is actually wrong, starting it again is not the fix.
            return false;
        }

        if (!assumeYes && !AnsiConsole.Profile.Capabilities.Interactive)
        {
            // Same rule as the schema offer below: a skipped prompt names itself and the
            // flag that answers it, rather than falling through to generic advice as though
            // nothing here could have been fixed automatically (origin: Windows install
            // friction log item 3).
            AnsiConsole.MarkupLine(
                "[dim]Skipping the start offer — stdin is not a terminal, so there is nobody to confirm this. "
                + "Re-run with h9k doctor --yes to start it automatically.[/]");
            return false;
        }

        if (!assumeYes)
        {
            string prompt = container == PostgresContainerStatus.Stopped
                ? "Start it now via Docker?"
                : "Postgres isn't running. Start it now via Docker?";
            if (!AnsiConsole.Confirm(prompt, defaultValue: true))
            {
                return false;
            }
        }

        if (container == PostgresContainerStatus.Stopped)
        {
            if (!await ContainerRuntimeProbe.StartStoppedContainerAsync(runner, cancellationToken))
            {
                AnsiConsole.MarkupLine(
                    $"[red]Docker could not start it[/] — check docker logs {PostgresRuntime.ContainerName}, "
                    + "or run the command by hand.");
                return false;
            }
        }
        else
        {
            (ComposeUpResult composeResult, IReadOnlyList<string> observedLegacyVolumes) =
                await ContainerRuntimeProbe.ComposeUpAsync(runner, cancellationToken);
            switch (composeResult)
            {
                case ComposeUpResult.LegacyVolumeDetected:
                    string observedVolumes = string.Join(" and ", observedLegacyVolumes);
                    AnsiConsole.MarkupLine(
                        $"[red]Not starting[/] — a volume named {observedVolumes.EscapeMarkup()} exists, and the "
                        + $"pinned {PostgresRuntime.VolumeName} volume this install's compose file points at does "
                        + "not, so this looks like data from before this install's compose name: pin that has not "
                        + $"been migrated forward yet. Bringing up a fresh container now would create a new, "
                        + $"empty {PostgresRuntime.VolumeName} volume alongside it rather than reconnect to your "
                        + "data. See docs/operations.md's Provisioning section to migrate it forward by hand, "
                        + "then run h9k doctor again.");
                    return false;
                case ComposeUpResult.LegacyVolumeCheckFailed:
                    AnsiConsole.MarkupLine(
                        $"[red]Not starting[/] — whether a volume from before this install's compose name: pin "
                        + "still exists could not be checked (docker volume ls itself failed), and bringing up a "
                        + $"fresh container now could create a new, empty {PostgresRuntime.VolumeName} volume "
                        + "beside real data this could not see to warn about. Retry once Docker is answering "
                        + "reliably.");
                    return false;
                case ComposeUpResult.Failed:
                    AnsiConsole.MarkupLine(
                        $"[red]Docker could not start it[/] — check docker logs {PostgresRuntime.ContainerName}, "
                        + "or run the command by hand.");
                    return false;
            }
        }

        AnsiConsole.Markup("[dim]Waiting for it to come up…[/]");
        bool ready = await WaitForReadinessAsync(connectionStringToPoll, cancellationToken);
        AnsiConsole.WriteLine();
        if (!ready)
        {
            AnsiConsole.MarkupLine(
                $"[red]Started, but it was not answering within {ReadinessTimeout.TotalSeconds:0}s.[/] "
                + $"Check docker logs {PostgresRuntime.ContainerName}, then try again.");
        }

        return ready;
    }

    private static Task<bool> WaitForReadinessAsync(string connectionString, CancellationToken cancellationToken) =>
        WaitForReadinessAsync(
            token => DatabaseReachability.ProbeAsync(connectionString, token),
            ReadinessTimeout,
            ReadinessPollInterval,
            TimeProvider.System,
            cancellationToken);

    /// <summary>
    /// The polling shape itself, isolated from the real Npgsql probe so it can be exercised
    /// without Docker or Postgres: a fake <paramref name="probe"/> stands in, and a shrunk
    /// <paramref name="timeout"/>/<paramref name="pollInterval"/> keeps the timeout and
    /// eventually-ready cases fast in tests rather than needing the real 30s. The deadline
    /// itself is read from <paramref name="timeProvider"/> (real wall-clock time in
    /// production) rather than <c>DateTimeOffset.UtcNow</c> directly, so a test can swap in
    /// a clock whose elapsed time is driven by call count instead of the runner's actual
    /// speed.
    /// </summary>
    internal static async Task<bool> WaitForReadinessAsync(
        Func<CancellationToken, Task<ReachabilityReport>> probe,
        TimeSpan timeout,
        TimeSpan pollInterval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = timeProvider.GetUtcNow() + timeout;
        while (timeProvider.GetUtcNow() < deadline)
        {
            if ((await probe(cancellationToken)).Status == ReachabilityStatus.Reachable)
            {
                return true;
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// The schema offer's action: Marten already creates its own tables on first real use
    /// (<c>AutoCreate.CreateOnly</c>, the mode every other store in this platform opens
    /// with) — this just makes that happen on the spot instead of on the next command, for
    /// an operator who asked the doctor "shall I set that up?" and wants to see it done.
    /// </summary>
    private static async Task ApplySchemaAsync(string connectionString)
    {
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(connectionString);
            opts.ConfigureHall9k(AutoCreate.CreateOrUpdate);
        });
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    }
}
