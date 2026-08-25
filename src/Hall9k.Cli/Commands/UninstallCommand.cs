using System.ComponentModel;
using System.Diagnostics;
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

        bool daemonStopped = await StopDaemonAsync(cancellationToken);
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

        // Skills first, deliberately: RemovePublished hashes file contents, which on a
        // self-contained install can still need to lazily load an assembly this process has not
        // touched yet — one that lives in bin/, the very directory RemoveInstallOwnedEntries is
        // about to delete. Hashing before bin/ goes means every assembly this process could ever
        // need is loaded (or at least still on disk to load) while bin/ still exists.
        List<string> stillPresent = [];
        (IReadOnlyList<string> skillsRemoved, bool skillManifestConfirmed) = SkillSeeder.RemovePublished(stillPresent);
        stillPresent.AddRange(RemoveInstallOwnedEntries(InstallOwnedEntries(home, stillPresent)));
        bool homeFullyRemoved = stillPresent.Count == 0 && pathLinkRemoved;

        ReportHomeRemoval(home, stillPresent, skillsRemoved, skillManifestConfirmed);
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
    /// Stops whatever is running now — through the service manager first (so KeepAlive
    /// policy cannot resurrect it mid-uninstall), then the recorded pid directly, the same
    /// two-step <see cref="DaemonLifecycle.StopAsync"/> already uses for the identical
    /// "a detached daemon can win the single-instance race against launchd" case — and
    /// unregisters autostart regardless of whether that first attempt succeeded. Windows has no
    /// <see cref="DaemonLifecycle"/> path yet (its daemon lifecycle arrives with S1-14), so a
    /// daemon recorded there — started out of band, since h9k daemon start itself refuses on
    /// Windows today — is stopped directly instead of through a lifecycle that would refuse
    /// the whole call outright.
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

        bool stopped = OperatingSystem.IsWindows()
            ? await StopWindowsDaemonIfRunningAsync(cancellationToken)
            : await DaemonLifecycle.StopAsync(autostart, cancellationToken) == ExitCodes.Ok;

        if (autostart.IsSupported && autostart.IsEnabled)
        {
            DaemonAutostartDisableOutcome outcome = await autostart.DisableAsync(cancellationToken);
            stopped = stopped || outcome == DaemonAutostartDisableOutcome.DaemonStopped;

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

    // Process.Kill(entireProcessTree: true) has already asked the OS to terminate the process;
    // this is only the wait for that termination to land. A kernel-mode wait the process cannot
    // be interrupted out of (a hung network filesystem or driver I/O) would otherwise leave
    // WaitForExitAsync incomplete forever, so this is bounded the same way the Unix stop path's
    // DaemonLifecycle.StopAsync bounds its own wait (StopTimeout).
    private static readonly TimeSpan WindowsKillTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// <see cref="ArgumentException"/> (no such process id, from
    /// <see cref="Process.GetProcessById(int)"/>) and <see cref="InvalidOperationException"/>
    /// (the process exited between the probe and the kill, from <see cref="Process.Kill(bool)"/>
    /// itself) both genuinely mean "already gone" — safe to report as such. A
    /// <see cref="Win32Exception"/> from <see cref="Process.Kill(bool)"/> means the opposite:
    /// the runtime only throws it once it has confirmed the process has NOT exited, so it is a
    /// real failure to terminate (commonly access denied), never a race with the process already
    /// dying on its own. <see cref="AggregateException"/> is <see cref="Process.Kill(bool)"/>'s
    /// own wrapper for exactly that same "could not be terminated" failure when the entire-tree
    /// overload is asked to kill more than one process. Conflating either of the last two with
    /// "already exited" would tell the caller it is safe to go on and remove bin/, the PATH
    /// link, and Postgres out from under a daemon that is still very much alive.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task<bool> StopWindowsDaemonIfRunningAsync(CancellationToken cancellationToken)
    {
        DaemonProcessDescriptor? running = DaemonProcess.Probe();
        if (running is null)
        {
            AnsiConsole.MarkupLine("[dim]No running daemon.[/]");
            return true;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(running.ProcessId);
        }
        catch (ArgumentException)
        {
            AnsiConsole.MarkupLine("[dim]The running daemon had already exited.[/]");
            return true;
        }

        using (process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).WaitAsync(WindowsKillTimeout, cancellationToken);
                AnsiConsole.MarkupLineInterpolated($"[green]Stopped[/] h9kd (pid {running.ProcessId}).");
                return true;
            }
            catch (InvalidOperationException)
            {
                AnsiConsole.MarkupLine("[dim]The running daemon had already exited.[/]");
                return true;
            }
            catch (Exception exception) when (exception is Win32Exception or AggregateException)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Could not stop h9kd (pid {running.ProcessId})[/]: {exception.Message.EscapeMarkup()}. "
                    + $"Stop it by hand (Task Manager, or `taskkill /PID {running.ProcessId} /T /F`), then run "
                    + "h9k uninstall again.");
                return false;
            }
            catch (TimeoutException)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Could not stop h9kd (pid {running.ProcessId})[/]: it did not exit within "
                    + $"{WindowsKillTimeout.TotalSeconds:0}s of being killed — it may be stuck in an uninterruptible "
                    + $"wait. Stop it by hand (Task Manager, or `taskkill /PID {running.ProcessId} /T /F`), then run "
                    + "h9k uninstall again.");
                return false;
            }
        }
    }

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
        ContainerAlreadyStopped,
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

        if (container != PostgresContainerStatus.Running)
        {
            AnsiConsole.MarkupLine(container == PostgresContainerStatus.Absent
                ? $"[dim]No {PostgresRuntime.ContainerName} container found — nothing to stop.[/]"
                : $"[dim]{PostgresRuntime.ContainerName} is already stopped — its data volume is untouched.[/]");
            return (true, container == PostgresContainerStatus.Absent
                ? DataTierOutcome.ContainerAbsent
                : DataTierOutcome.ContainerAlreadyStopped);
        }

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
    /// The daemon's pid and single-instance lock files are included unconditionally: this
    /// method is only ever reached once <see cref="ExecuteAsync"/> has confirmed the daemon
    /// actually stopped (a daemon that could not be stopped skips this whole removal, home and
    /// all, rather than needing this list to carve the two files back out). Deleting either out
    /// from under a daemon that is still running would free the single-instance guard for a
    /// second daemon to start against the same database, and leave the first one with no pid
    /// file for <c>h9k daemon status</c> or <c>h9k daemon stop</c> to find it by.
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
                DeleteContentsBestEffort(path, lockedUnderThisEntry);
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
    /// for a path <see cref="ReportHomeRemoval"/> no longer names.</summary>
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
                + $"{destination.EscapeMarkup()}, outside ~/.hall9k, for Windows to reclaim once nothing has it open.[/]");
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
                DeleteContentsBestEffort(subdirectory, stillPresent);
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
    /// </summary>
    internal static void TryRemoveIfEmpty(string home)
    {
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
            // Left as an empty directory; nothing install-owned remains inside it either way.
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
        string home, IReadOnlyList<string> stillPresent, IReadOnlyList<string> skillsRemoved, bool skillManifestConfirmed)
    {
        bool homeExistedBeforeRemoval = Directory.Exists(home);

        if (stillPresent.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]Could not remove everything install owns[/]:");
            foreach (string path in stillPresent)
            {
                AnsiConsole.MarkupLine($"  [yellow]{path.EscapeMarkup()}[/]");
            }

            AnsiConsole.MarkupLine(
                "[dim]Still in use — most likely this very h9k, if you are running the installed binary. "
                + "Delete it by hand once this process exits.[/]");
        }

        TryRemoveIfEmpty(home);

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
                        + "inspect, so a volume left behind by an earlier `docker rm` was not touched either way; "
                        + "h9k uninstall --purge-data is what finds and removes one.",
                DataTierOutcome.ContainerAlreadyStopped or DataTierOutcome.ContainerStopped =>
                    $"[green]Left in Docker:[/] the {PostgresRuntime.ContainerName} container (stopped) and its "
                        + "data volume, untouched. Every task, run, and idea you've recorded is safe there — a "
                        + "later h9k install reconnects to it. Run h9k uninstall --purge-data if you want that "
                        + "gone too.",
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
            DataTierOutcome.ContainerAbsent =>
                $"[dim]Nothing to purge:[/] no {PostgresRuntime.ContainerName} container and no "
                    + $"{PostgresRuntime.VolumeName} volume were found — this install never left anything in "
                    + "Docker to destroy.",
            DataTierOutcome.PurgedContainerAndVolume =>
                $"[red]Destroyed:[/] the {PostgresRuntime.ContainerName} container and its data volume — "
                    + "nothing survives from this install; a fresh install starts from nothing.",
            DataTierOutcome.PurgedContainerOnly =>
                $"[red]Destroyed:[/] the {PostgresRuntime.ContainerName} container — there was no separate "
                    + "data volume to destroy (either none was ever created, or it was mounted as a bind mount "
                    + "rather than a named volume), so there was nothing else here for --purge-data to remove.",
            _ => "[red]Purge did not fully complete[/] — see above for what to finish by hand.",
        });
    }
}
