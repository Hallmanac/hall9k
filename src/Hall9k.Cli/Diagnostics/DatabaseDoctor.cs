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

    /// <summary>
    /// Run the full check, printing teaching messages as it goes, and — when
    /// <paramref name="offerFixes"/> is set and the session is interactive — offering to
    /// fix what it can (starting Hall9k's own Postgres, creating the schema). Returns the
    /// connection string this process resolved and proved reachable, or <see langword="null"/>
    /// if it could not. A caller like <c>h9k daemon start</c> needs the string itself, not
    /// just a yes/no: the process it spawns runs from a different working directory
    /// (<c>RunPaths.Root</c>), so re-resolving there could walk up for a project override
    /// file from the wrong place and land on a different answer than the one just checked.
    /// </summary>
    public static Task<string?> RunAsync(bool offerFixes, CancellationToken cancellationToken) =>
        RunAsync(offerFixes, ExternalProcess.Runner, cancellationToken);

    internal static async Task<string?> RunAsync(bool offerFixes, ProcessRunner runner, CancellationToken cancellationToken)
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

        if (!resolution.IsConfigured)
        {
            resolution = await DiagnoseNotConfiguredAsync(offerFixes, runner, cancellationToken);
            if (resolution.Value is not { } configured)
            {
                return null;
            }

            return await CheckReachabilityAndSchemaAsync(configured, resolution, offerFixes, runner, cancellationToken);
        }

        return await CheckReachabilityAndSchemaAsync(resolution.Value, resolution, offerFixes, runner, cancellationToken);
    }

    /// <summary>Question 1 failed, so question 4 is what is left to say: what is available to point at.</summary>
    private static async Task<ConnectionStringResolution> DiagnoseNotConfiguredAsync(
        bool offerFixes, ProcessRunner runner, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine(
            "[yellow]No connection string is configured.[/] That is the whole problem — nothing else has been checked yet.");
        AnsiConsole.MarkupLine(
            $"[dim]Checked, in order: the {Hall9kDatabase.EnvironmentVariableName} environment variable, "
            + $"the platform config file ({Hall9kDatabase.ConfigFile.EscapeMarkup()}), and a "
            + $"{Hall9kDatabase.ProjectOverrideFileName} file walking up from "
            + $"{Directory.GetCurrentDirectory().EscapeMarkup()}.[/]");

        (ContainerRuntimeStatus runtime, PostgresContainerStatus container) =
            await ReportContainerRuntimeStatusAsync(runner, cancellationToken);

        if (await ContainerRuntimeProbe.PortListeningAsync("localhost", 5432, cancellationToken))
        {
            AnsiConsole.MarkupLine(
                $"[dim]Something is already listening on localhost:5432 — if that is your Postgres, point "
                + $"{Hall9kDatabase.EnvironmentVariableName} at it.[/]");
        }

        if (runtime == ContainerRuntimeStatus.Running && offerFixes
            && await OfferAndStartAsync(Hall9kDatabase.DefaultConnectionString, container, runner, cancellationToken))
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
                    (ContainerRuntimeStatus runtime, PostgresContainerStatus container) =
                        await ReportContainerRuntimeStatusAsync(runner, cancellationToken);
                    if (offerFixes && runtime == ContainerRuntimeStatus.Running
                        && await OfferAndStartAsync(connectionString, container, runner, cancellationToken))
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
            if (offerFixes && AnsiConsole.Profile.Capabilities.Interactive
                && AnsiConsole.Confirm("Shall I set that up now?", defaultValue: true))
            {
                await ApplySchemaAsync(connectionString);
                AnsiConsole.MarkupLine("[green]Schema created.[/]");
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
    private static async Task<(ContainerRuntimeStatus Runtime, PostgresContainerStatus Container)> ReportContainerRuntimeStatusAsync(
        ProcessRunner runner, CancellationToken cancellationToken)
    {
        ContainerRuntimeStatus runtime = await ContainerRuntimeProbe.RuntimeStatusAsync(runner, cancellationToken);
        PostgresContainerStatus container = PostgresContainerStatus.Absent;
        switch (runtime)
        {
            case ContainerRuntimeStatus.Running:
                AnsiConsole.MarkupLine("[dim]A container runtime (Docker) is running.[/]");
                container = await ContainerRuntimeProbe.Hall9kContainerStatusAsync(runner, cancellationToken);
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
                AnsiConsole.MarkupLine(
                    "[dim]No container runtime (docker) found — Postgres does not need one: a native install "
                    + "works just as well (Homebrew: brew install postgresql@18; apt: sudo apt install postgresql), "
                    + "or point at one you already run elsewhere (Decisions Log #57).[/]");
                break;
        }

        return (runtime, container);
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
    /// ask, never what to report.
    /// </summary>
    private static async Task<bool> OfferAndStartAsync(
        string connectionStringToPoll, PostgresContainerStatus container, ProcessRunner runner, CancellationToken cancellationToken)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return false;
        }

        if (container == PostgresContainerStatus.Running)
        {
            // Already running, so whatever is actually wrong, starting it again is not the fix.
            return false;
        }

        string prompt = container == PostgresContainerStatus.Stopped
            ? "Start it now via Docker?"
            : "Postgres isn't running. Start it now via Docker?";
        if (!AnsiConsole.Confirm(prompt, defaultValue: true))
        {
            return false;
        }

        bool started = container == PostgresContainerStatus.Stopped
            ? await ContainerRuntimeProbe.StartStoppedContainerAsync(runner, cancellationToken)
            : await ContainerRuntimeProbe.ComposeUpAsync(runner, cancellationToken);
        if (!started)
        {
            AnsiConsole.MarkupLine(
                $"[red]Docker could not start it[/] — check docker logs {PostgresRuntime.ContainerName}, "
                + "or run the command by hand.");
            return false;
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

    private static async Task<bool> WaitForReadinessAsync(string connectionString, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ReadinessTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await DatabaseReachability.ProbeAsync(connectionString, cancellationToken)).Status == ReachabilityStatus.Reachable)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
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
