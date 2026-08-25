using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Storage;
using Microsoft.Win32;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Takes the platform off a machine without taking the work with it (Decisions Log #83,
/// the Windows walk): stops a running daemon, unregisters autostart, removes the PATH link,
/// and removes everything under ~/.hall9k that <c>h9k install</c> itself ever wrote — bin/,
/// the skill set, the Postgres compose file, the daemon's log/pid/lock files — so a removed
/// home is a removed home for exactly the machine the walk's own reasoning describes: one
/// that has run nothing but install and uninstall. What it deliberately never touches is
/// anything install did not write: a registered project's home
/// (<c>~/.hall9k/projects/&lt;name&gt;</c>, real git clones and worktrees, possibly carrying
/// uncommitted work), <c>~/.hall9k/credentials</c>, <c>config.json</c> (written by an operator
/// or by <see cref="DatabaseDoctor"/>'s start-offer, never by install itself — deleting it
/// would silently strip a configured connection string out from under a reinstall, pointing
/// it at a fresh empty database instead of reconnecting to the one the operator set up), or
/// the global idea/run fallback directories — none of those are "the install", and wiping
/// them on a machine that has done real work would be exactly the "taking the work with it"
/// this command exists not to do.
/// Postgres gets the identical split: the <see cref="PostgresRuntime.ContainerName"/> container
/// is stopped, never removed, and its <see cref="PostgresRuntime.VolumeName"/> volume is never
/// touched, so a later <c>h9k install</c> finds it again exactly as it was. <c>--purge-data</c>
/// is the one path that destroys both, and it names what is about to die and asks before doing it.
/// </summary>
public sealed class UninstallCommand : Hall9kAsyncCommand<UninstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--purge-data")]
        [Description(
            "Also destroy the hall9k-postgres container AND its data volume — every task, run, and idea "
            + "recorded there goes with it, permanently. The only uninstall path that touches your data; "
            + "without it, Postgres survives in Docker for a later install to reconnect to. Asks for "
            + "confirmation unless --yes is given.")]
        public bool PurgeData { get; init; }

        [CommandOption("--yes")]
        [Description(
            "Skip the --purge-data confirmation prompt — required in a non-interactive session, since "
            + "there is no terminal to ask. Has no effect without --purge-data.")]
        public bool Yes { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        ProcessRunner runner = ExternalProcess.Runner;

        if (settings.PurgeData && !await ConfirmPurgeAsync(settings.Yes, runner, cancellationToken))
        {
            await Console.Error.WriteLineAsync(
                "Refusing to purge data without confirmation — nothing on this machine has been touched. "
                + "Re-run with --yes to skip the prompt in a non-interactive session, or drop --purge-data "
                + "for the default uninstall that leaves your database untouched.");
            return ExitCodes.Error;
        }

        bool daemonStopped;
        try
        {
            daemonStopped = await StopDaemonAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            // WindowsDaemonAutostart.DisableAsync throws when schtasks /Delete itself fails
            // (LaunchdDaemonAutostart's own DisableAsync never does — a missing plist is a
            // no-op there), and this call site predates that: it runs uncaught, same as
            // DaemonAutostartDisableCommand's identical call before that command grew its
            // own try/catch around it. Stop here, before bin/, the PATH link, or the home
            // are touched, rather than let a .NET stack trace stand in for this command's
            // own summary.
            IDaemonAutostart autostart = DaemonAutostart.ForCurrentPlatform();
            await Console.Error.WriteLineAsync(
                $"Autostart disable failed: the {autostart.MechanismDescription} may still be registered. "
                + $"{exception.Message}");
            return ExitCodes.Error;
        }

        if (!daemonStopped)
        {
            // Nothing else runs: h9k itself (bin/ and the PATH link) stays in place so the
            // instruction below is one an operator can actually follow, and Postgres is left
            // exactly as it is rather than stopped or purged out from under a daemon that may
            // still be mid-append to it. Autostart was already touched, inside StopDaemonAsync
            // — but that call's own outcome is already folded into daemonStopped, so reaching
            // this branch means the daemon is genuinely still running even after that attempt.
            AnsiConsole.MarkupLine(
                "[yellow]h9kd is still running[/] — leaving bin/, the PATH link, and Postgres exactly as they "
                + "are, so h9k stays runnable and nothing is pulled out from under the daemon while it may "
                + "still be writing. Stop it, then run h9k uninstall again.");
            PrintSummary(settings.PurgeData, DataTierOutcome.NotAttempted, daemonStopped: false, homeRemovalOutcome: null);
            return ExitCodes.Error;
        }

        (bool dataTierOk, DataTierOutcome dataTierOutcome) = await HandleDataTierAsync(settings.PurgeData, runner, cancellationToken);
        if (settings.PurgeData && !dataTierOk)
        {
            // HandleDataTierAsync has already printed why (Docker unreachable, or a purge it
            // could not fully confirm) and what to run again — "run h9k uninstall --purge-data
            // again". Removing bin/, the PATH link, and the home here anyway would strand that
            // remedy: there would be no h9k left on this machine to run it with, and "before
            // anything is removed" (the promise the purge refusal itself makes) would already be
            // false by the time the operator read it. This gate is purge-only: on the default
            // tier, dataTierOk can also come back false — a live hall9k-postgres container's
            // `docker stop` failing, or `docker ps -a` itself failing so the container's status
            // could not even be confirmed — but every remedy those two default-tier cases print
            // (a bare `docker stop hall9k-postgres`, or `docker ps -a --filter ...`) is a command
            // an operator can run without h9k still being on the machine, so neither blocks the
            // removal below (the outcome is still folded into this run's exit code further down).
            PrintSummary(settings.PurgeData, dataTierOutcome, daemonStopped: true, homeRemovalOutcome: null);
            return ExitCodes.Error;
        }

        bool pathLinkRemoved = OperatingSystem.IsWindows()
            ? RemoveFromWindowsPath(DaemonRuntime.BinDirectory)
            : RemoveFromPath(
                Path.Combine(DaemonRuntime.BinDirectory, "h9k"),
                Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        string home = PlatformPaths.Home;
        bool homeExistedBeforeRemoval = Directory.Exists(home);

        // Skills first, deliberately: RemovePublished hashes file contents, which on a
        // self-contained install can still need to lazily load an assembly this process has not
        // touched yet — one that lives in bin/, the very directory RemoveInstallOwnedEntries is
        // about to delete. Hashing before bin/ goes means every assembly this process could ever
        // need is loaded (or at least still on disk to load) while bin/ still exists.
        List<string> stillPresent = [];
        (IReadOnlyList<string> skillsRemoved, bool skillManifestConfirmed) = SkillSeeder.RemovePublished(stillPresent);
        stillPresent.AddRange(RemoveInstallOwnedEntries(InstallOwnedEntries(home, stillPresent)));
        TryRemoveIfEmpty(home, stillPresent);
        bool homeFullyRemoved = stillPresent.Count == 0 && pathLinkRemoved;

        ReportHomeRemoval(home, homeExistedBeforeRemoval, stillPresent, skillsRemoved, skillManifestConfirmed);
        PrintSummary(settings.PurgeData, dataTierOutcome, daemonStopped: true, homeRemovalOutcome: homeFullyRemoved);

        return dataTierOk && homeFullyRemoved ? ExitCodes.Ok : ExitCodes.Error;
    }

    /// <summary>
    /// Names what --purge-data is about to destroy and asks before it happens — before
    /// anything else on the machine has been touched, so a refusal here leaves the whole
    /// command a no-op. --yes is the only way past a non-interactive session: there is no
    /// terminal to ask, so silence is never read as consent.
    /// <para>
    /// The volume(s) named in the prompt are observed, not guessed: <see cref="ObserveMountedVolumeNamesAsync"/>
    /// inspects the live container the same way <see cref="HandleDataTierAsync"/> will when it
    /// actually purges, so the prompt never names a different volume than the one that ends up
    /// destroyed. A <c>docker inspect</c> is read-only, so running it here does not violate
    /// "asks before anything happens" — nothing on the machine is touched until the operator
    /// consents.
    /// </para>
    /// </summary>
    private static async Task<bool> ConfirmPurgeAsync(bool yes, ProcessRunner runner, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> observedVolumes = await ObserveMountedVolumeNamesAsync(runner, cancellationToken);
        string volumeClause = observedVolumes.Count switch
        {
            0 => "its data volume, once one can be confirmed to exist — nothing is guessed at and destroyed "
                + $"if {PostgresRuntime.ContainerName} turns out to be absent",
            1 => $"its {observedVolumes[0]} data volume",
            _ => $"its {string.Join(" and ", observedVolumes)} data volumes",
        };

        AnsiConsole.MarkupLine(
            $"[red]--purge-data will destroy the {PostgresRuntime.ContainerName} container and {volumeClause}[/] "
            + "— every task, run, and idea Hall9k has recorded there goes with it, permanently. This is the "
            + "only uninstall path that touches your data; without it, the database survives in Docker for a "
            + "later reinstall to reconnect to.");

        if (yes)
        {
            return true;
        }

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine(
                "[red]Refusing[/]: this session cannot prompt for confirmation. Pass --yes to proceed anyway.");
            return false;
        }

        return AnsiConsole.Confirm("Destroy the container and its data volume?", defaultValue: false);
    }

    /// <summary>
    /// What <see cref="HandleDataTierAsync"/> will actually purge, observed the same way it
    /// observes it: empty when Docker itself cannot be asked (not running or not installed),
    /// when <c>hall9k-postgres</c> is absent, or when it exists but has no named volume mount to
    /// report (a bind mount, or an anonymous volume) — none of those three cases has anything to
    /// inspect, and falling back to the bare <see cref="PostgresRuntime.VolumeName"/> literal in
    /// any of them would be exactly the guess "never guess at unobserved facts" rules out: that
    /// literal is also the pre-migration Aspire dev loop's own volume name (see that property's
    /// remarks), so naming it here without having observed the container actually mount it would
    /// assert ownership of a volume that may not be this install's at all.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ObserveMountedVolumeNamesAsync(ProcessRunner runner, CancellationToken cancellationToken)
    {
        ContainerRuntimeStatus runtime = await ContainerRuntimeProbe.RuntimeStatusAsync(runner, cancellationToken);
        if (runtime != ContainerRuntimeStatus.Running)
        {
            return [];
        }

        (bool containerConfirmed, PostgresContainerStatus container) =
            await ContainerRuntimeProbe.Hall9kContainerStatusAsync(runner, cancellationToken);
        if (!containerConfirmed || container == PostgresContainerStatus.Absent)
        {
            return [];
        }

        (_, IReadOnlyList<string> names) = await ContainerRuntimeProbe.DataVolumeNameAsync(runner, cancellationToken);
        return names;
    }

    /// <summary>
    /// Stops whatever is running now through <see cref="DaemonLifecycle.StopAsync"/> — the
    /// service manager first (so KeepAlive/Scheduled Task policy cannot resurrect it
    /// mid-uninstall), then the recorded pid directly (a graceful SIGTERM on Unix, or the
    /// <see cref="DaemonRuntime.StopRequestFile"/> request <c>WindowsStopRequestWatcher</c>
    /// honours on Windows, per S1-14) — and unregisters autostart regardless of whether that
    /// first attempt succeeded.
    /// <para>
    /// Unregistering autostart is not a no-op on a daemon the first attempt failed to bring
    /// down: on macOS, <see cref="LaunchdDaemonAutostart.DisableAsync"/> runs its own
    /// <c>launchctl bootout</c> whenever the job is loaded, which can succeed at signalling and
    /// confirming the exit of a daemon that had just outlasted the first attempt's stop budget.
    /// Its <see cref="DaemonAutostartDisableOutcome"/> is folded back into the returned verdict
    /// so a daemon it actually finished off is reported stopped, rather than the confirmed
    /// outcome being thrown away in favour of the (by-then stale) first attempt's answer.
    /// </para>
    /// <para>
    /// Returns false when a daemon was found running and neither attempt could bring it down
    /// within its stop budget — <see cref="ExecuteAsync"/>'s signal to leave everything else on
    /// the machine untouched this run (bin/, the PATH link, Postgres, the pid and
    /// single-instance lock files) rather than remove any of it out from under a process that is
    /// still alive: deleting the lock file frees the single-instance guard for a second daemon
    /// to start against the same database, deleting bin/ or the PATH link strands the operator
    /// without a working h9k to retry with, and stopping or purging Postgres risks the very
    /// in-flight appends this stop budget exists to protect. The command ends with a nonzero
    /// exit rather than reporting a stop that did not happen.
    /// </para>
    /// </summary>
    private static async Task<bool> StopDaemonAsync(CancellationToken cancellationToken)
    {
        IDaemonAutostart autostart = DaemonAutostart.ForCurrentPlatform();

        bool stopped = await DaemonLifecycle.StopAsync(autostart, cancellationToken) == ExitCodes.Ok;

        if (autostart.IsSupported && autostart.IsEnabled)
        {
            DaemonAutostartDisableOutcome outcome = await autostart.DisableAsync(cancellationToken);
            stopped = FoldAutostartOutcomeIntoStopped(stopped, outcome);

            AnsiConsole.MarkupLine(outcome switch
            {
                DaemonAutostartDisableOutcome.DaemonStopped =>
                    "[green]Autostart unregistered[/] — the LaunchAgent is gone, and h9kd, still running under "
                        + "it, was stopped in the process.",
                DaemonAutostartDisableOutcome.DaemonStopping =>
                    "[yellow]Autostart unregistered[/] — the LaunchAgent is gone, and h9kd (still running under "
                        + "it) was signalled, but had not exited by the time this stopped watching.",
                _ => "[green]Autostart unregistered[/] — the LaunchAgent is gone.",
            });
        }
        else if (autostart.IsSupported)
        {
            AnsiConsole.MarkupLine("[dim]Autostart was not enabled — nothing to unregister.[/]");
        }

        return stopped;
    }

    /// <summary>
    /// <see cref="DaemonAutostartDisableOutcome.NothingStopped"/> means the service manager found
    /// nothing of its own to touch, so whatever the direct stop attempt already found stands
    /// unchanged. <see cref="DaemonAutostartDisableOutcome.DaemonStopped"/> and
    /// <see cref="DaemonAutostartDisableOutcome.DaemonStopping"/> are both a live manager-owned
    /// process actually observed just now — and only the first is confirmed gone, so
    /// <c>DaemonStopping</c> must override a <paramref name="stoppedSoFar"/> that was already
    /// <see langword="true"/> (the direct attempt finding nothing running, moments before launchd
    /// started this very process) rather than let a plain <c>||</c> mask a daemon this call just
    /// found still shutting down.
    /// </summary>
    internal static bool FoldAutostartOutcomeIntoStopped(bool stoppedSoFar, DaemonAutostartDisableOutcome outcome) =>
        outcome switch
        {
            DaemonAutostartDisableOutcome.DaemonStopped => true,
            DaemonAutostartDisableOutcome.DaemonStopping => false,
            _ => stoppedSoFar,
        };

    /// <summary>
    /// What actually happened to the Postgres tier — granular enough for
    /// <see cref="PrintSummary"/> to describe exactly what it observed, rather than
    /// collapsing every success into one asserted outcome ("stopped" implying a container that
    /// may never have existed, or "destroyed" implying a volume that was never there to
    /// destroy). <see cref="NotAttempted"/> is the daemon-still-running case: the whole tier
    /// was left untried on purpose, so there is nothing here to describe.
    /// </summary>
    internal enum DataTierOutcome
    {
        NotAttempted,
        NoContainerRuntime,
        ContainerRuntimeNotRunning,
        ContainerStatusCheckFailed,
        ContainerAbsent,
        ContainerStopped,
        ContainerStopFailed,
        PurgeUnconfirmedVolume,
        PurgedContainerOnly,
        PurgedContainerAndVolume,
        PurgeIncomplete,
    }

    /// <summary>
    /// The Postgres tier: stop-never-remove by default, or destroy both container and volume
    /// once <see cref="ConfirmPurgeAsync"/> has already consented. <c>Ok</c> is false whenever
    /// this tier could not do what it set out to do — a purge asked for and not fully carried
    /// out, but also a default-tier `docker stop` or `docker ps -a` that itself failed — which is
    /// the signal <see cref="ExecuteAsync"/> folds into a nonzero exit rather than reporting the
    /// tier as clean when it was not. Only the purge case blocks the home removal that follows
    /// (see <see cref="ExecuteAsync"/>'s own reasoning on the gate): the default tier's own
    /// failure remedies are bare docker commands an operator can run without h9k, so its
    /// <c>Ok: false</c> still lets that removal proceed. <c>Outcome</c> names exactly what was
    /// observed, so <see cref="PrintSummary"/> can report "the data volume is gone" only when a
    /// volume was actually observed and removed, and can avoid claiming a container was stopped
    /// or destroyed when the probe found none — never conflating "nothing was there to touch"
    /// with "it was touched and is now gone".
    /// </summary>
    internal static async Task<(bool Ok, DataTierOutcome Outcome)> HandleDataTierAsync(
        bool purgeData, ProcessRunner runner, CancellationToken cancellationToken)
    {
        ContainerRuntimeStatus runtime = await ContainerRuntimeProbe.RuntimeStatusAsync(runner, cancellationToken);
        if (runtime != ContainerRuntimeStatus.Running)
        {
            bool notInstalled = runtime == ContainerRuntimeStatus.NotInstalled;
            string reason = notInstalled
                ? "No container runtime (docker) is installed"
                : "Docker is installed but not running";
            DataTierOutcome outcome = notInstalled
                ? DataTierOutcome.NoContainerRuntime
                : DataTierOutcome.ContainerRuntimeNotRunning;

            if (!purgeData)
            {
                AnsiConsole.MarkupLine($"[dim]{reason} — nothing to stop in Docker. Your database, if any, is untouched.[/]");
                return (true, outcome);
            }

            if (notInstalled)
            {
                // Unlike "installed but not running" — where a real hall9k-postgres container may
                // be sitting there unreachable — no docker binary on this machine means no docker
                // daemon has ever run here, so there is no container and no docker-managed volume
                // for this install to have created in the first place. docs/operations.md's
                // native/remote-Postgres path makes this an ordinary machine, not an edge case, and
                // refusing the whole uninstall with an unfollowable "start Docker" remedy stranded
                // it. Proceeding (Ok: true) is the honest read of "nothing here to destroy", not a
                // guess about what a volume contains.
                AnsiConsole.MarkupLine(
                    $"[dim]{reason}[/] — {PostgresRuntime.ContainerName} can only ever have run under Docker, so "
                    + "there is no container and no data volume here for --purge-data to destroy. Proceeding "
                    + "with the rest of the uninstall.");
                return (true, outcome);
            }

            AnsiConsole.MarkupLine(
                $"[red]Could not purge[/]: {reason.EscapeMarkup()}, so {PostgresRuntime.ContainerName} could not "
                + "be reached and which volume it actually mounts cannot be confirmed — a container from before "
                + $"this branch's compose name: pin, or the pre-migration Aspire dev loop, can carry the "
                + $"{PostgresRuntime.VolumeName} literal without being this install's volume at all. Nothing in "
                + "Docker was touched. h9kd was already stopped and its autostart registration (if any) already "
                + "unregistered before this run reached here, though — bin/, the PATH link, and the rest of "
                + "~/.hall9k were left alone. Start Docker, then run h9k uninstall --purge-data again to finish.");
            return (false, outcome);
        }

        (bool containerConfirmed, PostgresContainerStatus container) =
            await ContainerRuntimeProbe.Hall9kContainerStatusAsync(runner, cancellationToken);
        if (!containerConfirmed)
        {
            // docker ps -a itself failed — not the same fact as "no such container": empty
            // stdout is what both an absent container and a failed command produce, so reading
            // this as a confirmed absence would report a container stopped or a volume
            // untouched that was never actually observed (finding #3, cycle 4 review).
            string remedy = $"docker ps -a --filter name=^/{PostgresRuntime.ContainerName}$";
            AnsiConsole.MarkupLine(purgeData
                ? $"[yellow]Purge incomplete[/] — checking Docker for the {PostgresRuntime.ContainerName} "
                    + $"container (docker ps -a) itself failed, so nothing was removed. Retry once Docker is "
                    + $"answering reliably, or check yourself: {remedy}"
                : $"[yellow]Could not confirm {PostgresRuntime.ContainerName}'s status[/] — checking Docker "
                    + $"(docker ps -a) itself failed, so nothing was stopped. Retry once Docker is answering "
                    + $"reliably, or check yourself: {remedy}");
            return (false, DataTierOutcome.ContainerStatusCheckFailed);
        }

        if (purgeData)
        {
            if (container == PostgresContainerStatus.Absent)
            {
                // No container left to docker inspect, so which volume is really this install's
                // cannot be observed — only guessed at. A container created before this
                // branch's compose name: pin (or brought up from an unpinned checkout, where
                // Compose derives the project name from the checkout's own directory name) mounts
                // a Compose-project-prefixed volume instead of the bare PostgresRuntime.VolumeName
                // literal, and that literal is also the exact string a pre-migration Aspire dev
                // loop names its own volume (PostgresRuntime.VolumeName's own remarks). Searching
                // by substring rather than enumerating PostgresRuntime.VolumeName and
                // PostgresRuntime.LegacyVolumeName as the only two possibilities also catches the
                // checkout-dirname-prefixed name docs/operations.md's Provisioning section
                // documents (e.g. hall9k_platform_hall9k-pgdata), which neither literal names.
                // Guessing which one to destroy is still refused either way — this only widens
                // what counts as "something to guess wrong about".
                (bool volumesConfirmed, IReadOnlyList<string> foundVolumes) =
                    await ContainerRuntimeProbe.FindDataVolumesAsync(runner, cancellationToken);
                if (!volumesConfirmed)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]Purge incomplete[/] — {PostgresRuntime.ContainerName} is absent, and checking "
                        + "Docker for a data volume left behind (docker volume ls) itself failed, so nothing was "
                        + "removed. Retry once Docker is answering reliably, or check yourself: docker volume ls "
                        + "--filter name=hall9k-pgdata");
                    return (false, DataTierOutcome.PurgeIncomplete);
                }

                if (foundVolumes.Count == 0)
                {
                    AnsiConsole.MarkupLine(
                        $"[dim]No {PostgresRuntime.ContainerName} container and no volume matching "
                        + "hall9k-pgdata were found — nothing to purge.[/]");
                    return (true, DataTierOutcome.ContainerAbsent);
                }

                string removalCommands = string.Join("; ", foundVolumes.Select(name => $"docker volume rm {name}"));
                AnsiConsole.MarkupLine(
                    $"[yellow]Purge incomplete[/] — {PostgresRuntime.ContainerName} is absent, so which volume "
                    + $"is really this install's cannot be confirmed by inspecting it. A volume named "
                    + $"{string.Join(" and ", foundVolumes)} exists, but it could belong to something else "
                    + "entirely (a pre-pin installed-mode Postgres, a pre-pin checkout-rooted docker compose run, "
                    + "or the pre-migration Aspire dev loop, can all carry a name like this without being this "
                    + $"install's volume at all), so it was left untouched rather than guessed at and destroyed. "
                    + $"If you are sure it is this install's, remove it by hand: {removalCommands}");
                return (false, DataTierOutcome.PurgeUnconfirmedVolume);
            }

            // Asked of the live container rather than assumed: a container brought up with a
            // bind mount, or no volume mount at all, mounts nothing docker inspect reports a
            // name for (an anonymous volume does get reported, under its generated hex name), and
            // falling back to the bare PostgresRuntime.VolumeName literal here would be the
            // identical guess the absent-container branch above refuses to make — this container
            // is present, but that does not make the literal its volume. A container can mount
            // more than one named volume, so every one it reports gets purged below — keeping
            // only the first would destroy one volume, leave the rest sitting untouched and
            // unreported, and still claim the whole install's data was gone.
            (bool volumeConfirmed, IReadOnlyList<string> volumeNames) = await ContainerRuntimeProbe.DataVolumeNameAsync(runner, cancellationToken);
            if (!volumeConfirmed)
            {
                // docker inspect itself failed — not the same fact as "no named volume mounted".
                // Removing the container now would destroy the one thing that could still answer
                // which volume is really this install's, so the container is left alone too.
                AnsiConsole.MarkupLine(
                    $"[yellow]Purge incomplete[/] — {PostgresRuntime.ContainerName} could not be inspected to "
                    + "confirm which data volume it has mounted, so nothing was removed. Retry once Docker is "
                    + $"answering reliably, or confirm the volume yourself first: docker inspect "
                    + $"{PostgresRuntime.ContainerName}");
                return (false, DataTierOutcome.PurgeIncomplete);
            }

            bool containerRemoved = await ContainerRuntimeProbe.RemoveContainerAsync(runner, cancellationToken);

            if (volumeNames.Count == 0)
            {
                AnsiConsole.MarkupLine(containerRemoved
                    ? $"[red]Purged[/]: the {PostgresRuntime.ContainerName} container is gone. It had no named "
                        + "data volume mounted (a bind mount, or no volume mount at all) for this to observe and "
                        + "destroy, so nothing there was touched."
                    : $"[yellow]Purge incomplete[/] — the container could not be removed. It also had no named "
                        + "data volume mounted for this to observe and destroy. Finish by hand: "
                        + $"docker rm -f {PostgresRuntime.ContainerName}");
                return (containerRemoved, containerRemoved ? DataTierOutcome.PurgedContainerOnly : DataTierOutcome.PurgeIncomplete);
            }

            List<string> removedVolumes = [];
            List<string> remainingVolumes = [];
            foreach (string volumeName in volumeNames)
            {
                (bool volumeCheckConfirmed, bool volumeStillExists) = await ContainerRuntimeProbe.VolumeExistsAsync(
                    runner, cancellationToken, volumeName);
                bool volumeRemoved = (volumeCheckConfirmed && !volumeStillExists)
                    || await ContainerRuntimeProbe.RemoveVolumeAsync(runner, cancellationToken, volumeName);
                (volumeRemoved ? removedVolumes : remainingVolumes).Add(volumeName);
            }

            if (containerRemoved && remainingVolumes.Count == 0)
            {
                string volumeWord = removedVolumes.Count > 1 ? "data volumes" : "data volume";
                AnsiConsole.MarkupLine(
                    $"[red]Purged[/]: the {PostgresRuntime.ContainerName} container and its "
                    + $"{string.Join(" and ", removedVolumes)} {volumeWord} are gone. Every task, run, and idea "
                    + "recorded there is gone with them.");
                return (true, DataTierOutcome.PurgedContainerAndVolume);
            }

            List<string> remedies = [];
            if (!containerRemoved)
            {
                remedies.Add($"docker rm -f {PostgresRuntime.ContainerName}");
            }
            remedies.AddRange(remainingVolumes.Select(name => $"docker volume rm {name}"));

            AnsiConsole.MarkupLine(
                $"[red]Purge incomplete[/] — {(containerRemoved ? "the container is gone" : "the container could not be removed")}, "
                + (remainingVolumes.Count > 0
                    ? $"{string.Join(" and ", remainingVolumes)} could not be removed"
                    : "every data volume is gone")
                + $". Finish by hand: {string.Join("; ", remedies)}");
            return (false, DataTierOutcome.PurgeIncomplete);
        }

        if (container == PostgresContainerStatus.Absent)
        {
            AnsiConsole.MarkupLine($"[dim]No {PostgresRuntime.ContainerName} container found — nothing to stop.[/]");
            return (true, DataTierOutcome.ContainerAbsent);
        }

        // Called unconditionally rather than only when Hall9kContainerStatusAsync's own status
        // reads Running: that probe collapses every Docker state besides "running" into Stopped,
        // including "restarting" and "paused" — an active, non-terminal state that would
        // otherwise be left alone here and come back on its own restart policy while the rest of
        // the machine is torn down around it. docker stop is idempotent against a container that
        // genuinely is already stopped (confirmed: exit 0 either way), so there is no case where
        // calling it unconditionally does anything worse than a no-op.
        bool stopped = await ContainerRuntimeProbe.StopRunningContainerAsync(runner, cancellationToken);
        AnsiConsole.MarkupLine(stopped
            ? $"[green]Stopped[/] the {PostgresRuntime.ContainerName} container. Its data volume was never touched — "
                + "everything you've recorded is exactly as you left it, in Docker rather than in this install. "
                + "Reinstalling h9k reconnects to it."
            : $"[yellow]Could not stop {PostgresRuntime.ContainerName}[/] — stop it by hand: docker stop {PostgresRuntime.ContainerName}");
        return (stopped, stopped ? DataTierOutcome.ContainerStopped : DataTierOutcome.ContainerStopFailed);
    }

    /// <summary>
    /// The reverse search of <see cref="InstallCommand.LinkOntoPath"/>: every place install
    /// might have put the h9k symlink (each PATH entry, the two Homebrew directories, and the
    /// ~/.local/bin fallback), deleting only a symlink that resolves to exactly
    /// <paramref name="target"/> — never a real file, and never a symlink pointing anywhere
    /// else, which might be an operator's own.
    /// <para>
    /// Returns <see langword="false"/> when a matching symlink was found but could not be
    /// deleted (already printed above, with the manual remedy), so a caller can fold that
    /// failure into its own exit code and summary rather than reporting the link as gone —
    /// the identical gap <see cref="RemoveInstallOwnedEntries"/> already closes for bin/ itself.
    /// </para>
    /// </summary>
    internal static bool RemoveFromPath(string target, string pathVariable, string homeDirectory)
    {
        List<string> candidates =
        [
            .. pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            "/opt/homebrew/bin",
            "/usr/local/bin",
            Path.Combine(homeDirectory, ".local", "bin"),
        ];

        bool removedAny = false;
        bool anyFailed = false;
        foreach (string directory in candidates.Distinct(StringComparer.Ordinal))
        {
            string path = Path.Combine(directory, "h9k");
            if (InstallCommand.Classify(path) != InstallCommand.PathEntry.Symlink)
            {
                continue;
            }

            FileSystemInfo? resolved = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: false);
            if (resolved is null || Path.GetFullPath(resolved.FullName) != Path.GetFullPath(target))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                AnsiConsole.MarkupLineInterpolated($"[green]Removed from PATH[/]: {path}");
                removedAny = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Could not remove {path}[/] — remove it by hand: rm {path}");
                anyFailed = true;
            }
        }

        if (!removedAny && !anyFailed)
        {
            AnsiConsole.MarkupLine("[dim]No PATH link found pointing at the installed h9k — nothing to remove there.[/]");
        }

        return !anyFailed;
    }

    /// <summary>The reverse of <see cref="InstallCommand.EnsureOnWindowsPath"/>: drop
    /// <paramref name="binDirectory"/> from the user's PATH if it is there, through the same
    /// registry seam (never <see cref="Environment.SetEnvironmentVariable(string, string?, EnvironmentVariableTarget)"/>,
    /// which would flatten any surviving <c>%VAR%</c> reference on write). Returns
    /// <see langword="false"/> only when the registry key could not be opened — the manual
    /// remedy is already printed above — so a caller can fold that failure into its own exit
    /// code and summary instead of reporting the PATH link as gone.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool RemoveFromWindowsPath(string binDirectory)
    {
        RegistryKey? environmentKey;
        try
        {
            environmentKey = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
        }
        catch (SecurityException)
        {
            environmentKey = null;
        }

        if (environmentKey is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not open HKCU\\Environment to remove h9k from your PATH[/] — remove {binDirectory} from your user PATH by hand.");
            return false;
        }

        using (environmentKey)
        {
            string current = environmentKey.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)
                as string ?? string.Empty;

            // Checked as membership, not as "did the rebuilt string change": rebuilding also
            // drops empty and whitespace-padded entries (RemoveEmptyEntries | TrimEntries), so a
            // PATH that merely had one of those — with binDirectory never on it at all — would
            // otherwise read as a removal that never happened.
            string normalized = binDirectory.TrimEnd('\\', '/');
            bool present = current
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(entry => string.Equals(entry.TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
            if (!present)
            {
                AnsiConsole.MarkupLine("[dim]Not on PATH — nothing to remove there.[/]");
                return true;
            }

            string updated = ComputeUserPathWithoutDirectory(current, binDirectory);
            environmentKey.SetValue("Path", updated, environmentKey.GetValueKind("Path"));
            InstallCommand.BroadcastEnvironmentChange();
            AnsiConsole.MarkupLineInterpolated(
                $"[green]Removed from PATH[/]: {binDirectory} (open a new terminal for it to take effect).");
            return true;
        }
    }

    /// <summary>The pure part of <see cref="RemoveFromWindowsPath"/>: drop
    /// <paramref name="directory"/> from <paramref name="currentUserPath"/> if it is there
    /// (trailing separators and casing ignored, matching <see cref="InstallCommand.ComputeUserPath"/>),
    /// leaving every other entry — including any <c>%VAR%</c> reference, any empty entry, and any
    /// surrounding whitespace — exactly as written. Splits on the raw path separator alone,
    /// deliberately without <see cref="StringSplitOptions.RemoveEmptyEntries"/> or
    /// <see cref="StringSplitOptions.TrimEntries"/>: those options are for the membership
    /// comparison only (matching <see cref="InstallCommand.ComputeUserPath"/>'s own write-side
    /// discipline of trimming to decide, never to rebuild), not for what gets rejoined — using
    /// them for reconstruction would drop every empty entry (which Windows resolves as the
    /// current directory during PATH search) and trim padding on every other entry too, not just
    /// the one actually being removed.</summary>
    internal static string ComputeUserPathWithoutDirectory(string currentUserPath, string directory)
    {
        string normalized = directory.TrimEnd('\\', '/');
        IEnumerable<string> kept = currentUserPath
            .Split(Path.PathSeparator)
            .Where(entry => !string.Equals(entry.Trim().TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
        return string.Join(Path.PathSeparator, kept);
    }

    /// <summary>
    /// Every path under <paramref name="home"/> that <c>h9k install</c> itself ever writes —
    /// the binaries (plus its staging and retired-swap scratch directories, in case an earlier
    /// install died mid-swap), the Postgres compose directory, and the daemon's own log files
    /// (both the live one and the one rotation rolled aside, since
    /// <see cref="Hall9k.Domain.Infrastructure.Storage.DaemonLogRotation"/> writes both under
    /// this same home). Deliberately not a directory listing of <paramref name="home"/>: a
    /// project's registered home (<c>projects/&lt;name&gt;</c>), <c>credentials/</c>, and the
    /// global idea/run fallback directories all live as siblings of these same entries, and none
    /// of them are install's to remove. <c>config.json</c> is deliberately not listed either,
    /// for the identical reason: install never writes it, an operator or
    /// <see cref="DatabaseDoctor"/>'s start-offer does, and it can be the only record of a
    /// hand-configured connection string — deleting it would leave a reinstall silently
    /// pointed at nothing rather than reconnecting to the database the operator set up. The
    /// canonical skill set is deliberately not listed here either — <see cref="SkillSeeder.RemovePublished"/>
    /// handles it separately, because unlike every entry below it is not safe to delete
    /// outright: an operator can and does write skills of their own straight into that same
    /// directory (see that method's own origin incident), so removing it needs the install's
    /// publish manifest, not a blind delete.
    /// <para>
    /// The daemon's pid, single-instance lock, and stop-request files are included
    /// unconditionally: this method is only ever reached once <see cref="ExecuteAsync"/> has
    /// confirmed the daemon actually stopped (a daemon that could not be stopped skips this
    /// whole removal, home and all, rather than needing this list to carve the files back out).
    /// Deleting the pid or lock file out from under a daemon that is still running would free
    /// the single-instance guard for a second daemon to start against the same database, and
    /// leave the first one with no pid file for <c>h9k daemon status</c> or <c>h9k daemon stop</c>
    /// to find it by. The stop-request file (<c>h9kd.stop</c>, Windows's stand-in for SIGTERM —
    /// see <see cref="DaemonLifecycle.RequestGracefulStopAsync"/>) is listed for the same reason
    /// the pid and lock files are: <c>WindowsStopRequestWatcher</c> normally deletes it within a
    /// tick of honoring it, but it can survive on disk when the daemon is force-killed, crashes,
    /// or the delete loses to a lock, and an uninstall that leaves it behind is not the clean
    /// <c>~/.hall9k</c> removal this command promises. <c>h9kd.stop.claimed</c> joins it for the
    /// same reason: <c>WindowsStopRequestWatcher</c> claims <c>h9kd.stop</c> onto this path
    /// before reading it and normally deletes the claimed copy within the same tick, but a read
    /// or delete that loses to a lock or a crash mid-claim leaves it behind exactly like its
    /// unclaimed sibling. <c>h9kd-autostart-launch.vbs</c> joins both for the identical reason:
    /// <see cref="WindowsDaemonAutostart.DisableAsync"/> normally
    /// deletes it too, on the same best-effort basis, and this entry is the backstop for when
    /// that delete loses to a lock or autostart was never cleanly disabled — a stray copy is not
    /// just clutter, since it can carry a captured PATH and (before this same task stopped
    /// embedding it) a Postgres connection string in plain text.
    /// </para>
    /// <para>
    /// Built from <paramref name="home"/> with the literal relative names rather than by
    /// calling <see cref="DaemonRuntime"/>/<see cref="Hall9kDatabase"/>/
    /// <see cref="PostgresRuntime"/> directly: those all resolve against the live
    /// <see cref="PlatformPaths.Home"/>, which is exactly what a caller testing this against a
    /// throwaway directory must not touch. The names are kept in sync with those types by hand,
    /// the same discipline <see cref="PostgresRuntime.ComposeFileContents"/> already uses to
    /// track the repository's own compose file.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> InstallOwnedEntries(string home, List<string> stillPresent) =>
    [
        Path.Combine(home, "bin"),
        Path.Combine(home, "bin.staging"),
        Path.Combine(home, "bin.old"),
        .. RetiredBinFallbacks(home, stillPresent),
        Path.Combine(home, "postgres"),
        Path.Combine(home, "h9kd.log"),
        Path.Combine(home, "h9kd.log.1"),
        Path.Combine(home, "h9kd.pid"),
        Path.Combine(home, "h9kd.lock"),
        Path.Combine(home, "h9kd.stop"),
        Path.Combine(home, "h9kd.stop.claimed"),
        Path.Combine(home, "h9kd-autostart-launch.vbs"),
    ];

    /// <summary>The uniquely suffixed <c>bin.old.&lt;random&gt;</c> directories
    /// <see cref="InstallCommand"/> falls back to when a locked <c>bin.old</c> already occupies
    /// the ordinary retirement name (a double-lock on Windows) — swept here for the same reason
    /// install's own next run sweeps them, so one never survives an uninstall by accident.
    /// <c>bin.old</c> itself is excluded from the results: on Windows, FileSystemName's
    /// Win32-expression translation rewrites the trailing <c>.*</c> into DOS_DOT, which also
    /// matches zero characters, so the glob below matches <c>bin.old</c> as well as its
    /// fallbacks — the same quirk <see cref="InstallCommand.SweepRetiredDirectories"/> documents
    /// and skips. <c>bin.old</c> is already listed separately in <see cref="InstallOwnedEntries"/>,
    /// so without the exclusion a locked one would appear, and be reported, twice.</summary>
    private static IEnumerable<string> RetiredBinFallbacks(string home, List<string> stillPresent)
    {
        if (!Directory.Exists(home))
        {
            return [];
        }

        string binOld = Path.Combine(home, "bin.old");
        try
        {
            return Directory.EnumerateDirectories(home, "bin.old.*")
                .Where(path => !string.Equals(path, binOld, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The read/execute bit dropped on home itself, the same point-of-no-return call
            // site (the PATH link is already gone by here) TryRemoveIfEmpty's identical
            // enumeration already guards. Reported through stillPresent rather than left to
            // escape as a raw stack trace, since a fallback this pass cannot even see is one
            // it must not silently claim never existed.
            stillPresent.Add(home);
            return [];
        }
    }

    /// <summary>
    /// Removes exactly <paramref name="entries"/>, file by file within each directory rather
    /// than in one <see cref="Directory.Delete(string, bool)"/> call, so a single locked file
    /// (typically the running h9k.exe itself, on Windows, mid-uninstall) costs exactly that
    /// file rather than aborting the whole removal before anything else was tried. Returns
    /// every path that could not be removed, empty when every install-owned entry is genuinely
    /// gone.
    /// <para>
    /// A directory Windows will not fully empty this way (again, the running h9k.exe itself:
    /// Windows will not delete an executable image mapped into a live process, the identical
    /// fact <see cref="InstallCommand.SwapIntoPlace"/> already works around by renaming rather
    /// than deleting) gets one more attempt on Windows: renaming what is left of it out from
    /// under <c>~/.hall9k</c> entirely. A rename is a directory-entry change, not a delete, so it
    /// succeeds even while the file is mapped — exactly the fact <see cref="InstallCommand.RetireDirectory"/>
    /// already relies on — and once the remainder lives outside the install home, the home
    /// itself is genuinely empty even though the locked file is still on disk somewhere, to be
    /// reclaimed by Windows once nothing has it open. Without this, an uninstall run from the
    /// installed binary itself could never fully remove the home on Windows, on every run.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> RemoveInstallOwnedEntries(IReadOnlyList<string> entries)
    {
        List<string> stillPresent = [];
        foreach (string path in entries)
        {
            if (Directory.Exists(path))
            {
                List<string> lockedUnderThisEntry = [];

                // A directory symlink or junction (a replaced bin/, or one an operator created)
                // still passes Directory.Exists, but recursing into its contents would walk
                // through to whatever it points at and delete that instead. Directory.Delete
                // unlinks a symlink without following it regardless of the target's own
                // contents, so skipping the recursion here and going straight to the delete
                // below is what keeps this to exactly the link, never the linked-to directory.
                if (new DirectoryInfo(path).LinkTarget is null)
                {
                    DeleteContentsBestEffort(path, lockedUnderThisEntry);
                }

                try
                {
                    Directory.Delete(path);
                    continue;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Could be non-empty because something under it could not be removed
                    // (recorded in lockedUnderThisEntry above) — but the directory itself can
                    // also fail to delete for a reason no per-file failure explains (no write
                    // permission on the directory entry itself, an open handle to the directory
                    // rather than a file inside it), in which case lockedUnderThisEntry stays
                    // empty even though the directory demonstrably survived. The fallback below
                    // covers that case so a still-present directory is never reported as gone.
                }

                if (OperatingSystem.IsWindows() && TryRelocateOutsideHome(path))
                {
                    continue;
                }

                stillPresent.AddRange(lockedUnderThisEntry.Count > 0 ? lockedUnderThisEntry : [path]);
            }
            else if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    stillPresent.Add(path);
                }
            }
        }

        return stillPresent;
    }

    /// <summary>Moves a directory Windows would not let this run fully empty (see
    /// <see cref="RemoveInstallOwnedEntries"/>) to the system temp directory, so it no longer
    /// counts against <c>~/.hall9k</c> even though the locked file inside it survives on disk
    /// under a different path. Reports where it went rather than leaving the operator to search
    /// for a path <see cref="ReportHomeRemoval"/> no longer names. Nothing schedules the actual
    /// delete: a rename succeeds while the file is still mapped, but there is no OS mechanism
    /// that reclaims an ordinary temp-directory entry once the handle closes — this is the same
    /// gap the operator would hit deleting it by hand right after the process exits, just with a
    /// stable, discoverable path in the meantime instead of the original one inside the removed
    /// home.</summary>
    [SupportedOSPlatform("windows")]
    private static bool TryRelocateOutsideHome(string path)
    {
        string destination = Path.Combine(
            Path.GetTempPath(), $"{Path.GetFileName(path)}.uninstalled.{Path.GetRandomFileName()}");
        try
        {
            Directory.Move(path, destination);
            AnsiConsole.MarkupLine(
                $"[dim]{path.EscapeMarkup()} still held a locked file (most likely this very h9k.exe) — moved to "
                + $"{destination.EscapeMarkup()}, outside ~/.hall9k. It is not deleted automatically; remove it "
                + "by hand once this process has exited.[/]");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteContentsBestEffort(string directory, List<string> stillPresent)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    stillPresent.Add(file);
                }
            }

            foreach (string subdirectory in Directory.EnumerateDirectories(directory))
            {
                // Same guard as the top-level entry point: a nested directory symlink or
                // junction must be unlinked, never recursed into.
                if (new DirectoryInfo(subdirectory).LinkTarget is null)
                {
                    DeleteContentsBestEffort(subdirectory, stillPresent);
                }

                try
                {
                    Directory.Delete(subdirectory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Not empty because something inside could not be removed, already recorded above.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The enumeration itself failed — a permission dropped on this directory's own
            // read/execute bit, or the directory removed out from under this walk by a
            // concurrent process — rather than a per-file failure the loops above could record
            // individually. Reported through stillPresent, the same discipline every failure in
            // this method already uses, rather than left to escape as a raw stack trace after
            // bin/ and the PATH link may already be gone.
            stillPresent.Add(directory);
        }
    }

    /// <summary>
    /// Deletes <paramref name="home"/> itself, but only when it is completely empty — the
    /// literal "a removed home is a removed home" case, reached only on a machine where
    /// nothing but install ever wrote there. A home still holding a project, a credential, or
    /// anything else is left exactly as it is; this never removes a non-empty directory.
    /// <paramref name="stillPresent"/> gains <paramref name="home"/> when this could not confirm
    /// it empty or could not delete it once confirmed — genuinely unfinished, unlike the
    /// ordinary "there is real content here" case above, which is not a failure and reports
    /// nothing — so a caller's exit code reflects the home surviving rather than reading either
    /// failure as done.
    /// </summary>
    internal static void TryRemoveIfEmpty(string home, List<string> stillPresent)
    {
        if (stillPresent.Contains(home, StringComparer.OrdinalIgnoreCase))
        {
            // Already named as still-present earlier in this same run — RetiredBinFallbacks hits
            // the identical unreadable-home condition this method's own enumeration would hit
            // again below. Re-deriving the same failure here would only list home a second time
            // (ReportHomeRemoval has no way to tell the two additions apart), not add information.
            return;
        }

        if (!Directory.Exists(home))
        {
            return;
        }

        bool empty;
        try
        {
            empty = !Directory.EnumerateFileSystemEntries(home).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable home is not the same fact as an empty one — this is the same
            // point-of-no-return call site (bin/ and the PATH link are already gone by here) that
            // made SkillSeeder.ReadManifest's identical read fail this safely rather than throw.
            // Left in place rather than guessed at and deleted.
            stillPresent.Add(home);
            return;
        }

        if (!empty)
        {
            return;
        }

        try
        {
            Directory.Delete(home);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Confirmed empty but the unlink itself was denied — left as an empty directory,
            // and recorded rather than swallowed so this run's exit code says so.
            stillPresent.Add(home);
        }
    }

    /// <summary>
    /// Reports what happened to the install-owned entries, then — only when every one of them
    /// is actually gone — removes <paramref name="home"/> itself if nothing else was ever
    /// written there. That is the literal "a removed home is a removed home" case: a machine
    /// that has run nothing but install and uninstall ends with no <c>~/.hall9k</c> at all,
    /// exactly as the walk's own reasoning describes. A machine that has also registered a
    /// project, added a credential, or captured an idea keeps that content untouched, and this
    /// says so explicitly rather than leaving an operator to wonder whether it was missed.
    /// <paramref name="skillsRemoved"/> is named explicitly rather than folded into "the skill
    /// set" as a blanket claim: <see cref="SkillSeeder.RemovePublished"/> deliberately leaves a
    /// skill alone when an operator has edited it since it was published, and a summary that
    /// says "removed" regardless would claim an outcome nobody observed.
    /// <paramref name="skillManifestConfirmed"/> is false when the manifest exists but could not
    /// be read this pass, the same condition <see cref="SkillPublication.ManifestUnconfirmed"/>
    /// reports on the install side — the entire published skill set is still on disk in that
    /// case, so the skills clause says so instead of reading the empty
    /// <paramref name="skillsRemoved"/> as "nothing was ever published".
    /// </summary>
    private static void ReportHomeRemoval(
        string home, bool homeExistedBeforeRemoval, IReadOnlyList<string> stillPresent,
        IReadOnlyList<string> skillsRemoved, bool skillManifestConfirmed)
    {
        if (stillPresent.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]Could not remove everything install owns[/]:");
            foreach (string path in stillPresent)
            {
                AnsiConsole.MarkupLine($"  [yellow]{path.EscapeMarkup()}[/]");
            }

            bool includesHome = stillPresent.Any(
                path => string.Equals(path, home, StringComparison.OrdinalIgnoreCase));
            bool includesManifest = stillPresent.Any(
                path => string.Equals(path, SkillLibraryPaths.PublishedManifest, StringComparison.OrdinalIgnoreCase));
            bool includesSkillsDirectory = stillPresent.Any(
                path => string.Equals(path, SkillLibraryPaths.CanonicalDirectory, StringComparison.OrdinalIgnoreCase));

            if (stillPresent.Any(path =>
                !string.Equals(path, home, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, SkillLibraryPaths.PublishedManifest, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, SkillLibraryPaths.CanonicalDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                AnsiConsole.MarkupLine(
                    "[dim]Still in use — most likely this very h9k, if you are running the installed binary. "
                    + "Delete it by hand once this process exits.[/]");
            }

            if (includesHome)
            {
                // Two different failures land home here — its contents could not be listed, or
                // listing confirmed it empty but the directory itself could not then be unlinked
                // — and stillPresent does not distinguish which one actually happened. Naming
                // only the first would blame a read that had, in the second case, already
                // succeeded, so both are named as the honest, unobserved-which-one truth rather
                // than asserting a specific cause that was never actually confirmed.
                AnsiConsole.MarkupLine(
                    $"[yellow]{home.EscapeMarkup()} could not be fully removed[/] — either its contents could "
                    + "not be listed, or listing confirmed it empty but deleting it was denied (both are "
                    + "permission problems, not necessarily a locked file), and this directory can hold a "
                    + "project's home, its worktrees, your credentials, and config.json, none of which install "
                    + "owns. Do not delete it by hand: fix whatever is blocking the read or the delete, and run "
                    + "h9k uninstall again.");
            }

            if (includesSkillsDirectory)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]{SkillLibraryPaths.CanonicalDirectory.EscapeMarkup()} could not be confirmed "
                    + "empty[/] — an operator can and does write skills of their own straight into that same "
                    + "directory, beside the published set, so its contents could not be told apart from those "
                    + "this pass. Do not delete it by hand: fix whatever is blocking the read and run h9k "
                    + "uninstall again.");
            }

            if (includesManifest)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]{SkillLibraryPaths.PublishedManifest.EscapeMarkup()} could not be read this pass[/] "
                    + "— do not delete it by hand: without it, a later h9k install cannot tell a published skill "
                    + "apart from one of your own, and would misclassify the whole published set as your "
                    + "overrides. Retry once whatever is holding it lets go.");
            }
        }

        string skillsClause = !skillManifestConfirmed
            ? "the skill set (left untouched — its manifest could not be read this pass, so a published skill "
                + "cannot be told apart from an operator's own file; retry once whatever is holding it lets go)"
            : skillsRemoved.Count > 0
                ? $"the skill set ({string.Join(", ", skillsRemoved).EscapeMarkup()})"
                : "the skill set (nothing to remove there — none was ever published, or what is there was edited "
                    + "since and left alone)";

        if (!Directory.Exists(home))
        {
            AnsiConsole.MarkupLine(homeExistedBeforeRemoval
                ? $"[green]Removed[/] {home.EscapeMarkup()} entirely — nothing but the install had ever written there."
                : $"[dim]{home.EscapeMarkup()} was never created[/] — there was nothing here for h9k install to "
                    + "have written, so there was nothing for this to remove.");
            return;
        }

        AnsiConsole.MarkupLine(stillPresent.Count > 0
            ? $"[yellow]Could not fully remove[/] the install's own files from {home.EscapeMarkup()} — see what's "
                + $"still there above. What did come off (of bin/, {skillsClause}, the Postgres compose file, "
                + "and the daemon's log/pid/lock files) is whatever is not listed above. What remains besides "
                + "that — a project home, config.json, credentials, anything else you or another tool put there "
                + "— is not install's to remove, and was left alone."
            : $"[green]Removed[/] the install's own files from {home.EscapeMarkup()}: bin/, {skillsClause}, "
                + "the Postgres compose file, and the daemon's log/pid/lock files. What remains there — a "
                + "project home, config.json, credentials, anything else you or another tool put there — is "
                + "not install's to remove, and was left alone.");
    }

    /// <summary>
    /// <paramref name="homeRemovalOutcome"/> is three-valued rather than a plain
    /// <see langword="bool"/> because there are three genuinely different outcomes to report,
    /// not two: <see langword="null"/> means bin/, the PATH link, and the home were never
    /// touched this run at all (a still-running daemon, or a purge <see cref="HandleDataTierAsync"/>
    /// could not carry out — both leave the whole removal untried on purpose, so the operator's
    /// remedy above still has an h9k to run it with). A <see langword="bool"/> value means the
    /// removal was attempted, and says whether everything install owns — including the PATH
    /// link, folded in by the caller alongside the home's own leftovers — actually came off.
    /// Collapsing the untried case into <see langword="true"/> would tell an operator work was
    /// removed that was never touched; collapsing it into <see langword="false"/> would print
    /// "the PATH link came off" when it was never attempted either.
    /// </summary>
    private static void PrintSummary(
        bool purgeData, DataTierOutcome dataTierOutcome, bool daemonStopped, bool? homeRemovalOutcome)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Summary[/]");

        if (!daemonStopped)
        {
            // Nothing below this applies: HandleDataTierAsync was never called, so there is no
            // Postgres outcome to report, purged or otherwise — reporting one here would be
            // exactly the unobserved claim the rest of this command works to avoid.
            AnsiConsole.MarkupLine(
                "[yellow]h9kd could not be stopped[/] — it is still running. Its autostart registration (if "
                + "any) was unregistered, but h9kd stayed up through that attempt too; everything "
                + $"else — bin/, the PATH link, config.json, Postgres{(purgeData ? " (--purge-data was not attempted)" : string.Empty)}, "
                + "and h9kd's own pid and lock files — was left exactly as it is. Stop h9kd, then run "
                + "h9k uninstall again to finish.");
            return;
        }

        AnsiConsole.MarkupLine(homeRemovalOutcome switch
        {
            null =>
                "[yellow]Not removed:[/] the purge above could not be completed, so bin/, the PATH link, and "
                    + "everything else h9k install wrote under ~/.hall9k were left exactly as they are — "
                    + "removing them here would strand the remedy above with no h9k left on this machine to "
                    + "run it with. The daemon was already stopped and its autostart registration (if any) "
                    + "already unregistered before that purge attempt ran, though, so start-at-login is gone "
                    + "even though this run stopped short. Follow the remedy above, then run h9k uninstall "
                    + "--purge-data again.",
            true =>
                "[dim]Removed from this machine:[/] the daemon (if it was running), its autostart registration "
                    + "(if any), the PATH link, and everything h9k install itself wrote under ~/.hall9k — bin/, "
                    + "the skill set, the Postgres compose file, logs. A registered project's home, config.json, "
                    + "your credentials, and anything else you put there were left alone.",
            false =>
                "[yellow]Not fully removed:[/] the daemon (if it was running) and its autostart registration (if "
                    + "any) came off, but the PATH link and/or some of what h9k install wrote under ~/.hall9k "
                    + "could not be removed — see above for exactly what is still there and how to finish by "
                    + "hand. A registered project's home, config.json, your credentials, and anything else you "
                    + "put there were left alone either way.",
        });

        if (!purgeData)
        {
            AnsiConsole.MarkupLine(dataTierOutcome switch
            {
                DataTierOutcome.NoContainerRuntime =>
                    "[dim]Left in Docker:[/] no container runtime (Docker) was found on this machine, so there "
                        + "was nothing to stop. If Postgres is running natively, or elsewhere, it is untouched "
                        + "either way — this uninstall never reaches outside Docker.",
                DataTierOutcome.ContainerRuntimeNotRunning =>
                    "[dim]Left in Docker:[/] Docker is installed but was not running, so there was nothing to "
                        + $"stop here. Start Docker and a {PostgresRuntime.ContainerName} container may well be "
                        + "sitting there untouched, with its data volume exactly as you left it.",
                DataTierOutcome.ContainerAbsent =>
                    $"[dim]Left in Docker:[/] no {PostgresRuntime.ContainerName} container was found — there was "
                        + "nothing here to stop. This tier does not look for a data volume without a container to "
                        + "inspect, so a volume left behind by an earlier `docker rm` was not touched either way. "
                        + "Check for one yourself: docker volume ls --filter name=hall9k-pgdata — and once you've "
                        + "confirmed a name is really this install's, docker volume rm <name> removes it."
                        + (homeRemovalOutcome != true
                            ? " h9k uninstall --purge-data does the same confirm-then-destroy, if you'd rather."
                            : string.Empty),
                DataTierOutcome.ContainerStopped =>
                    $"[green]Left in Docker:[/] the {PostgresRuntime.ContainerName} container (stopped) and its "
                        + "data volume, untouched. Every task, run, and idea you've recorded is safe there — a "
                        + $"later h9k install reconnects to it. To remove it yourself: docker rm -f "
                        + $"{PostgresRuntime.ContainerName} removes the container (never the volume); docker "
                        + "volume ls --filter name=hall9k-pgdata finds its data volume, and docker volume rm "
                        + "<name> removes that too, once you've confirmed the name is really this install's."
                        + (homeRemovalOutcome != true
                            ? " h9k uninstall --purge-data does the same confirm-then-destroy, if you'd rather."
                            : string.Empty),
                DataTierOutcome.ContainerStatusCheckFailed =>
                    $"[yellow]Left in Docker, status unknown:[/] whether {PostgresRuntime.ContainerName} is "
                        + "running, stopped, or absent could not be confirmed — see above for how to check it "
                        + "yourself. Nothing in Docker was touched either way, so its data volume (if any) is "
                        + "untouched and a later h9k install still reconnects to it.",
                _ =>
                    $"[yellow]Left in Docker, still running:[/] {PostgresRuntime.ContainerName} could not be "
                        + "stopped — see above for the command to stop it by hand. Its data volume was never "
                        + "touched either way, so a later h9k install still reconnects to it.",
            });
            return;
        }

        AnsiConsole.MarkupLine(dataTierOutcome switch
        {
            DataTierOutcome.NoContainerRuntime =>
                "[dim]Nothing to purge:[/] no container runtime (Docker) is installed on this machine, so this "
                    + $"install never had a {PostgresRuntime.ContainerName} container or a data volume for "
                    + "Docker to destroy.",
            DataTierOutcome.ContainerAbsent =>
                $"[dim]Nothing to purge:[/] no {PostgresRuntime.ContainerName} container and no "
                    + $"{PostgresRuntime.VolumeName} volume were found — this install never left anything in "
                    + "Docker to destroy.",
            DataTierOutcome.PurgedContainerAndVolume =>
                $"[red]Destroyed:[/] the {PostgresRuntime.ContainerName} container and its data volume — "
                    + "every task, run, and idea recorded there is gone; a fresh install's database starts "
                    + "empty. config.json, if it exists, still survives (see above) and may still name that "
                    + "now-destroyed database — check it before your next h9k install.",
            DataTierOutcome.PurgedContainerOnly =>
                $"[red]Destroyed:[/] the {PostgresRuntime.ContainerName} container — there was no separate "
                    + "data volume to destroy (either none was ever created, or it was mounted as a bind mount "
                    + "rather than a named volume), so there was nothing else here for --purge-data to remove.",
            _ => "[red]Purge did not fully complete[/] — see above for what to finish by hand.",
        });
    }
}
