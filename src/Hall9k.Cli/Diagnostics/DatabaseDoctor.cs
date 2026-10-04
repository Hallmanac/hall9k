using Hall9k.Cli.DaemonControl;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using JasperFx;
using Marten;
using Npgsql;
using Spectre.Console;
using Weasel.Core;

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
    /// skip and the flag to re-run with, never a silent fall-through to advice — unless
    /// <paramref name="staleSchemaRepairedByCaller"/> says the connection string is headed
    /// somewhere that repairs a stale schema itself before using it (<c>h9kd</c>'s own
    /// <see cref="EventStoreSchemaGuard.EnsureCurrentAsync"/>, which runs unconditionally
    /// before the daemon opens its own <c>CreateOnly</c> store): there, a stale-but-present
    /// schema is treated the same as the always-self-healing missing-schema case below it.
    /// Returns the connection string this process resolved and proved reachable, or
    /// <see langword="null"/> if it could not. A caller like <c>h9k daemon start</c> needs the
    /// string itself, not just a yes/no: the process it spawns runs from a different working
    /// directory (<c>RunPaths.Root</c>), so re-resolving there could walk up for a project
    /// override file from the wrong place and land on a different answer than the one just
    /// checked.
    /// <para>
    /// <paramref name="recordConnectionStringIfUnconfigured"/> is the one remediation a caller can
    /// withhold on its own (<c>h9k doctor --no-configure</c>): see
    /// <see cref="DiagnoseNotConfiguredAsync"/> for why the restart hand-off withholds it.
    /// </para>
    /// </summary>
    public static Task<string?> RunAsync(
        bool offerFixes, bool assumeYes, CancellationToken cancellationToken, bool staleSchemaRepairedByCaller = false,
        bool recordConnectionStringIfUnconfigured = true) =>
        RunAsync(
            offerFixes, assumeYes, ExternalProcess.Runner, cancellationToken, staleSchemaRepairedByCaller,
            recordConnectionStringIfUnconfigured);

    internal static Task<string?> RunAsync(
        bool offerFixes, bool assumeYes, ProcessRunner runner, CancellationToken cancellationToken,
        bool staleSchemaRepairedByCaller = false, bool recordConnectionStringIfUnconfigured = true) =>
        RunAsync(
            offerFixes, assumeYes, runner, () => DaemonProcess.ProbeBootStatus().State != DaemonBootState.NotRunning,
            cancellationToken, staleSchemaRepairedByCaller, recordConnectionStringIfUnconfigured);

    /// <summary>
    /// Same check, with the "is a daemon running right now" fact injectable — the password
    /// migration's own guard (<see cref="CheckReachabilityAndSchemaAsync(string,ConnectionStringResolution,bool,bool,ProcessRunner,Func{bool},Func{CancellationToken,Task{ReachabilityReport}},TimeSpan,TimeSpan,TimeProvider,CancellationToken,bool)"/>)
    /// needs the identical seam <see cref="CheckContainerPortBindingAsync(bool,ProcessRunner,Func{bool},Func{ConnectionStringResolution},CancellationToken)"/>
    /// already uses, for the same reason: a test asserting the migration's own guard cannot depend
    /// on a real pid file or a real daemon process.
    /// </summary>
    internal static async Task<string?> RunAsync(
        bool offerFixes, bool assumeYes, ProcessRunner runner, Func<bool> daemonRunning, CancellationToken cancellationToken,
        bool staleSchemaRepairedByCaller = false, bool recordConnectionStringIfUnconfigured = true)
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
            resolution = await DiagnoseNotConfiguredAsync(
                offerFixes, assumeYes, runner, cancellationToken, recordConnectionStringIfUnconfigured);
            if (resolution.Value is not { } configured)
            {
                return null;
            }

            return await CheckReachabilityAndSchemaAsync(
                configured, resolution, offerFixes, assumeYes, runner, daemonRunning, cancellationToken,
                staleSchemaRepairedByCaller);
        }

        return await CheckReachabilityAndSchemaAsync(
            resolution.Value, resolution, offerFixes, assumeYes, runner, daemonRunning, cancellationToken,
            staleSchemaRepairedByCaller);
    }

    /// <summary>
    /// The one doctor question that runs whether or not the four questions above find anything
    /// wrong at all (security review idea 6be68ee2, secrets-files-network finding 1): does
    /// <c>hall9k-postgres</c> publish port 5432 anywhere but <c>127.0.0.1</c>. Bare <c>"5432:5432"</c>
    /// in a compose file binds Docker to every interface, which put the container's own superuser
    /// <c>postgres</c> and its public default password on the network. Read-only until it is about
    /// to recreate: it reads what the running or stopped container was actually created with via a
    /// single <c>docker inspect</c> (<see cref="ContainerRuntimeProbe.InspectPortBindingAsync"/>),
    /// because the compose file cannot roll an existing container's binding forward on its own,
    /// since Docker only reads it again at creation. <see cref="PostgresRuntime.ComposeFile"/> is
    /// rewritten from the shipped constant only on the one branch that immediately composes from it (see below), so a check that only
    /// reports never creates or changes it. <c>h9k install</c> and <c>h9k update</c> are what refresh
    /// it on every run.
    /// <para>
    /// A node whose resolved connection string points anywhere but hall9k's own local container
    /// (<see cref="IsHall9kOwnLocalAddress"/>) is left alone entirely: no <c>docker</c> call, no
    /// write, one message saying so. That Postgres is not hall9k's to rebind or rotate, and on a
    /// machine whose docker reaches another machine's engine the name <c>hall9k-postgres</c> may be
    /// that machine's container, so inspecting it here could report or advise against the wrong one.
    /// </para>
    /// <para>
    /// A found drift is recreated automatically — <c>docker compose -f ComposeFile up -d</c>, onto
    /// the container's existing named volume — only when <paramref name="assumeYes"/> is set
    /// (<c>h9k doctor --yes</c>; this offer is never interactive, unlike every other fix in this
    /// file, because recreating a container already holding real data is a bigger action than
    /// starting or stopping one) and three facts all hold: the container mounts exactly
    /// <see cref="PostgresRuntime.VolumeName"/> and nothing else, its own compose project's
    /// <c>config_files</c> label names this exact file, and <paramref name="daemonRunning"/> says
    /// no daemon is running right now. The compose file is rewritten right before that recreate and
    /// at no other point; a write that fails is reported and the recreate is not run. Any one of
    /// those three failing means either the container was not created by this install's own compose
    /// file (a hand-created container, a different project mounting a differently-named volume —
    /// recreating that would either collide or silently orphan real data) or a daemon is still
    /// holding a connection through the very socket about to be torn down. Every other case,
    /// remediated or not, prints the exact hand commands: stop the daemon, run
    /// <c>h9k doctor --yes</c> (so the recreate goes through the path that writes the file first),
    /// start the daemon again — the same order <c>h9k update --restart</c>'s own hand-off already
    /// runs in the newly installed binary, which is what makes the guard's "no daemon running" true
    /// there in the first place (that hand-off stops the daemon as its own first step, before ever
    /// calling this doctor).
    /// </para>
    /// <para>
    /// Takes <paramref name="daemonRunning"/> and <paramref name="resolveConnectionString"/> as
    /// injected probes, the same seam <see cref="DiagnoseNotConfiguredAsync"/> already uses for
    /// <paramref name="alreadyRunningContainerProbe"/>: a test substitutes a fake answer instead of
    /// depending on a real daemon process and pid file, or on the ambient
    /// <c>HALL9K_CONNECTION_STRING</c> and <c>config.json</c>.
    /// </para>
    /// </summary>
    public static Task CheckContainerPortBindingAsync(bool assumeYes, CancellationToken cancellationToken) =>
        CheckContainerPortBindingAsync(
            assumeYes, ExternalProcess.Runner,
            () => DaemonProcess.ProbeBootStatus().State != DaemonBootState.NotRunning,
            () => Hall9kDatabase.Resolve(),
            cancellationToken);

    internal static Task CheckContainerPortBindingAsync(
        bool assumeYes, ProcessRunner runner, Func<bool> daemonRunning,
        Func<ConnectionStringResolution> resolveConnectionString, CancellationToken cancellationToken) =>
        CheckContainerPortBindingAsync(
            assumeYes, runner, daemonRunning, resolveConnectionString,
            // The compose file this method rewrites right before its recreate is already this
            // password's durable record by the time this probe is actually invoked (only after that
            // recreate), so this reads it back rather than generating a second, different one — the
            // fallback generation is defensive only, for a file that somehow vanished between that
            // write and this read.
            token => DatabaseReachability.ProbeAsync(
                Hall9kDatabase.ConnectionStringWithPassword(
                    PostgresRuntime.ReadPasswordFromComposeFile() ?? PostgresRuntime.GeneratePassword()),
                token),
            ReadinessTimeout, ReadinessPollInterval, TimeProvider.System, cancellationToken);

    /// <summary>
    /// Same check, with the post-recreate readiness poll's probe, timeout, interval and clock
    /// injectable — the same seam <see cref="OfferAndStartAsync"/>'s own readiness poll already
    /// exposes through <see cref="WaitForReadinessAsync(Func{CancellationToken,Task{ReachabilityReport}},TimeSpan,TimeSpan,TimeProvider,CancellationToken)"/>,
    /// needed here for the identical reason: a test asserting the recreate branch cannot depend on
    /// a real Postgres answering at the compose file's own recorded password within the real 30s
    /// timeout.
    /// </summary>
    internal static async Task CheckContainerPortBindingAsync(
        bool assumeYes, ProcessRunner runner, Func<bool> daemonRunning,
        Func<ConnectionStringResolution> resolveConnectionString,
        Func<CancellationToken, Task<ReachabilityReport>> readinessProbe,
        TimeSpan readinessTimeout, TimeSpan readinessPollInterval, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (resolveConnectionString().Value is { Length: > 0 } configured
            && NonLocalEndpointOf(configured) is { } remote)
        {
            // Nothing hall9k owns is on the other end, so there is nothing to inspect, rebind or
            // rotate, and a 127.0.0.1 pin would point the wrong way (a teammate's node whose
            // Postgres runs on its Mac host, 2026-09-30). No docker call either: where docker
            // reaches another machine's engine, hall9k-postgres may be that machine's container.
            AnsiConsole.MarkupLine(
                $"[yellow]This node's database is at {remote.Host.EscapeMarkup()}:{remote.Port}[/], "
                + "not hall9k's own container at 127.0.0.1:5432. hall9k does not manage that Postgres, so the loopback-only binding and the "
                + "default-password rotation do not apply to it; see docs/operations.md's \"Postgres on another "
                + "host\" section."
                + (remote.UsesShippedDefaultPassword
                    ? " That database still uses hall9k's shipped default password, which has to be rotated by hand."
                    : string.Empty));
            return;
        }

        if (await ContainerRuntimeProbe.RuntimeStatusAsync(runner, cancellationToken) != ContainerRuntimeStatus.Running)
        {
            return;
        }

        (bool containerConfirmed, PostgresContainerStatus containerStatus) =
            await ContainerRuntimeProbe.Hall9kContainerStatusAsync(runner, cancellationToken);
        if (!containerConfirmed || containerStatus == PostgresContainerStatus.Absent)
        {
            return;
        }

        (bool inspected, string? hostIp, string? configFilesLabel, IReadOnlyList<string> mountedVolumes) =
            await ContainerRuntimeProbe.InspectPortBindingAsync(runner, cancellationToken);
        if (!inspected)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Could not confirm {PostgresRuntime.ContainerName}'s port binding[/] — docker inspect "
                + "itself failed. Retry once Docker is answering reliably.");
            return;
        }

        if (hostIp is null or "127.0.0.1")
        {
            return;
        }

        // Escaped once here, rather than at each of the three places below that print it: the
        // path underneath it is HALL9K_HOME-derived and could in principle carry a literal '['
        // or ']' (a custom HALL9K_HOME, an unusual username) that would otherwise reach
        // AnsiConsole.MarkupLine as unbalanced markup instead of a literal bracket — the same
        // risk InstallCommand's own compose-file line already guards against.
        string composeUpCommand = $"docker compose -f {PostgresRuntime.ComposeFile} up -d".EscapeMarkup();
        AnsiConsole.MarkupLine(
            $"[red]{PostgresRuntime.ContainerName} publishes port 5432 on {hostIp.EscapeMarkup()}, not 127.0.0.1[/] "
            + "— anything on the network can reach hall9k's own Postgres, with its known default credentials, "
            + "over this. The running container keeps that binding until it is recreated, whatever the "
            + "compose file now says.");

        bool mountsExactlyPinnedVolume = mountedVolumes.Count == 1
            && string.Equals(mountedVolumes[0], PostgresRuntime.VolumeName, StringComparison.Ordinal);
        bool labelNamesThisComposeFile = configFilesLabel is not null && ComposeConfigFileMatches(configFilesLabel);

        if (assumeYes && mountsExactlyPinnedVolume && labelNamesThisComposeFile && !daemonRunning())
        {
            // The one write this check makes, right before the command that composes from the file,
            // so an install that predates the loopback-only pin, or one whose local copy was
            // hand-edited, still recreates onto the pinned binding.
            try
            {
                await PostgresRuntime.WriteComposeFileAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Could not rewrite {PostgresRuntime.ComposeFile.EscapeMarkup()}[/] "
                    + $"({exception.Message.EscapeMarkup()}), so {PostgresRuntime.ContainerName} was not recreated. "
                    + "Fix that and run h9k doctor --yes again.");
                return;
            }

            AnsiConsole.MarkupLine($"[dim]Rewrote the compose file; recreating it now: {composeUpCommand}[/]");
            bool recreated = await ContainerRuntimeProbe.RecreateFromComposeAsync(runner, cancellationToken);
            if (!recreated)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Docker could not recreate it[/] — check docker logs {PostgresRuntime.ContainerName}, "
                        + "or run the command by hand.");
                return;
            }

            // docker compose up -d returns once the container has started, not once Postgres
            // inside it is accepting connections — the very next question this doctor asks
            // (or the caller it just returned to) tries the database once, with no retry of its
            // own, and a container still booting reads as either a refused connection or "the
            // database system is starting up" (cycle-1 pre-PR review, both lenses).
            AnsiConsole.Markup("[dim]Waiting for it to come up…[/]");
            bool ready = await WaitForReadinessAsync(
                readinessProbe, readinessTimeout, readinessPollInterval, timeProvider, cancellationToken);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(ready
                ? $"[green]Recreated[/] — {PostgresRuntime.ContainerName} now publishes 5432 on 127.0.0.1 only."
                : $"[red]Recreated, but it was not answering within {readinessTimeout.TotalSeconds:0}s.[/] "
                    + $"Check docker logs {PostgresRuntime.ContainerName}, then try again.");
            return;
        }

        // Only a mismatched volume or a mismatched compose label makes a hand recreate unsafe —
        // either means this container was not created by this exact install's own compose file,
        // so running that compose file's "up -d" now could create a fresh, empty pinned volume
        // alongside real data sitting under a different name, or collide with a different
        // compose project's own container of the same name (cycle-1 pre-PR review, adversarial
        // lens). Naming which guard actually failed, and never printing the recreate command for
        // either of those two cases, is what makes the remaining "safe to hand-recreate" message
        // below actually true every time it prints.
        if (!mountsExactlyPinnedVolume)
        {
            string observedVolumes = mountedVolumes.Count == 0 ? "no named volume" : string.Join(", ", mountedVolumes);
            AnsiConsole.MarkupLine(
                $"[dim]Not recreating it automatically — {PostgresRuntime.ContainerName} does not mount exactly "
                + $"the pinned {PostgresRuntime.VolumeName} volume (it mounts: {observedVolumes.EscapeMarkup()}). "
                + $"Recreating it from {PostgresRuntime.ComposeFile.EscapeMarkup()} now would either create a "
                + $"fresh, empty {PostgresRuntime.VolumeName} volume alongside this data, or collide with a "
                + "different install's container of the same name. See docs/operations.md's Provisioning "
                + "section to migrate the volume forward by hand, then run h9k doctor again.[/]");
            return;
        }

        if (!labelNamesThisComposeFile)
        {
            AnsiConsole.MarkupLine(
                $"[dim]Not recreating it automatically — {PostgresRuntime.ContainerName} was created from a "
                + $"different compose file ({(configFilesLabel ?? "none recorded").EscapeMarkup()}), not "
                + $"{PostgresRuntime.ComposeFile.EscapeMarkup()}. Recreating from this compose file now could "
                + "fail outright with a name conflict, or act on a container this install does not actually "
                + "own. See docs/operations.md's Provisioning section, then run h9k doctor again once that is "
                + "resolved.[/]");
            return;
        }

        List<string> blockedBy = [];
        if (!assumeYes)
        {
            blockedBy.Add("--yes was not given");
        }

        if (daemonRunning())
        {
            blockedBy.Add("a daemon is running, and a live connection would be caught mid-swap");
        }

        AnsiConsole.MarkupLine(
            $"[dim]Not recreating it automatically — {string.Join(" and ", blockedBy)}. Recreate it by hand once "
            + "that is dealt with:[/]\n"
            + "  h9k daemon stop\n"
            + "  h9k doctor --yes\n"
            + "  h9k daemon start");
    }

    /// <summary>
    /// Whether an endpoint is hall9k's own local container: host <c>localhost</c> or
    /// <c>127.0.0.1</c> on port 5432. The one test every "is this Postgres ours to start or
    /// rebind" question in this file asks.
    /// </summary>
    internal static bool IsHall9kOwnLocalAddress(string host, int port) =>
        host is "localhost" or "127.0.0.1" && port == 5432;

    /// <summary>
    /// The endpoint a connection string points at, when it is not hall9k's own local container,
    /// with whether its password is the shipped <see cref="Hall9kDatabase.LegacyPassword"/>; null
    /// for a local endpoint and for a string that does not parse, which stays today's local case
    /// rather than an exception.
    /// </summary>
    private static (string Host, int Port, bool UsesShippedDefaultPassword)? NonLocalEndpointOf(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return null;
        }

        (string host, int port) = DatabaseReachability.EndpointOf(builder);
        return IsHall9kOwnLocalAddress(host, port)
            ? null
            : (host, port, string.Equals(builder.Password, Hall9kDatabase.LegacyPassword, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether a container's own <c>com.docker.compose.project.config_files</c> label names
    /// exactly <see cref="PostgresRuntime.ComposeFile"/> — the label is the absolute path Compose
    /// was invoked with, so this compares full paths rather than raw strings, and follows the
    /// platform's own case rule (Windows paths compare case-insensitively; every other platform
    /// this ships on does not) rather than assuming either. A label that is not a fully qualified
    /// path on this platform never matches: resolving it would turn another machine's path (a
    /// <c>/</c>-rooted one on Windows) into this install's own.
    /// </summary>
    private static bool ComposeConfigFileMatches(string configFilesLabel)
    {
        if (!Path.IsPathFullyQualified(configFilesLabel))
        {
            return false;
        }

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(configFilesLabel), Path.GetFullPath(PostgresRuntime.ComposeFile), comparison);
    }

    /// <summary>
    /// Question 1 failed, so question 4 is what is left to say: what is available to point at.
    /// Takes <paramref name="alreadyRunningContainerProbe"/> rather than probing the freshly
    /// generated-or-recovered default connection string directly so a test can exercise this
    /// routing decision — and confirm it actually records the connection string, not merely that
    /// it avoids a docker mutation — with a fake answer instead of a real Postgres bound to the
    /// exact host and port that string names; <see langword="null"/> (every production caller)
    /// probes for real.
    /// <para>
    /// Both fixes on this path end in the same write — the connection string built from
    /// <see cref="PostgresRuntime.GeneratePassword"/> (or whatever <see cref="PostgresRuntime.WriteComposeFileAsync(CancellationToken)"/>
    /// finds already in effect) recorded in the platform config file — and
    /// <paramref name="recordConnectionString"/> is how a caller withholds it
    /// (<c>h9k doctor --no-configure</c>). The restart hand-off
    /// (<see cref="Hall9k.Cli.Installation.DaemonRestartHandoff"/>) does exactly that, for the reason
    /// Decisions Log #118 gives for <c>h9k update</c> never making this write itself: nothing
    /// resolving <em>here</em> does not mean nothing is configured, only that this shell cannot see
    /// it, and a daemon named by <c>HALL9K_CONNECTION_STRING</c> in the shell that started it — or by
    /// a project override file somewhere else on disk — would be brought back up against a different
    /// database, with the guessed default outranking both from then on (cycle-1 pre-PR review,
    /// adversarial lens). The rest of <c>--yes</c>, including starting a stopped container for an
    /// address that <em>is</em> configured, is untouched by this.
    /// </para>
    /// </summary>
    internal static async Task<ConnectionStringResolution> DiagnoseNotConfiguredAsync(
        bool offerFixes, bool assumeYes, ProcessRunner runner,
        CancellationToken cancellationToken,
        bool recordConnectionString = true,
        Func<CancellationToken, Task<ReachabilityReport>>? alreadyRunningContainerProbe = null)
    {
        AnsiConsole.MarkupLine(
            "[yellow]No connection string is configured.[/] That is the whole problem — nothing else has been checked yet.");
        AnsiConsole.MarkupLine(
            $"[dim]Checked, in order: the {Hall9kDatabase.EnvironmentVariableName} environment variable, "
            + $"the platform config file ({Hall9kDatabase.ConfigFile.EscapeMarkup()}), and a "
            + $"{Hall9kDatabase.ProjectOverrideFileName} file walking up from "
            + $"{Directory.GetCurrentDirectory().EscapeMarkup()}.[/]");

        (ContainerRuntimeStatus runtime, bool containerConfirmed, PostgresContainerStatus container) =
            await ReportContainerRuntimeStatusAsync(runner, connectionStringAlreadyConfigured: false, offerFixes, cancellationToken);

        if (await ContainerRuntimeProbe.PortListeningAsync("127.0.0.1", 5432, cancellationToken))
        {
            AnsiConsole.MarkupLine(
                $"[dim]Something is already listening on 127.0.0.1:5432 — if that is your Postgres, point "
                + $"{Hall9kDatabase.EnvironmentVariableName} at it.[/]");
        }

        // Both remediations below end in the same write, so one flag withholds both, and a
        // withheld fix names itself rather than looking like a machine with nothing to offer —
        // the same rule the skipped prompts elsewhere in this file follow.
        bool offerToRecord = offerFixes && recordConnectionString;
        if (offerFixes && !recordConnectionString)
        {
            AnsiConsole.MarkupLine(
                $"[dim]Not recording one either: this run was asked not to (h9k doctor --no-configure), because "
                + $"nothing resolving here does not mean nothing is configured — a {Hall9kDatabase.EnvironmentVariableName} "
                + $"set in another shell, or a {Hall9kDatabase.ProjectOverrideFileName} under a directory this run never "
                + $"looked in, is invisible from here and would be outranked forever by a default written now. Set "
                + $"{Hall9kDatabase.EnvironmentVariableName} where this can see it, or run h9k doctor --yes yourself to "
                + "record the default deliberately.[/]");
        }

        if (offerToRecord && containerConfirmed && container == PostgresContainerStatus.Running)
        {
            string defaultConnectionString = Hall9kDatabase.ConnectionStringWithPassword(
                await PostgresRuntime.WriteComposeFileAsync(cancellationToken));
            Func<CancellationToken, Task<ReachabilityReport>> probe = alreadyRunningContainerProbe
                ?? (token => DatabaseReachability.ProbeAsync(defaultConnectionString, token));
            if (await OfferAndRecordAlreadyRunningContainerAsync(defaultConnectionString, assumeYes, probe, cancellationToken) is { } recorded)
            {
                return recorded;
            }
        }
        else if (runtime == ContainerRuntimeStatus.Running && offerToRecord)
        {
            (bool started, string usedConnectionString) = await OfferAndStartAsync(
                connectionStringToPoll: null, containerConfirmed, container, assumeYes, runner, cancellationToken);
            if (started)
            {
                await Hall9kDatabase.WriteConfiguredConnectionStringAsync(usedConnectionString, cancellationToken);
                AnsiConsole.MarkupLine($"[green]Configured[/]: wrote the connection string to {Hall9kDatabase.ConfigFile.EscapeMarkup()}.");
                return Hall9kDatabase.Resolve();
            }
        }

        AnsiConsole.MarkupLine(
            $"[dim]Set one: export {Hall9kDatabase.EnvironmentVariableName}=\"Host=…;Port=…;Database=…;"
            + $"Username=…;Password=…\", or write {{\"connectionString\": \"…\"}} to "
            + $"{Hall9kDatabase.ConfigFile.EscapeMarkup()}.[/]");
        return ConnectionStringResolution.NotConfigured;
    }

    /// <summary>Questions 2 and 3: is it reachable, and is the schema there.</summary>
    private static Task<string?> CheckReachabilityAndSchemaAsync(
        string connectionString,
        ConnectionStringResolution resolution,
        bool offerFixes,
        bool assumeYes,
        ProcessRunner runner,
        Func<bool> daemonRunning,
        CancellationToken cancellationToken,
        bool staleSchemaRepairedByCaller) =>
        CheckReachabilityAndSchemaAsync(
            connectionString, resolution, offerFixes, assumeYes, runner, daemonRunning,
            token => DatabaseReachability.ProbeAsync(connectionString, token),
            ReadinessTimeout, ReadinessPollInterval, TimeProvider.System, cancellationToken, staleSchemaRepairedByCaller);

    /// <summary>
    /// Same check, with the reachability probe and the post-refusal retry's timeout, poll
    /// interval and clock all injectable — the seam a test needs to prove the
    /// <see cref="ReachabilityStatus.RefusedConnection"/> case in
    /// <see cref="DiagnoseRefusedConnectionAsync"/> is retried with a bounded wait rather than
    /// failed on a single sample, without a real Postgres or a real wait: field reports
    /// 2026-09-27 (Mac and Windows) show the guarded recreate's own readiness wait observing one
    /// clean answer, immediately followed by this method's own first probe — a fresh connection,
    /// moments later — catching Postgres transiently dropping it while it finishes starting.
    /// </summary>
    internal static async Task<string?> CheckReachabilityAndSchemaAsync(
        string connectionString,
        ConnectionStringResolution resolution,
        bool offerFixes,
        bool assumeYes,
        ProcessRunner runner,
        Func<bool> daemonRunning,
        Func<CancellationToken, Task<ReachabilityReport>> reachabilityProbe,
        TimeSpan readinessTimeout,
        TimeSpan readinessPollInterval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        bool staleSchemaRepairedByCaller)
    {
        ReachabilityReport reachability = await reachabilityProbe(cancellationToken);
        switch (reachability.Status)
        {
            case ReachabilityStatus.Reachable:
                break;

            case ReachabilityStatus.RefusedConnection:
                reachability = await DiagnoseRefusedConnectionAsync(
                    reachability, resolution, connectionString, offerFixes, assumeYes, runner, reachabilityProbe,
                    readinessTimeout, readinessPollInterval, timeProvider, cancellationToken);
                if (reachability.Status != ReachabilityStatus.Reachable)
                {
                    ReportUnreachable(reachability, resolution);
                    return null;
                }

                break;

            case ReachabilityStatus.AuthenticationFailed:
            case ReachabilityStatus.DatabaseMissing:
                ReportUnreachable(reachability, resolution);
                return null;

            default:
                reachability = await DiagnoseOtherErrorAsync(
                    reachability, offerFixes, runner, reachabilityProbe, readinessTimeout, readinessPollInterval,
                    timeProvider, cancellationToken);
                if (reachability.Status != ReachabilityStatus.Reachable)
                {
                    ReportUnreachable(reachability, resolution);
                    return null;
                }

                break;
        }

        if (offerFixes)
        {
            (string migratedConnectionString, ConnectionStringResolution migratedResolution, bool abort) =
                await MaybeMigrateLegacyPasswordAsync(connectionString, resolution, assumeYes, runner, daemonRunning, cancellationToken);
            if (abort)
            {
                return null;
            }

            connectionString = migratedConnectionString;
            resolution = migratedResolution;
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
        else if (!await SchemaCurrentAsync(connectionString, cancellationToken))
        {
            // The schema exists but no longer matches what this build configures — the shape
            // every schema change before event stamping (idea 202383dc) never produced, since
            // each one only ever added a new table, and CreateOnly (every store this platform
            // opens with ordinarily) happily creates what is missing. Adding metadata headers to
            // the existing mt_events table is the first change that alters an object already
            // there, which CreateOnly refuses outright — an install whose schema predates that
            // change would otherwise crash raw on its very first write after updating, a
            // directory this question exists to walk past instead.
            AnsiConsole.MarkupLine(
                $"[yellow]Connected to {reachability.Host.EscapeMarkup()}:{reachability.Port}/{reachability.Database.EscapeMarkup()}[/] "
                + $"({resolution.Description.EscapeMarkup()}), but Hall9k's schema there predates this build.");
            if (offerFixes && (assumeYes
                || (AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Confirm("Shall I update it now?", defaultValue: true))))
            {
                await ApplySchemaAsync(connectionString);
                AnsiConsole.MarkupLine("[green]Schema updated.[/]");
            }
            else if (offerFixes && !AnsiConsole.Profile.Capabilities.Interactive && staleSchemaRepairedByCaller)
            {
                // DaemonLifecycle.StartAsync passes true here: the process it is about to spawn
                // is h9kd itself, and h9kd's own entry point runs
                // EventStoreSchemaGuard.EnsureCurrentAsync unconditionally, with no prompt, before
                // it ever opens its own CreateOnly store — so an Update-shaped difference left
                // unfixed here is not fatal to that caller the way it is to one (h9k doctor,
                // an ordinary command's own CreateOnly store) that will use the connection string
                // directly (cycle-1 pre-PR review, adversarial lens: refusing here regardless of
                // caller left a non-interactive h9k daemon start / h9k update --restart unable to
                // ever bring the daemon back up after an upgrade, even though launching it would
                // have fixed the schema on its own).
                AnsiConsole.MarkupLine(
                    "[dim]Stdin is not a terminal, so there is nobody to confirm this — but the process this "
                    + "connection string is headed to repairs its own schema at startup, so this is not fatal "
                    + "here.[/]");
            }
            else if (offerFixes && !AnsiConsole.Profile.Capabilities.Interactive)
            {
                // Unlike the schema-missing branch above, CreateOnly does not fall through to
                // fixing this on the next command that touches the database — it throws outright
                // for an Update-shaped difference — so a caller like h9k doctor itself, which hands
                // this string to nothing that repairs it downstream, must be told this one is not
                // usable rather than have it returned as though it were.
                AnsiConsole.MarkupLine(
                    "[dim]Skipping — stdin is not a terminal, so there is nobody to confirm this. Re-run with "
                    + "h9k doctor --yes to update it right now.[/]");
                return null;
            }
            else
            {
                AnsiConsole.MarkupLine(
                    "[dim]Run h9k doctor --yes to update it — until then, the next command that touches the "
                    + "database will fail.[/]");
                return null;
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
    /// The password migration (security review idea 6be68ee2, secrets-files-network findings 1, 2,
    /// 9, superseding Decisions Log #118): an existing install whose <c>config.json</c> still names
    /// the shipped default gets moved onto a generated one, over this doctor's own already-reachable
    /// connection, in one transaction — <c>ALTER ROLE</c> with a precomputed SCRAM-SHA-256 verifier
    /// (never a cleartext literal: the live server logs a failed statement's own text at
    /// <c>log_min_error_statement=error</c>), then <c>config.json</c> written atomically, then
    /// <c>COMMIT</c>. Runs only from <see cref="CheckReachabilityAndSchemaAsync(string,ConnectionStringResolution,bool,bool,ProcessRunner,Func{bool},Func{CancellationToken,Task{ReachabilityReport}},TimeSpan,TimeSpan,TimeProvider,CancellationToken,bool)"/>'s
    /// own Reachable path, and only when every one of these holds: <c>config.json</c>'s own
    /// connection string is exactly the old default (a hand-set string, or one already migrated, is
    /// never touched — <see cref="Hall9kDatabase.IsLegacyDefaultConnectionString"/>);
    /// <see cref="Hall9kDatabase.EnvironmentVariableName"/> is not set (it would silently outrank the
    /// write this is about to make); no daemon is running (a live connection would be caught
    /// mid-rotation); and the container passes the same ownership guard task 359e0d7a's guarded
    /// recreate already uses (mounts exactly <see cref="PostgresRuntime.VolumeName"/>, and its own
    /// compose <c>config_files</c> label names the installed compose file) — the same reasons that
    /// guard exists apply here unchanged. Never runs unless <paramref name="assumeYes"/>
    /// (<c>h9k doctor --yes</c>): this mutates a real credential, the same bar the guarded recreate
    /// already sets for a comparable action. <c>h9k doctor --no-configure</c> does not withhold
    /// this: that flag only ever meant "do not guess a connection string when nothing resolves",
    /// which has nothing to say about a connection string that already resolves to the value this
    /// migration exists to move off of.
    /// <para>
    /// The three-way failure story: a failed <c>ALTER ROLE</c> or a failed <c>config.json</c> write
    /// rolls the transaction back and reports nothing changed — the migration is simply retried on
    /// the next <c>h9k doctor --yes</c>. A failed or canceled <c>COMMIT</c> is genuinely ambiguous
    /// (the server may have applied it despite the client never seeing the acknowledgement, and a
    /// cancellation landing on that same await is no different), so this never claims nothing
    /// changed there: the recovery restores <c>config.json</c>'s own previous bytes, held in memory
    /// since before the write, back to what they were, and prints the recovery command regardless —
    /// forcing the role back to the already-public old password (<see cref="Hall9kDatabase.LegacyPassword"/>),
    /// never the new one — a known, boring target reachable over the container's own trusted local
    /// socket regardless of whether the ambiguous commit actually landed, rather than a guess at
    /// whatever the new password might now be. Only when the restore itself also fails does the
    /// message additionally warn that <c>config.json</c> may now disagree with the database.
    /// </para>
    /// <para>Returns the connection string and resolution the rest of this check should use —
    /// migrated, or unchanged when nothing here applied or nothing here could safely proceed — and
    /// <c>Abort</c>, set only in the one case above where continuing with either connection string
    /// would be acting on a guess rather than an observed fact.</para>
    /// </summary>
    internal static async Task<(string ConnectionString, ConnectionStringResolution Resolution, bool Abort)> MaybeMigrateLegacyPasswordAsync(
        string connectionString,
        ConnectionStringResolution resolution,
        bool assumeYes,
        ProcessRunner runner,
        Func<bool> daemonRunning,
        CancellationToken cancellationToken)
    {
        (ConfigFileConnectionStringState state, string? configuredValue) = Hall9kDatabase.ConnectionStringStateAndValueInConfigFile();
        if (state != ConfigFileConnectionStringState.Supplied || !Hall9kDatabase.IsLegacyDefaultConnectionString(configuredValue))
        {
            // Not this migration's business: a hand-set connection string, one already migrated, or
            // a config file this doctor already reported broken elsewhere. Silent — reporting a
            // rotation opportunity on every healthy, already-configured machine would be noise.
            return (connectionString, resolution, false);
        }

        if (resolution.Origin == ConnectionStringOrigin.EnvironmentVariable)
        {
            // resolution.Origin, not a direct Environment.GetEnvironmentVariable read: this is what
            // the process actually resolved against (it outranks the config file), so a config file
            // still naming the old default while the environment variable points elsewhere means
            // this migration would rotate a credential nothing here is even using. Unlike the
            // branch above, this is worth telling the operator about — a config file stuck on the
            // shipped default indefinitely, with no message ever explaining why, is exactly what an
            // operator with a stray HALL9K_CONNECTION_STRING in their shell profile would otherwise
            // never notice (conformance pre-PR review, cycle 1).
            AnsiConsole.MarkupLine(
                $"[dim]{PostgresRuntime.ContainerName} is still using the shipped default password — not rotating "
                + $"it automatically because {Hall9kDatabase.EnvironmentVariableName} is set, and this process "
                + $"resolved its connection string from that variable rather than from {Hall9kDatabase.ConfigFile.EscapeMarkup()}. "
                + $"Unset {Hall9kDatabase.EnvironmentVariableName} (or point it at the rotated credential yourself) "
                + "if you want this migration to run.[/]");
            return (connectionString, resolution, false);
        }

        if (resolution.Origin != ConnectionStringOrigin.PlatformConfigFile)
        {
            // Silent, the same as the eligibility check above: Configured (Aspire's dev-loop
            // wiring), TestOverride, and ProjectOverride all resolve connectionString from
            // somewhere config.json never named, so config.json's own legacy default just
            // detected above describes a server this run is not even talking to. Proceeding
            // regardless would rotate (or, for the ownership guard's docker inspect, merely
            // probe) whatever connectionString actually points at, then overwrite config.json
            // with that unrelated server's address — the wrong fix for a config file that was
            // never in play here (a test's own throwaway database was the shape that surfaced
            // this: DatabaseDoctorTests.Assume_yes_creates_the_schema_without_asking runs against
            // a fixture database while a real, unmigrated config.json happened to sit on the same
            // host).
            return (connectionString, resolution, false);
        }

        List<string> blockedBy = [];
        if (!assumeYes)
        {
            blockedBy.Add("--yes was not given");
        }

        if (daemonRunning())
        {
            blockedBy.Add("a daemon is running, and a live connection would be caught mid-rotation");
        }

        (bool inspected, string? _, string? configFilesLabel, IReadOnlyList<string> mountedVolumes) =
            await ContainerRuntimeProbe.InspectPortBindingAsync(runner, cancellationToken);
        if (!inspected)
        {
            blockedBy.Add($"{PostgresRuntime.ContainerName} could not be inspected to confirm rotating it is safe");
        }
        else if (mountedVolumes.Count != 1 || !string.Equals(mountedVolumes[0], PostgresRuntime.VolumeName, StringComparison.Ordinal))
        {
            blockedBy.Add($"{PostgresRuntime.ContainerName} does not mount exactly the pinned {PostgresRuntime.VolumeName} volume");
        }
        else if (configFilesLabel is null || !ComposeConfigFileMatches(configFilesLabel))
        {
            blockedBy.Add($"{PostgresRuntime.ContainerName} was not created from {PostgresRuntime.ComposeFile}");
        }

        if (blockedBy.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]{PostgresRuntime.ContainerName} is still using the shipped default password — not rotating "
                + $"it automatically because {string.Join(" and ", blockedBy)}. Migrate it by hand once that is "
                + "dealt with:[/]\n"
                + "  h9k daemon stop\n"
                + "  h9k doctor --yes\n"
                + "  h9k daemon start");
            return (connectionString, resolution, false);
        }

        string newPassword = PostgresRuntime.GeneratePassword();
        // Built from connectionString's own host, port, database and username — never
        // Hall9kDatabase.ConnectionStringWithPassword's hardcoded 127.0.0.1:5432 — because the
        // eligibility check above only guarantees config.json's value equals the legacy default
        // string, not that connectionString (a separate parameter) was resolved from that exact
        // string rather than, say, a test or a future caller pointing this at a differently
        // addressed Postgres. Swapping just the password preserves everything else this doctor
        // already proved reachable.
        string newConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Password = newPassword }.ConnectionString;
        string verifier = ScramSha256PasswordVerifier.Build(newPassword);
        string previousConfigBytes = File.Exists(Hall9kDatabase.ConfigFile)
            ? await File.ReadAllTextAsync(Hall9kDatabase.ConfigFile, cancellationToken)
            : string.Empty;

        NpgsqlConnectionStringBuilder connectingBuilder = new(connectionString) { Pooling = false };
        await using NpgsqlConnection connection = new(connectingBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // The verifier, never the plaintext password: Postgres recognises a value already
            // shaped like SCRAM-SHA-256$… and stores it as-is instead of re-encrypting it, which is
            // what keeps the generated password itself off the wire and out of the server's own
            // statement log for this command (security review idea 6be68ee2, secrets-files-network
            // finding 1). A single quote can never occur in this shape, but the doubled-quote escape
            // costs nothing and removes the question.
            await using NpgsqlCommand alter = new(
                $"ALTER ROLE postgres WITH PASSWORD '{verifier.Replace("'", "''", StringComparison.Ordinal)}'",
                connection, transaction);
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (NpgsqlException exception)
        {
            await TryRollbackAsync(transaction, cancellationToken);
            AnsiConsole.MarkupLine(
                $"[red]Could not rotate {PostgresRuntime.ContainerName}'s password[/]: the ALTER ROLE itself "
                + $"failed ({exception.Message.EscapeMarkup()}) — nothing changed. Re-run h9k doctor --yes once "
                + "that is fixed.");
            return (connectionString, resolution, false);
        }

        try
        {
            await Hall9kDatabase.WriteConfiguredConnectionStringAsync(newConnectionString, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await TryRollbackAsync(transaction, cancellationToken);
            AnsiConsole.MarkupLine(
                $"[red]Could not rotate {PostgresRuntime.ContainerName}'s password[/]: writing "
                + $"{Hall9kDatabase.ConfigFile.EscapeMarkup()} failed ({exception.Message.EscapeMarkup()}) — the "
                + "database change was rolled back, so nothing changed. Re-run h9k doctor --yes once that is "
                + "fixed.");
            return (connectionString, resolution, false);
        }

        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or OperationCanceledException)
        {
            // OperationCanceledException belongs here alongside NpgsqlException: a cancellation
            // (Ctrl+C) landing right on this await is exactly as ambiguous about whether the server
            // applied the commit as a dropped connection is (adversarial pre-PR review, cycle 1),
            // and the recovery this branch already has to offer for that ambiguity is identical
            // either way. The restore write below deliberately uses CancellationToken.None rather
            // than the (possibly already-canceled) cancellationToken — a canceled token would make
            // the restore itself throw immediately, leaving config.json holding a password the
            // database may never have adopted with no attempt made to put it back.
            string recoveryCommand =
                $"docker exec -i {PostgresRuntime.ContainerName} psql -U postgres -c \"ALTER ROLE postgres WITH "
                + $"PASSWORD '{Hall9kDatabase.LegacyPassword}'\"";
            try
            {
                await AtomicFileWrite.WriteAllTextAsync(Hall9kDatabase.ConfigFile, previousConfigBytes, CancellationToken.None);
                AnsiConsole.MarkupLine(
                    $"[red]Could not rotate {PostgresRuntime.ContainerName}'s password[/]: committing the change "
                    + $"could not be confirmed ({exception.Message.EscapeMarkup()}) — the server may already have "
                    + $"applied it, so this cannot claim nothing changed. {Hall9kDatabase.ConfigFile.EscapeMarkup()} "
                    + "has been restored to its previous contents (the old password), but if the database already "
                    + "adopted the new one, that old password will no longer authenticate. If h9k doctor --yes then "
                    + $"fails with a wrong-password error, force the role back to the known, already-public old "
                    + $"password over the container's own trusted local socket first: {recoveryCommand} — then "
                    + "re-run h9k doctor --yes.");
                return (connectionString, resolution, false);
            }
            catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Could not rotate {PostgresRuntime.ContainerName}'s password, and could not restore "
                    + $"{Hall9kDatabase.ConfigFile.EscapeMarkup()} either[/] ({restoreException.Message.EscapeMarkup()}) "
                    + "— whether the database actually kept the new password is now uncertain, and this config "
                    + $"file no longer necessarily matches it. Recover by hand: force the role back to the known, "
                    + $"already-public old password over the container's own trusted local socket — {recoveryCommand} "
                    + $"— then fix {Hall9kDatabase.ConfigFile.EscapeMarkup()} by hand to match.");
                return (connectionString, resolution, true);
            }
        }

        await PostgresRuntime.WriteComposeFileAsync(newPassword, cancellationToken);
        AnsiConsole.MarkupLine(
            $"[green]Rotated[/]: {PostgresRuntime.ContainerName}'s password is no longer the shipped default.");
        return (newConnectionString, Hall9kDatabase.Resolve(), false);
    }

    private static async Task TryRollbackAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (NpgsqlException)
        {
            // The connection may already be dead (which is often why the preceding command threw
            // in the first place) — Postgres itself never commits an unfinished transaction on a
            // dropped connection, so there is nothing left here for a rollback to actually undo.
        }
    }

    /// <summary>
    /// The terminal diagnosis for a <see cref="ReachabilityReport"/> that never turned
    /// <see cref="ReachabilityStatus.Reachable"/> — shared by the immediate-probe cases above and
    /// by the two retried cases (<see cref="DiagnoseRefusedConnectionAsync"/>,
    /// <see cref="DiagnoseOtherErrorAsync"/>), so a status that only emerges partway through a
    /// bounded retry — Postgres answering "wrong password" or "no such database" after a few
    /// seconds of "still starting up" — gets the same specific message a first-probe answer of
    /// that same status already gets, rather than the generic detail dump or the retry's own
    /// "not answering" wording that fits neither (cycle-3 pre-PR review, both lenses).
    /// <see cref="ReachabilityStatus.RefusedConnection"/> prints nothing here: whichever caller
    /// reached a still-refused result already explained it — <see cref="DiagnoseRefusedConnectionAsync"/>
    /// prints its own "is it running / still not answering" line, <see cref="DiagnoseOtherErrorAsync"/>
    /// prints the same "still not answering" line when its own retry ends up refused instead of
    /// persisting as <see cref="ReachabilityStatus.OtherError"/> (cycle-4 pre-PR review, conformance
    /// lens), and the immediate-probe case never reaches this method at all (it retries first).
    /// </summary>
    private static void ReportUnreachable(ReachabilityReport reachability, ConnectionStringResolution resolution)
    {
        switch (reachability.Status)
        {
            case ReachabilityStatus.RefusedConnection:
                break;

            case ReachabilityStatus.AuthenticationFailed:
                AnsiConsole.MarkupLine(
                    $"[red]Reached Postgres at {reachability.Host.EscapeMarkup()}:{reachability.Port}[/], but it "
                    + $"rejected the credentials in {resolution.Description.EscapeMarkup()}: {reachability.Detail.EscapeMarkup()}");
                AnsiConsole.MarkupLine(
                    "[dim]Check the username and password in the connection string, or rotate the credential "
                    + "and reconfigure it there.[/]");
                break;

            case ReachabilityStatus.DatabaseMissing:
                AnsiConsole.MarkupLine(
                    $"[red]Reached Postgres at {reachability.Host.EscapeMarkup()}:{reachability.Port}[/], but the "
                    + $"database '{reachability.Database.EscapeMarkup()}' does not exist there yet. Create it, or "
                    + "point the connection string at one that does.");
                break;

            default:
                AnsiConsole.MarkupLine(
                    $"[red]Reached Postgres at {reachability.Host.EscapeMarkup()}:{reachability.Port}[/], but it "
                    + $"reported: {reachability.Detail.EscapeMarkup()}");
                break;
        }
    }

    /// <summary>
    /// Everything the <see cref="ReachabilityStatus.RefusedConnection"/> case does once the first
    /// probe refuses: report what Docker knows via <see cref="ReportContainerRuntimeStatusAsync"/>,
    /// offer to start what is actually stopped via <see cref="OfferAndStartAsync"/> — and, when the
    /// container is already confirmed <see cref="PostgresContainerStatus.Running"/> (so
    /// <see cref="OfferAndStartAsync"/> refuses to touch it outright: restarting an already-running
    /// container is never the fix), retry the same probe with the bounded wait the guarded
    /// recreate's own readiness poll already uses, rather than diagnosing "unreachable" from a
    /// single sample. That single-sample diagnosis is the defect this method fixes: the guarded
    /// recreate (<see cref="CheckContainerPortBindingAsync(bool,ProcessRunner,Func{bool},Func{ConnectionStringResolution},CancellationToken)"/>)
    /// already waits for one clean answer before declaring the container recreated, but the very
    /// next probe — a fresh connection, run moments later by this doctor's next question — could
    /// still catch Postgres transiently dropping it while it finishes starting (field reports
    /// 2026-09-27, Mac and Windows: both saw "Exception while reading from stream" immediately
    /// after a printed "Recreated" success, and both left the daemon stopped because this check
    /// used to give up after that one sample). Returns the final <see cref="ReachabilityReport"/>,
    /// reachable or not, for the caller to act on.
    /// </summary>
    internal static async Task<ReachabilityReport> DiagnoseRefusedConnectionAsync(
        ReachabilityReport reachability,
        ConnectionStringResolution resolution,
        string connectionString,
        bool offerFixes,
        bool assumeYes,
        ProcessRunner runner,
        Func<CancellationToken, Task<ReachabilityReport>> reachabilityProbe,
        TimeSpan readinessTimeout,
        TimeSpan readinessPollInterval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine(
            $"[yellow]Configured (from {resolution.Description.EscapeMarkup()}) to connect to "
            + $"{reachability.Host.EscapeMarkup()}:{reachability.Port}[/], but nothing is listening there. "
            + $"({reachability.Detail.EscapeMarkup()})");

        bool waitedForStartup = false;
        bool looksLocal = IsHall9kOwnLocalAddress(reachability.Host, reachability.Port);
        if (looksLocal)
        {
            // Question 4's Docker awareness applies here too, not only to the
            // never-configured path: the boundary is Docker itself, wherever the
            // check finds an unreachable local Postgres (origin incident,
            // 2026-08-21 — a machine reboot leaves the connection string configured
            // but Docker Desktop, and so hall9k-postgres, not yet back up).
            (ContainerRuntimeStatus runtime, bool containerConfirmed, PostgresContainerStatus container) =
                await ReportContainerRuntimeStatusAsync(runner, connectionStringAlreadyConfigured: true, offerFixes, cancellationToken);

            bool justStarted = offerFixes && runtime == ContainerRuntimeStatus.Running
                && (await OfferAndStartAsync(connectionString, containerConfirmed, container, assumeYes, runner, cancellationToken)).Started;

            if (justStarted || (offerFixes && containerConfirmed && container == PostgresContainerStatus.Running))
            {
                // OfferAndStartAsync's own readiness wait (when justStarted) already saw one clean
                // answer, and a container this doctor never had to start but finds already
                // confirmed Running is in the same still-booting window either way — a fresh
                // connection moments later can still catch Postgres transiently dropping it while
                // it finishes starting (field reports 2026-09-27, Mac and Windows), so both cases
                // get the same bounded retry rather than a single fresh sample (cycle-1 pre-PR
                // review, adversarial lens — the justStarted case used to take only one more
                // sample here). Gated on offerFixes so a passive diagnosis after an ordinary
                // command's own failure never adds up to 30s to that failure on its own.
                AnsiConsole.Markup(
                    $"[dim]Retrying for up to {readinessTimeout.TotalSeconds:0}s, in case it is still finishing startup…[/]");
                reachability = await WaitForReachableAsync(
                    reachabilityProbe, readinessTimeout, readinessPollInterval, timeProvider, cancellationToken);
                AnsiConsole.WriteLine();
                waitedForStartup = true;
            }
        }

        // Only for a still-refused result: a status the wait turned into something else —
        // AuthenticationFailed, DatabaseMissing, a persisting OtherError — already got its own
        // real answer from Postgres, and saying "not answering" (or "is it running?") about a
        // server that just answered would report the opposite of what actually happened. The
        // caller's own switch (CheckReachabilityAndSchemaAsync) prints the diagnosis that fits
        // whatever this method actually returns (cycle-3 pre-PR review, both lenses).
        if (reachability.Status == ReachabilityStatus.RefusedConnection)
        {
            AnsiConsole.MarkupLine(waitedForStartup
                ? $"[dim]Still not answering after waiting up to {readinessTimeout.TotalSeconds:0}s for it to finish "
                    + $"starting. Check docker logs {PostgresRuntime.ContainerName}, then try again.[/]"
                : "[dim]Is Postgres running? Start it, then try again.[/]");
        }

        return reachability;
    }

    /// <summary>
    /// The <see cref="ReachabilityStatus.OtherError"/> sibling of <see cref="DiagnoseRefusedConnectionAsync"/>:
    /// a container still booting reads as either a refused connection or Postgres answering "the
    /// database system is starting up" (57P03, which <see cref="DatabaseReachability.ProbeAsync"/>
    /// turns into <see cref="ReachabilityStatus.OtherError"/>) — only the first got the bounded
    /// retry, so the second still failed on a single sample (cycle-1 pre-PR review, conformance
    /// lens). Gated the same way as the refused-connection retry: only for a local default address,
    /// only when the caller is offering fixes at all (<paramref name="offerFixes"/> — a passive
    /// diagnosis after an ordinary command's own failure should not add up to 30s to that failure),
    /// and only once <c>hall9k-postgres</c> is itself confirmed <see cref="PostgresContainerStatus.Running"/>,
    /// since only then does "still starting up" actually explain the error. Internal, the same
    /// visibility <see cref="DiagnoseRefusedConnectionAsync"/> already uses, so a test can drive it
    /// directly with a fake probe and a fake process runner rather than going through
    /// <see cref="CheckReachabilityAndSchemaAsync(string,ConnectionStringResolution,bool,bool,ProcessRunner,Func{bool},Func{CancellationToken,Task{ReachabilityReport}},TimeSpan,TimeSpan,TimeProvider,CancellationToken,bool)"/>,
    /// which would reach a real schema check the moment reachability turns Reachable.
    /// </summary>
    internal static async Task<ReachabilityReport> DiagnoseOtherErrorAsync(
        ReachabilityReport reachability,
        bool offerFixes,
        ProcessRunner runner,
        Func<CancellationToken, Task<ReachabilityReport>> reachabilityProbe,
        TimeSpan readinessTimeout,
        TimeSpan readinessPollInterval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        bool looksLocal = IsHall9kOwnLocalAddress(reachability.Host, reachability.Port);
        if (!offerFixes || !looksLocal
            || await ContainerRuntimeProbe.RuntimeStatusAsync(runner, cancellationToken) != ContainerRuntimeStatus.Running)
        {
            return reachability;
        }

        (bool containerConfirmed, PostgresContainerStatus container) =
            await ContainerRuntimeProbe.Hall9kContainerStatusAsync(runner, cancellationToken);
        if (!containerConfirmed || container != PostgresContainerStatus.Running)
        {
            return reachability;
        }

        AnsiConsole.Markup(
            $"[dim]{PostgresRuntime.ContainerName} is confirmed running — retrying for up to "
            + $"{readinessTimeout.TotalSeconds:0}s, in case it is still finishing startup…[/]");
        ReachabilityReport retried = await WaitForReachableAsync(
            reachabilityProbe, readinessTimeout, readinessPollInterval, timeProvider, cancellationToken);
        AnsiConsole.WriteLine();

        // The status can flip to RefusedConnection across polls (Postgres briefly closing its
        // listening socket mid-restart) — and ReportUnreachable stays silent for that status,
        // trusting DiagnoseRefusedConnectionAsync's own callers to have already explained it.
        // This method's own callers never go through there, so a still-refused result here would
        // otherwise reach the caller's ReportUnreachable call and print nothing at all (cycle-4
        // pre-PR review, conformance lens).
        if (retried.Status == ReachabilityStatus.RefusedConnection)
        {
            AnsiConsole.MarkupLine(
                $"[dim]Still not answering after waiting up to {readinessTimeout.TotalSeconds:0}s for it to finish "
                + $"starting. Check docker logs {PostgresRuntime.ContainerName}, then try again.[/]");
        }

        return retried;
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
    /// <paramref name="connectionStringAlreadyConfigured"/> distinguishes the two callers for
    /// the confirmed-<see cref="PostgresContainerStatus.Running"/> case specifically: the
    /// not-configured path's "point the env var at it, or run h9k doctor --yes" advice is only
    /// true when nothing is configured yet — the configured-but-refused caller already points
    /// its connection string at this exact address and already tried it a moment ago, and
    /// <c>--yes</c> cannot reach the not-configured path's own offer from there either, so both
    /// halves of that advice would be false on that caller (cycle-1 adversarial review finding,
    /// on this method). <paramref name="offerFixes"/> distinguishes the same caller's own two
    /// modes: whether to name "retry in a moment" as the operator's own next step, or leave it
    /// unsaid because the caller is about to retry that exact wait automatically the moment
    /// this returns — dropping it unconditionally left a passive, non-retrying diagnosis with no
    /// next step at all (cycle-3 pre-PR review, adversarial lens).
    /// </summary>
    private static async Task<(ContainerRuntimeStatus Runtime, bool ContainerConfirmed, PostgresContainerStatus Container)> ReportContainerRuntimeStatusAsync(
        ProcessRunner runner, bool connectionStringAlreadyConfigured, bool offerFixes, CancellationToken cancellationToken)
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
                else if (container == PostgresContainerStatus.Running && connectionStringAlreadyConfigured)
                {
                    // "Retry in a moment" only doesn't apply when offerFixes is set: the only
                    // caller that reaches this branch (DiagnoseRefusedConnectionAsync) is about to
                    // retry automatically, on its own, right after this prints — but only while it
                    // is offering fixes at all. A passive diagnosis (offerFixes: false — an ordinary
                    // command's own NpgsqlException, via Program.cs) never retries anything, so
                    // dropping that advice unconditionally left a passive caller with a "confirmed
                    // running" message and no next step, immediately followed by the contradicting
                    // "Is Postgres running?" from this same method's caller (cycle-3 pre-PR review,
                    // adversarial lens).
                    AnsiConsole.MarkupLine(offerFixes
                        ? $"[dim]Found {PostgresRuntime.ContainerName} confirmed running[/] — but nothing answered "
                            + "when the already-configured connection string just tried it, so Postgres inside it "
                            + "may still be finishing initialisation, or something else is bound to that address."
                        : $"[dim]Found {PostgresRuntime.ContainerName} confirmed running[/] — but nothing answered "
                            + "when the already-configured connection string just tried it, so Postgres inside it "
                            + "may still be finishing initialisation, or something else is bound to that address. "
                            + "Retry in a moment, or check the container's own logs.");
                }
                else if (container == PostgresContainerStatus.Running)
                {
                    AnsiConsole.MarkupLine(
                        $"[dim]Found {PostgresRuntime.ContainerName} confirmed running[/] — if that is what is "
                        + $"listening at 127.0.0.1:5432, point {Hall9kDatabase.EnvironmentVariableName} at it "
                        + "directly, or run h9k doctor --yes to configure it automatically.");
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
    /// this method fixes). Probes <paramref name="connectionString"/> directly — the container
    /// is confirmed by name, not by the connection string a caller happens to have configured,
    /// since nothing is configured yet on this path — and, if it answers, records that same
    /// string exactly the way <see cref="OfferAndStartAsync"/>'s own caller already does.
    /// Offer-never-force still applies: it asks before writing (or is told
    /// <paramref name="assumeYes"/> in its place), because writing the platform config file is
    /// the same kind of fix as starting a container, even though nothing here starts anything.
    /// Takes <paramref name="probe"/> rather than calling <see cref="DatabaseReachability.ProbeAsync"/>
    /// directly so a test can substitute a fake answer instead of depending on a real Postgres
    /// bound to the exact host and port <paramref name="connectionString"/> names.
    /// </summary>
    internal static Task<ConnectionStringResolution?> OfferAndRecordAlreadyRunningContainerAsync(
        string connectionString, bool assumeYes, Func<CancellationToken, Task<ReachabilityReport>> probe,
        CancellationToken cancellationToken) =>
        OfferAndRecordAlreadyRunningContainerAsync(
            connectionString, assumeYes, probe, ReadinessTimeout, ReadinessPollInterval, TimeProvider.System, cancellationToken);

    /// <summary>
    /// Same offer as the 4-argument overload above, with the poll's timeout, interval and clock
    /// injectable so a test can exercise the not-ready-yet case without the real 30s wait — the
    /// same seam <see cref="WaitForReadinessAsync(Func{CancellationToken,Task{ReachabilityReport}},TimeSpan,TimeSpan,TimeProvider,CancellationToken)"/>
    /// already uses for the sibling start-offer's own readiness poll.
    /// </summary>
    internal static async Task<ConnectionStringResolution?> OfferAndRecordAlreadyRunningContainerAsync(
        string connectionString,
        bool assumeYes,
        Func<CancellationToken, Task<ReachabilityReport>> probe,
        TimeSpan timeout,
        TimeSpan pollInterval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ReachabilityReport report = await probe(cancellationToken);
        if (report.Status == ReachabilityStatus.RefusedConnection)
        {
            // A freshly-started container can report Running before Postgres inside it has
            // finished initialising — OfferAndStartAsync's own sibling path already waits up to
            // ReadinessTimeout for exactly that; this path used to give up after a single probe
            // instead of waiting the same way (cycle-1 review finding on this method).
            AnsiConsole.Markup("[dim]Waiting for it to come up…[/]");
            DateTimeOffset deadline = timeProvider.GetUtcNow() + timeout;
            while (report.Status == ReachabilityStatus.RefusedConnection && timeProvider.GetUtcNow() < deadline)
            {
                await Task.Delay(pollInterval, cancellationToken);
                report = await probe(cancellationToken);
            }

            AnsiConsole.WriteLine();
        }

        switch (report.Status)
        {
            case ReachabilityStatus.Reachable:
                break;

            case ReachabilityStatus.AuthenticationFailed:
                // The two facts observed here are independent (a running container by name,
                // an address that answers) and neither one alone says whose credentials are
                // wrong — naming both, rather than staying silent, is the most useful thing
                // left to say once this path's own probe has already found something to report.
                AnsiConsole.MarkupLine(
                    $"[yellow]{PostgresRuntime.ContainerName} is confirmed running, and something at "
                    + $"{report.Host.EscapeMarkup()}:{report.Port} answered[/], but it rejected the credentials "
                    + $"recorded in {PostgresRuntime.ComposeFile.EscapeMarkup()}: {report.Detail.EscapeMarkup()}. "
                    + $"If that isn't the container's own Postgres, point {Hall9kDatabase.EnvironmentVariableName} "
                    + "at the right one directly instead.");
                return null;

            case ReachabilityStatus.DatabaseMissing:
                AnsiConsole.MarkupLine(
                    $"[yellow]{PostgresRuntime.ContainerName} is confirmed running, and something at "
                    + $"{report.Host.EscapeMarkup()}:{report.Port} answered[/], but the "
                    + $"'{report.Database.EscapeMarkup()}' database does not exist there yet.");
                return null;

            case ReachabilityStatus.RefusedConnection:
                AnsiConsole.MarkupLine(
                    $"[yellow]{PostgresRuntime.ContainerName} is confirmed running, but nothing answered at "
                    + $"{report.Host.EscapeMarkup()}:{report.Port}[/] ({report.Detail.EscapeMarkup()}) — Postgres "
                    + "inside the container may still be starting, or something else is bound to that port. "
                    + "Retry in a moment, or check the container's own logs.");
                return null;

            default:
                AnsiConsole.MarkupLine(
                    $"[yellow]{PostgresRuntime.ContainerName} is confirmed running, and something at "
                    + $"{report.Host.EscapeMarkup()}:{report.Port} answered[/], but it reported: "
                    + $"{report.Detail.EscapeMarkup()}.");
                return null;
        }

        if (!assumeYes && !AnsiConsole.Profile.Capabilities.Interactive)
        {
            // Same rule as the schema and start offers: a skipped prompt names itself and the
            // flag that answers it, rather than falling through to generic advice as though
            // nothing here could have been fixed automatically (origin: Windows install
            // friction log item 3).
            AnsiConsole.MarkupLine(
                $"[dim]{PostgresRuntime.ContainerName} is confirmed running, and Postgres is answering at the "
                + "default address, but skipping the offer to point at it — stdin is not a terminal, so there is "
                + "nobody to confirm this. Re-run with h9k doctor --yes to configure it automatically.[/]");
            return null;
        }

        if (!assumeYes && !AnsiConsole.Confirm(
            $"Found {PostgresRuntime.ContainerName} confirmed running, and Postgres answering at the default "
            + "address. Configure h9k to use it?",
            defaultValue: true))
        {
            return null;
        }

        await Hall9kDatabase.WriteConfiguredConnectionStringAsync(connectionString, cancellationToken);
        AnsiConsole.MarkupLine($"[green]Configured[/]: wrote the connection string to {Hall9kDatabase.ConfigFile.EscapeMarkup()}.");
        return Hall9kDatabase.Resolve();
    }

    /// <summary>
    /// Offer-never-force (same shape as the queue prompt at publish): asks before
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
    /// <paramref name="connectionStringToPoll"/> is <see langword="null"/> when the caller has no
    /// connection string of its own yet (the not-configured path): once the confirm gate passes,
    /// this derives one from whatever password <see cref="PostgresRuntime.WriteComposeFileAsync(CancellationToken)"/>
    /// finds already in effect (or generates), so the same password backs both the readiness poll
    /// below and whatever the caller goes on to record. A caller that already has a connection
    /// string configured (<see cref="DiagnoseRefusedConnectionAsync"/>) passes it directly instead,
    /// since that is what a start here is meant to bring up to answer.
    /// </summary>
    private static async Task<(bool Started, string ConnectionString)> OfferAndStartAsync(
        string? connectionStringToPoll,
        bool containerConfirmed,
        PostgresContainerStatus container,
        bool assumeYes,
        ProcessRunner runner,
        CancellationToken cancellationToken)
    {
        if (!containerConfirmed)
        {
            return (false, connectionStringToPoll ?? string.Empty);
        }

        if (container == PostgresContainerStatus.Running)
        {
            // Already running, so whatever is actually wrong, starting it again is not the fix.
            return (false, connectionStringToPoll ?? string.Empty);
        }

        if (container == PostgresContainerStatus.Stopped)
        {
            // A plain docker start never rolls a container's port binding forward — Docker only
            // reads the compose file's port line again at creation — so starting a stopped
            // container still bound to every interface would bring hall9k's own Postgres, and
            // its known default credentials, straight back onto the network the moment this
            // offer runs, on every path that reaches it: h9k doctor after its own port-binding
            // check declined to recreate, and h9k daemon start, which never runs that check at
            // all (cycle-1 pre-PR review, conformance lens).
            (bool inspected, string? hostIp, _, _) = await ContainerRuntimeProbe.InspectPortBindingAsync(runner, cancellationToken);
            if (inspected && hostIp is not (null or "127.0.0.1"))
            {
                AnsiConsole.MarkupLine(
                    $"[red]Not starting[/] — {PostgresRuntime.ContainerName} publishes port 5432 on "
                    + $"{hostIp.EscapeMarkup()}, not 127.0.0.1, and a plain docker start would keep that "
                    + "binding — Docker only reads the compose file's port mapping again when the container is "
                    + "recreated. Run h9k doctor --yes to recreate it safely, or see docs/operations.md's "
                    + "Network exposure section for the hand commands.");
                return (false, connectionStringToPoll ?? string.Empty);
            }
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
            return (false, connectionStringToPoll ?? string.Empty);
        }

        if (!assumeYes)
        {
            string prompt = container == PostgresContainerStatus.Stopped
                ? "Start it now via Docker?"
                : "Postgres isn't running. Start it now via Docker?";
            if (!AnsiConsole.Confirm(prompt, defaultValue: true))
            {
                return (false, connectionStringToPoll ?? string.Empty);
            }
        }

        string connectionString = connectionStringToPoll
            ?? Hall9kDatabase.ConnectionStringWithPassword(await PostgresRuntime.WriteComposeFileAsync(cancellationToken));

        if (container == PostgresContainerStatus.Stopped)
        {
            if (!await ContainerRuntimeProbe.StartStoppedContainerAsync(runner, cancellationToken))
            {
                AnsiConsole.MarkupLine(
                    $"[red]Docker could not start it[/] — check docker logs {PostgresRuntime.ContainerName}, "
                    + "or run the command by hand.");
                return (false, connectionString);
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
                    return (false, connectionString);
                case ComposeUpResult.LegacyVolumeCheckFailed:
                    AnsiConsole.MarkupLine(
                        $"[red]Not starting[/] — whether a volume from before this install's compose name: pin "
                        + "still exists could not be checked (docker volume ls itself failed), and bringing up a "
                        + $"fresh container now could create a new, empty {PostgresRuntime.VolumeName} volume "
                        + "beside real data this could not see to warn about. Retry once Docker is answering "
                        + "reliably.");
                    return (false, connectionString);
                case ComposeUpResult.ComposeFileWriteFailed:
                    AnsiConsole.MarkupLine(
                        $"[red]Not starting[/] — {PostgresRuntime.ComposeFile.EscapeMarkup()} could not be "
                        + "rewritten, and bringing up a fresh container from a compose file that might still be "
                        + "stale could publish Postgres on every interface. Fix that and run h9k doctor again.");
                    return (false, connectionString);
                case ComposeUpResult.Failed:
                    AnsiConsole.MarkupLine(
                        $"[red]Docker could not start it[/] — check docker logs {PostgresRuntime.ContainerName}, "
                        + "or run the command by hand.");
                    return (false, connectionString);
            }
        }

        AnsiConsole.Markup("[dim]Waiting for it to come up…[/]");
        bool ready = await WaitForReadinessAsync(connectionString, cancellationToken);
        AnsiConsole.WriteLine();
        if (!ready)
        {
            AnsiConsole.MarkupLine(
                $"[red]Started, but it was not answering within {ReadinessTimeout.TotalSeconds:0}s.[/] "
                + $"Check docker logs {PostgresRuntime.ContainerName}, then try again.");
        }

        return (ready, connectionString);
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
    /// speed. A thin wrapper over <see cref="WaitForReachableAsync"/> for the callers that
    /// only need the yes/no answer, not the final report.
    /// </summary>
    internal static async Task<bool> WaitForReadinessAsync(
        Func<CancellationToken, Task<ReachabilityReport>> probe,
        TimeSpan timeout,
        TimeSpan pollInterval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        (await WaitForReachableAsync(probe, timeout, pollInterval, timeProvider, cancellationToken)).Status
            == ReachabilityStatus.Reachable;

    /// <summary>
    /// Same bounded poll, returning the final <see cref="ReachabilityReport"/> rather than a bare
    /// bool — the seam <see cref="DiagnoseRefusedConnectionAsync"/> needs to retry a refused
    /// connection and still report whatever the last probe actually said (a different failure
    /// kind, or the same one) once the bound expires, rather than collapsing every non-reachable
    /// outcome into "false". Stops the moment the probe answers with a status waiting cannot
    /// change — <see cref="ReachabilityStatus.AuthenticationFailed"/> or
    /// <see cref="ReachabilityStatus.DatabaseMissing"/> mean Postgres itself already answered
    /// definitively, so polling out the rest of <paramref name="timeout"/> would only delay
    /// reporting a wrong-credentials or missing-database diagnosis that waiting longer never
    /// fixes (cycle-3 pre-PR review, adversarial lens) — only
    /// <see cref="ReachabilityStatus.RefusedConnection"/> and <see cref="ReachabilityStatus.OtherError"/>
    /// plausibly mean "still starting up" and are worth retrying.
    /// </summary>
    internal static async Task<ReachabilityReport> WaitForReachableAsync(
        Func<CancellationToken, Task<ReachabilityReport>> probe,
        TimeSpan timeout,
        TimeSpan pollInterval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = timeProvider.GetUtcNow() + timeout;
        ReachabilityReport report = await probe(cancellationToken);
        while (IsWorthRetrying(report.Status) && timeProvider.GetUtcNow() < deadline)
        {
            await Task.Delay(pollInterval, timeProvider, cancellationToken);
            report = await probe(cancellationToken);
        }

        return report;
    }

    private static bool IsWorthRetrying(ReachabilityStatus status) =>
        status is ReachabilityStatus.RefusedConnection or ReachabilityStatus.OtherError;

    /// <summary>
    /// Whether the schema already there is one <c>AutoCreate.CreateOnly</c> — every ordinary
    /// store this platform opens with — can use as-is, question 3's second half, asked only
    /// once <see cref="DatabaseReachability.SchemaPresentAsync"/> has already answered yes to
    /// "is it there at all": that check is a bare table-existence probe and stays one, so this
    /// is the one place a genuine column-level (or other object-level) difference is actually
    /// detected, via the same migration diff <see cref="ApplySchemaAsync"/> applies.
    /// <see cref="SchemaPatchDifference.Create"/> — configured objects missing, nothing existing
    /// altered — is current enough: <c>CreateOnly</c> creates a missing table lazily on its own
    /// first use, the same as the schema-entirely-missing branch above this method's own caller
    /// already treats as self-healing. Only <see cref="SchemaPatchDifference.Update"/> (an
    /// existing object needs altering) or <see cref="SchemaPatchDifference.Invalid"/> is
    /// genuinely stale: those are exactly what <c>CreateOnly</c> refuses outright (cycle-1
    /// pre-PR review, conformance lens — the previous version of this method treated a young
    /// install with one lazily-created table not yet touched, such as <c>EpicDetails</c>, as
    /// stale for no reason).
    /// </summary>
    private static async Task<bool> SchemaCurrentAsync(string connectionString, CancellationToken cancellationToken)
    {
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(connectionString);
            opts.ConfigureHall9k(AutoCreate.None);
        });
        SchemaMigration migration = await store.Storage.Database.CreateMigrationAsync(cancellationToken);
        return migration.Difference is SchemaPatchDifference.None or SchemaPatchDifference.Create;
    }

    /// <summary>
    /// The schema offer's action: Marten already creates its own tables on first real use
    /// (<c>AutoCreate.CreateOnly</c>, the mode every other store in this platform opens
    /// with) — this just makes that happen on the spot instead of on the next command, for
    /// an operator who asked the doctor "shall I set that up?" and wants to see it done. Also
    /// what <see cref="SchemaCurrentAsync"/> found stale: <c>CreateOrUpdate</c> both creates
    /// what is missing and alters what has changed, in the one call.
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
