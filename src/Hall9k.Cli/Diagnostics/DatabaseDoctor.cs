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
    /// fix what it can (starting Hall9k's own Postgres, creating the schema). Returns
    /// whether Postgres is reachable by the time this returns, which is all a caller like
    /// <c>h9k daemon start</c> needs to decide whether it is safe to proceed.
    /// </summary>
    public static Task<bool> RunAsync(bool offerFixes, CancellationToken cancellationToken) =>
        RunAsync(offerFixes, ExternalProcess.Runner, cancellationToken);

    internal static async Task<bool> RunAsync(bool offerFixes, ProcessRunner runner, CancellationToken cancellationToken)
    {
        ConnectionStringResolution resolution = Hall9kDatabase.Resolve();
        if (resolution.Origin == ConnectionStringOrigin.PlatformConfigFileMalformed)
        {
            AnsiConsole.MarkupLine(
                $"[red]The platform config file ({resolution.Source!.EscapeMarkup()}) exists but is not valid JSON.[/] "
                + "Fix or delete it, then run h9k doctor again — a broken file is not the same as an unconfigured "
                + "install, so the project override file underneath it in the precedence chain is never consulted "
                + "while this one stays broken.");
            return false;
        }

        if (!resolution.IsConfigured)
        {
            resolution = await DiagnoseNotConfiguredAsync(offerFixes, runner, cancellationToken);
            if (resolution.Value is not { } configured)
            {
                return false;
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

        ContainerRuntimeStatus runtime = await ContainerRuntimeProbe.RuntimeStatusAsync(runner, cancellationToken);
        switch (runtime)
        {
            case ContainerRuntimeStatus.Running:
                AnsiConsole.MarkupLine("[dim]A container runtime (Docker) is running.[/]");
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

        if (await ContainerRuntimeProbe.PortListeningAsync("localhost", 5432, cancellationToken))
        {
            AnsiConsole.MarkupLine(
                $"[dim]Something is already listening on localhost:5432 — if that is your Postgres, point "
                + $"{Hall9kDatabase.EnvironmentVariableName} at it.[/]");
        }

        if (runtime == ContainerRuntimeStatus.Running && offerFixes
            && await OfferAndStartAsync(Hall9kDatabase.DefaultConnectionString, runner, cancellationToken))
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
    private static async Task<bool> CheckReachabilityAndSchemaAsync(
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

                bool looksLocal = offerFixes && reachability.Host is "localhost" or "127.0.0.1" && reachability.Port == 5432;
                if (looksLocal && await OfferAndStartAsync(connectionString, runner, cancellationToken))
                {
                    reachability = await DatabaseReachability.ProbeAsync(connectionString, cancellationToken);
                }

                if (reachability.Status != ReachabilityStatus.Reachable)
                {
                    AnsiConsole.MarkupLine("[dim]Is Postgres running? Start it, then try again.[/]");
                    return false;
                }

                break;

            case ReachabilityStatus.AuthenticationFailed:
                AnsiConsole.MarkupLine(
                    $"[red]Reached Postgres at {reachability.Host.EscapeMarkup()}:{reachability.Port}[/], but it "
                    + $"rejected the credentials in {resolution.Description.EscapeMarkup()}: {reachability.Detail.EscapeMarkup()}");
                AnsiConsole.MarkupLine(
                    "[dim]Check the username and password in the connection string, or rotate the credential "
                    + "and reconfigure it there.[/]");
                return false;

            case ReachabilityStatus.DatabaseMissing:
                AnsiConsole.MarkupLine(
                    $"[red]Reached Postgres at {reachability.Host.EscapeMarkup()}:{reachability.Port}[/], but the "
                    + $"database '{reachability.Database.EscapeMarkup()}' does not exist there yet. Create it, or "
                    + "point the connection string at one that does.");
                return false;

            default:
                AnsiConsole.MarkupLine(
                    $"[red]Reached Postgres at {reachability.Host.EscapeMarkup()}:{reachability.Port}[/], but it "
                    + $"reported: {reachability.Detail.EscapeMarkup()}");
                return false;
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

        return true;
    }

    /// <summary>
    /// Offer-never-force (same shape as the auto-assign prompt at publish): asks before
    /// starting anything, and only when there is something Docker can actually do — a
    /// stopped hall9k-postgres container to restart, or the shipped compose definition to
    /// bring up for the first time. Waits for readiness before reporting success, so the
    /// caller never proceeds against a database that answered "starting" and nothing more.
    /// </summary>
    private static async Task<bool> OfferAndStartAsync(
        string connectionStringToPoll, ProcessRunner runner, CancellationToken cancellationToken)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return false;
        }

        if (await ContainerRuntimeProbe.RuntimeStatusAsync(runner, cancellationToken) != ContainerRuntimeStatus.Running)
        {
            // Not this method's place to explain why — the caller already has, or is about
            // to (the boundary is Docker itself: not running is always the human's to fix).
            return false;
        }

        PostgresContainerStatus container = await ContainerRuntimeProbe.Hall9kContainerStatusAsync(runner, cancellationToken);
        if (container == PostgresContainerStatus.Running)
        {
            // Already running, so whatever is actually wrong, starting it again is not the fix.
            return false;
        }

        string prompt = container == PostgresContainerStatus.Stopped
            ? $"Found a stopped {PostgresRuntime.ContainerName} container from a previous session — your "
              + "database exists, it is just not running. Start it now via Docker?"
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
