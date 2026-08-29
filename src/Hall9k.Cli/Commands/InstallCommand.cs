using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Microsoft.Win32;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Publish-and-refresh installation (Decisions Log #31, backlog 42): binaries into
/// ~/.hall9k/bin, h9k on the PATH, the canonical skill set at ~/.hall9k/skills, and —
/// deliberately — no background service, no login item, no autostart of any kind.
/// Re-running after a merge (or a release) republishes idempotently and offers to
/// restart a running daemon, which is the answer to installed-binary staleness
/// (origin incident: the hand-made h9k symlink went stale the moment main advanced).
/// <para>
/// Two sources feed the same publish-and-refresh: <c>--repo</c> builds locally with
/// <c>dotnet publish</c> (the dev-loop and hand-run path, needs the .NET SDK), and
/// <c>--from-release</c> stages an already-downloaded, already-verified release payload
/// (what the bootstrap scripts and <see cref="UpdateCommand"/> use — no SDK, no repo
/// checkout, on a bare machine). <see cref="FinishAsync"/> is everything after staging
/// and is shared by both.
/// </para>
/// </summary>
public sealed class InstallCommand : Hall9kAsyncCommand<InstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--repo <PATH>")]
        [Description("The hall9k repository root to publish from — the directory holding Hall9k.slnx, taken as given and never searched upward (default: found by walking up from the current directory unless --from-release is given)")]
        public string? Repo { get; init; }

        [CommandOption("--from-release <DIR>")]
        [Description("Install from an already-downloaded, already-extracted release payload (h9k/h9kd binaries, a skills/ directory, a VERSION file) instead of building from --repo — what the bootstrap scripts and h9k update use, so a bare machine needs neither a repo checkout nor the .NET SDK")]
        public string? FromRelease { get; init; }

        [CommandOption("--restart")]
        [Description("Restart a running daemon onto the fresh binaries without asking")]
        public bool Restart { get; init; }

        [CommandOption("--no-restart")]
        [Description("Leave a running daemon on its current binaries (it picks up the new ones at its next start)")]
        public bool NoRestart { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        string staging = DaemonRuntime.StagingBinDirectory;
        TryDelete(staging);

        string version;
        string? skillsSource;
        string? connectionStringStartDirectory = null;
        if (settings.FromRelease is not null)
        {
            string? problem = ValidateReleasePayload(settings.FromRelease);
            if (problem is not null)
            {
                await Console.Error.WriteLineAsync(problem);
                return ExitCodes.Error;
            }

            StageFromRelease(settings.FromRelease, staging, cancellationToken);
            // No relationship to the binaries being placed — a payload with no VERSION
            // marker gets the same honest "unknown" UpdateCommand.RunAsync uses for the
            // identical gap, not the running CLI's own version.
            version = ReadVersionFile(settings.FromRelease) ?? "unknown";
            skillsSource = Path.Combine(settings.FromRelease, "skills");
        }
        else
        {
            string? repoRoot = ResolveRepositoryRoot(settings.Repo);
            if (repoRoot is null)
            {
                await Console.Error.WriteLineAsync(DescribeMissingRepository(settings.Repo));
                return ExitCodes.Error;
            }

            foreach (string project in new[] { "Hall9k.Cli", "Hall9k.Daemon" })
            {
                AnsiConsole.MarkupLineInterpolated($"[dim]Publishing {project} (Release)…[/]");
                ExecResult publish = await Exec.RunAsync(
                    "dotnet",
                    ["publish", Path.Combine(repoRoot, "src", project), "-c", "Release", "-o", staging, "--nologo"],
                    cancellationToken);
                if (!publish.Succeeded)
                {
                    await Console.Error.WriteLineAsync($"dotnet publish failed for {project}:");
                    await Console.Error.WriteLineAsync(publish.StandardOutput);
                    await Console.Error.WriteLineAsync(publish.StandardError);
                    return ExitCodes.Error;
                }
            }

            // Not CliVersion.Current — that's the version of the *running* CLI, which has
            // no relationship to what dotnet publish just built (the checkout's csproj
            // carries the placeholder 0.1.0 outside of release.yml's -p:Version). Read
            // it back off the binary actually staged, the same refusal-to-guess as the
            // --from-release branch's own "unknown" fallback above.
            version = ReadPublishedVersion(staging);
            skillsSource = Path.Combine(repoRoot, ".claude", "skills");
            // The project-override tier walks up from a start directory (Hall9kDatabase.Resolve),
            // and for --repo that directory is repoRoot, not wherever this process happens to be
            // invoked from — passing the two apart let an out-of-tree `h9k install --repo <path>`
            // miss its own project's .hall9k-connection file entirely (cycle-1 review).
            connectionStringStartDirectory = repoRoot;
        }

        return await FinishAsync(
            staging,
            skillsSource,
            version,
            settings.Restart,
            settings.NoRestart,
            writeDefaultConnectionStringIfUnconfigured: true,
            connectionStringStartDirectory: connectionStringStartDirectory,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Everything after staging is ready, shared by <c>h9k install --repo</c>,
    /// <c>h9k install --from-release</c>, and <see cref="UpdateCommand"/>: swap the
    /// staged binaries into place, write Hall9k's own Postgres definition, republish the
    /// canonical skill set, put h9k on the PATH, report the version placed, and — if a
    /// daemon was already running — offer the restart.
    /// </summary>
    internal static async Task<int> FinishAsync(
        string staging,
        string? skillsSource,
        string version,
        bool restart,
        bool noRestart,
        bool linkOntoPath = true,
        bool writeDefaultConnectionStringIfUnconfigured = false,
        string? connectionStringStartDirectory = null,
        Func<CancellationToken, Task<bool>>? portListeningProbe = null,
        string? currentDirectoryOverride = null,
        CancellationToken cancellationToken = default)
    {
        // The actual last point before staging becomes ~/.hall9k/bin, run for every caller —
        // ExecuteAsync's --repo branch included, which stages straight from `dotnet publish`
        // and never goes through StageFromRelease's own filtering. Directory.Build.targets is
        // meant to keep a Development settings file out of that publish output in the first
        // place, but only for a checkout that carries the fix (an older branch, a stray
        // worktree, a hand-assembled payload will not), so this is the guard that still holds
        // when it doesn't (origin incident: found sitting in ~/.hall9k/bin, 2026-08-24).
        try
        {
            RemoveDevelopmentSettingsFiles(staging, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            // Caught here rather than at each of FinishAsync's two callers (ExecuteAsync's
            // --repo/--from-release branches and UpdateCommand.RunAsync) so both get the
            // mapped exit code and remedy from one place, the same as the two
            // WindowsDaemonAutostart throws are caught at their own call sites.
            await Console.Error.WriteLineAsync(exception.Message);
            return ExitCodes.Error;
        }

        DaemonProcessDescriptor? runningBefore = DaemonProcess.Probe();

        // Ships Hall9k's own Postgres definition into ~/.hall9k (Decisions Log #73), so
        // h9k daemon start's reachability probe and h9k doctor's start-offer never need a
        // repo checkout — an installed user has no dev worktree to run compose from. No
        // prompt and nothing started here: install stays boring (Decisions Log #58).
        PostgresRuntime.WriteComposeFile();
        AnsiConsole.MarkupLine(
            $"[dim]Wrote Hall9k's own Postgres definition to {PostgresRuntime.ComposeFile.EscapeMarkup()} "
            + "(not started — h9k doctor or h9k daemon start will offer to when it's needed).[/]");

        if (writeDefaultConnectionStringIfUnconfigured)
        {
            await WriteDefaultConnectionStringIfUnconfiguredAsync(
                connectionStringStartDirectory, portListeningProbe, currentDirectoryOverride, cancellationToken);
        }

        if (skillsSource is not null)
        {
            PublishSkills(skillsSource);
        }

        // linkOntoPath defaults true for both real callers; a test passes false to skip
        // it, because this step mutates the REAL process PATH and home directory (a
        // real symlink in a real /opt/homebrew/bin or ~/.local/bin) — there is no safe
        // way to redirect it without redirecting those two env vars process-wide, which
        // would race any concurrently running test that shells out to git/gh/docker via
        // PATH (origin incident: an early version of UpdateCommandTests did exactly
        // that and broke GitWorktreeManagerTests intermittently by wiping PATH out from
        // under them). LinkOntoPath and ComputeUserPath already have direct unit
        // coverage with fake paths in InstallCommandTests, so skipping this step in a
        // higher-level test loses no coverage.
        if (linkOntoPath)
        {
            if (OperatingSystem.IsWindows())
            {
                EnsureOnWindowsPath(DaemonRuntime.BinDirectory);
            }
            else
            {
                LinkOntoPath(
                    Path.Combine(DaemonRuntime.BinDirectory, "h9k"),
                    Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }
        }

        // Deliberately last: h9k update runs from the very binaries this call is about to
        // replace, and the runtime resolves an assembly's first load lazily by absolute
        // path — so anything this process still had to load after an earlier swap would
        // load from the new build instead of the one it actually started under. Nothing
        // above touches an assembly this process has not already loaded (the compose
        // file, the skills directory, and the PATH are all just files and environment
        // state), so swapping last means everything that can crash on a version mismatch
        // already ran under the version that is still valid to run it.
        try
        {
            SwapIntoPlace(staging, DaemonRuntime.BinDirectory);
        }
        catch (InvalidOperationException exception)
        {
            // What SwapIntoPlace can throw on Windows, reported instead of an unhandled
            // IOException so the operator gets a sentence instead of a stack trace (origin
            // incident: exactly that stack trace, Brian's first real Windows update): a lock
            // stronger than a running module's own share-delete access (an antivirus scan
            // holding a file exclusively) surfacing while retiring a conflicting file or while
            // placing a staged one, or a directory junction, symlink, file, or directory
            // conflicting with what staging ships that could not be cleared before either phase
            // started. The first two roll back completely — a lock during retirement undoes
            // every retirement already made; a lock during placement undoes every placement AND
            // every retirement already made, exactly as fully — so bin holds every file the old
            // release shipped, exactly as this method left it, short of the vanishingly rare
            // case where undoing an earlier placement or retirement in this same run hits a
            // second, independent lock of its own (that file is cleaned up automatically by a
            // future install or update instead). An unclearable conflict is caught before
            // anything is retired or placed, and a conflicting directory it clears along the way
            // is retired aside rather than deleted (see ClearConflictingDestinationEntry), so
            // that case is non-destructive too — the one exception is a conflicting file an
            // earlier entry in the same walk already deleted outright before this one's own
            // conflict proved unclearable: a single file, replaced by simply running the command
            // again, not the broken half-state a partial directory delete used to risk (origin:
            // cycle-4 pre-PR adversarial review).
            // RemoveStaleFiles only runs once every phase has fully succeeded, so it never sees
            // a partial merge either. The messages below describe only what SwapFilesIntoPlace
            // itself did or did not do, which on this path is bin's whole condition, short of
            // that one file.
            await Console.Error.WriteLineAsync(exception.Message);
            return ExitCodes.Error;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Installed[/] {version}: h9k and h9kd release binaries in {DaemonRuntime.BinDirectory}");

        AnsiConsole.MarkupLine(
            "[dim]No background service was registered — the daemon runs on demand (h9k daemon start / stop). "
            + "Start-at-login is a separate, explicit opt-in: h9k daemon autostart enable.[/]");

        return runningBefore is null
            ? ExitCodes.Ok
            : await OfferRestartAsync(restart, noRestart, runningBefore, cancellationToken);
    }

    /// <summary>
    /// The compose file just written above fully determines what a first-time
    /// <c>h9k doctor</c> or <c>h9k daemon start</c> would find at
    /// <see cref="Hall9kDatabase.DefaultConnectionString"/>, so a machine with nothing
    /// configured yet gets that answer recorded up front rather than left to fail
    /// <c>h9k doctor</c>'s first question for no reason a fresh install couldn't already
    /// see (Windows install friction log item 1: config.json was left empty and doctor's
    /// first run failed with "No connection string is configured" on a machine whose
    /// compose file already said exactly what that string should be). Never runs when
    /// something already resolves — the environment variable, the platform config file, or
    /// a per-project override file all outrank a value install would only be guessing
    /// matches what the operator actually wants; a malformed config file is left alone too,
    /// since repairing it is <c>h9k doctor</c>'s own diagnosis to make, not install's to
    /// paper over.
    /// <para>
    /// <paramref name="startDirectory"/> is where the per-project override tier's walk-up
    /// begins (<c>--repo</c> passes its resolved repo root, the actual project this install
    /// concerns, rather than leaving it to whatever directory the process happens to be
    /// invoked from); <c>null</c> falls back to the current directory, same as any other
    /// command's connection-string resolution. <c>--from-release</c> has no project checkout
    /// to anchor to at all — that gap, and why this write only ever runs from <c>h9k
    /// install</c> and never from <see cref="UpdateCommand"/>, is explained inline where
    /// <see cref="UpdateCommand.RunAsync"/> calls <see cref="FinishAsync"/> (cycle-1 review:
    /// an operator relying purely on a <c>.hall9k-connection</c> override, on a machine
    /// installed before this write existed, could otherwise have it permanently shadowed by a
    /// plain `h9k update` run from outside their project — the platform config file this
    /// writes outranks the override unconditionally, everywhere, forever, once written).
    /// </para>
    /// <para>
    /// The override tier is the one part of the precedence chain that is directory-dependent,
    /// so <paramref name="startDirectory"/> alone is not enough to call nothing configured:
    /// <c>--repo</c> names the repository being published <em>from</em>, which is not
    /// necessarily the directory the operator is actually sitting in, and checking only one
    /// of the two missed the other's own override (cycle-1 review, both directions — the
    /// walk from <c>repoRoot</c> alone misses an override the operator's actual working
    /// directory carries, exactly the shape <c>UpdateCommand</c>'s comment above already
    /// warns about for the wider case). The current working directory is always checked in
    /// addition to whatever <paramref name="startDirectory"/> names. This narrows the gap; it
    /// does not close it — an override that sits under neither directory is still invisible
    /// to this check, the same inherent limit <c>UpdateCommand</c> disables the write over
    /// entirely rather than guess past.
    /// </para>
    /// <paramref name="portListeningProbe"/> stands in for the real port-5432 check below —
    /// injectable so a test can assert the unconfigured-machine path without depending on
    /// whether the host actually running the test suite happens to have Postgres on 5432
    /// (a dev machine running this repository's own <c>docker compose</c> Postgres, say);
    /// <c>null</c> uses the real <see cref="ContainerRuntimeProbe.PortListeningAsync"/> check.
    /// <paramref name="currentDirectoryOverride"/> stands in for the real
    /// <see cref="Directory.GetCurrentDirectory"/> consulted below, for the identical reason:
    /// without a seam of its own, a test asserting this method's outcome depends on wherever
    /// the test host process actually happens to be running from, which can carry its own
    /// <c>.hall9k-connection</c> override (a contributor's checkout root, or an ancestor of it)
    /// and make the test's result depend on that machine's layout rather than on the scenario
    /// under test (cycle-6 review); <c>null</c> uses the real current directory.
    /// </summary>
    private static async Task WriteDefaultConnectionStringIfUnconfiguredAsync(
        string? startDirectory,
        Func<CancellationToken, Task<bool>>? portListeningProbe,
        string? currentDirectoryOverride,
        CancellationToken cancellationToken)
    {
        ConnectionStringResolution resolution = Hall9kDatabase.Resolve(startDirectory: startDirectory);
        if (resolution.Origin != ConnectionStringOrigin.None)
        {
            return;
        }

        string currentDirectory = currentDirectoryOverride ?? Directory.GetCurrentDirectory();
        if (startDirectory is not null
            && !ProjectHomePaths.SameDirectory(Path.GetFullPath(startDirectory), Path.GetFullPath(currentDirectory))
            && Hall9kDatabase.Resolve(startDirectory: currentDirectory).Origin != ConnectionStringOrigin.None)
        {
            return;
        }

        // A machine with something already listening on the default port is not "nothing
        // configured" in the sense this write means to fix — it may be a native Postgres the
        // operator already runs there (an explicitly supported deployment: docs/operations.md
        // takes no position on where Postgres runs, and h9k doctor names this exact case).
        // Writing install's own compose credentials over that would replace doctor's honest
        // "something is already listening" diagnosis with a manufactured authentication
        // failure against a credential install itself invented (cycle-1 review; AGENTS.md:
        // never guess at unobserved facts). Leave it to h9k doctor's own diagnosis instead.
        Func<CancellationToken, Task<bool>> probe = portListeningProbe
            ?? (token => ContainerRuntimeProbe.PortListeningAsync("localhost", 5432, token));
        if (await probe(cancellationToken))
        {
            AnsiConsole.MarkupLine(
                "[dim]Something is already listening on localhost:5432 — left unconfigured rather than "
                + $"guessing it is safe to write {Hall9kDatabase.DefaultConnectionString.EscapeMarkup()} there. "
                + $"Run h9k doctor to diagnose what is listening, or set {Hall9kDatabase.EnvironmentVariableName} "
                + "yourself if it is already your Postgres.[/]");
            return;
        }

        try
        {
            await Hall9kDatabase.WriteConfiguredConnectionStringAsync(Hall9kDatabase.DefaultConnectionString, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A convenience write, not install's job. Every neighbouring step in FinishAsync
            // that touches the filesystem — RemoveDevelopmentSettingsFiles, TryDelete,
            // EnsureOnWindowsPath, PublishSkills — already reports and continues rather than
            // taking the whole install down over it. An I/O failure here (a full disk,
            // tightened permissions, an antivirus/indexer holding config.json mid-write on
            // Windows) would otherwise abort FinishAsync before SwapIntoPlace runs, leaving
            // freshly published binaries stranded in bin.staging (cycle-4 review).
            AnsiConsole.MarkupLine(
                $"[dim]Could not write the connection string to {Hall9kDatabase.ConfigFile.EscapeMarkup()} "
                + $"({exception.Message.EscapeMarkup()}) — h9k doctor will offer to configure it instead.[/]");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[dim]Wrote the matching connection string to {Hall9kDatabase.ConfigFile.EscapeMarkup()} — "
            + "nothing was configured yet, and this is what the compose file above stands up.[/]");
    }

    /// <summary>The binary names --from-release must carry for this platform: h9k/h9kd on
    /// Unix, h9k.exe/h9kd.exe on Windows.</summary>
    internal static string BinaryFileName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    internal static string? ValidateReleasePayload(string fromRelease)
    {
        if (!Directory.Exists(fromRelease))
        {
            return $"No release payload at {Path.GetFullPath(fromRelease)} — --from-release names a directory "
                + "already extracted from a release archive (h9k/h9kd binaries, a skills/ directory, a VERSION file).";
        }

        List<string> missing = [];
        foreach (string binary in new[] { "h9k", "h9kd" })
        {
            if (!File.Exists(Path.Combine(fromRelease, BinaryFileName(binary))))
            {
                missing.Add(BinaryFileName(binary));
            }
        }

        return missing.Count == 0
            ? null
            : $"{Path.GetFullPath(fromRelease)} is missing {string.Join(" and ", missing)} — "
              + "not a complete release payload for this platform.";
    }

    /// <summary>The version actually embedded in the binary <c>--repo</c> just published to
    /// <paramref name="staging"/>, read from the managed assembly's own PE version resource
    /// (present on every platform .NET publishes to, since a managed assembly is always a
    /// PE file) rather than assumed from the running CLI — the same "read what was actually
    /// produced" discipline <see cref="ReadVersionFile"/> applies to a release payload.</summary>
    internal static string ReadPublishedVersion(string staging)
    {
        string assemblyPath = Path.Combine(staging, "h9k.dll");
        if (!File.Exists(assemblyPath))
        {
            return "unknown";
        }

        string? productVersion = FileVersionInfo.GetVersionInfo(assemblyPath).ProductVersion;
        if (string.IsNullOrEmpty(productVersion))
        {
            return "unknown";
        }

        int metadataStart = productVersion.IndexOf('+');
        return metadataStart < 0 ? productVersion : productVersion[..metadataStart];
    }

    internal static string? ReadVersionFile(string fromRelease)
    {
        string versionFile = Path.Combine(fromRelease, "VERSION");
        if (!File.Exists(versionFile))
        {
            return null;
        }

        string content = File.ReadAllText(versionFile).Trim();
        return content.Length == 0 ? null : content;
    }

    /// <summary>Copies the platform's binaries (and every other published file — DLLs,
    /// runtimeconfig, native host, satellite-resource subdirectories — but not the skills/
    /// subdirectory, the VERSION marker, or a Development settings file, none of which belongs
    /// in ~/.hall9k/bin) from an extracted release payload into staging. The Development check
    /// here is belt-and-suspenders on top of the release workflow's own `find -iname` gate,
    /// which already refuses to ship a payload carrying one; <see cref="RemoveDevelopmentSettingsFiles"/>,
    /// run on staging itself by <see cref="FinishAsync"/> regardless of which branch fed it, is
    /// the actual last point before a file would land on an installed machine.
    /// Checked per file rather than left to run to completion: the payload is a
    /// self-contained publish of two apps, tens of megabytes, and the zip-extraction step
    /// immediately before this one earned its own per-entry cancellation check for the same
    /// reason (60bc393).</summary>
    internal static void StageFromRelease(string fromRelease, string staging, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(staging);
        foreach (string file in Directory.EnumerateFiles(fromRelease))
        {
            string fileName = Path.GetFileName(file);
            if (fileName is "VERSION" || IsDevelopmentSettingsFile(fileName))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(staging, fileName), overwrite: true);
        }

        foreach (string sourceDirectory in Directory.EnumerateDirectories(fromRelease))
        {
            if (Path.GetFileName(sourceDirectory) is "skills")
            {
                continue;
            }

            CopyDirectoryRecursively(sourceDirectory, Path.Combine(staging, Path.GetFileName(sourceDirectory)), cancellationToken);
        }
    }

    private static void CopyDirectoryRecursively(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
        {
            string fileName = Path.GetFileName(file);
            if (IsDevelopmentSettingsFile(fileName))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destinationDirectory, fileName), overwrite: true);
        }

        foreach (string nested in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectoryRecursively(nested, Path.Combine(destinationDirectory, Path.GetFileName(nested)), cancellationToken);
        }
    }

    /// <summary>The staging-level backstop <see cref="FinishAsync"/> runs before every
    /// <see cref="SwapIntoPlace"/>, regardless of which branch of <see cref="ExecuteAsync"/> (or
    /// <see cref="UpdateCommand"/>) produced staging — including the local `dotnet publish`
    /// path, which never goes through <see cref="StageFromRelease"/>'s own filtering and depends
    /// on Directory.Build.targets alone to keep the file out in the first place. Recursive
    /// because a nested project directory can carry its own settings file, and checked per file —
    /// same as <see cref="StageFromRelease"/> and <see cref="CopyDirectoryRecursively"/> on the
    /// same tree — rather than left to run the whole walk uninterruptibly.</summary>
    internal static void RemoveDevelopmentSettingsFiles(string staging, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(staging))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsDevelopmentSettingsFile(Path.GetFileName(file)))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Could not remove {file} from staging before it could reach {DaemonRuntime.BinDirectory} — "
                        + "something else has it open (an antivirus scan, an indexer). Close whatever holds it "
                        + "and retry; nothing has been swapped into place yet.",
                    exception);
            }
        }
    }

    /// <summary>A literal <c>.Development.</c> segment anywhere in the name, matched without
    /// regard to case — <c>appsettings.Development.json</c> today, whatever sibling gets the
    /// same treatment tomorrow, and a stray <c>appsettings.DEVELOPMENT.json</c> alongside it. A
    /// bare <c>Development.json</c> does not match (nothing precedes the dot) and is not treated
    /// as a Development settings file either. This is deliberately broader than
    /// Directory.Build.targets' own <c>**\*.Development.*</c> / <c>**\*.development.*</c> globs,
    /// which only cover those two literal casings (MSBuild's Update comparison does not fold
    /// case on a case-sensitive filesystem) — the gap between them is why this check, not that
    /// one, is what backs <see cref="RemoveDevelopmentSettingsFiles"/>, the layer that actually
    /// sits closest to ~/.hall9k/bin. It matches the release workflow's `find -iname` exactly.</summary>
    private static bool IsDevelopmentSettingsFile(string fileName) =>
        fileName.Contains(".Development.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Windows has no PATH-directory convention the way Unix does (no /usr/local/bin every
    /// shell already searches), and creating a symlink there needs Developer Mode or
    /// elevation a bare machine may not have — so instead of the Unix retarget-a-symlink
    /// dance, ~/.hall9k/bin is prepended straight onto the user's PATH environment variable
    /// (HKCU\Environment, no elevation needed), idempotently.
    /// <para>
    /// Read and written through the registry directly rather than through
    /// <see cref="Environment.GetEnvironmentVariable(string, EnvironmentVariableTarget)"/> /
    /// <see cref="Environment.SetEnvironmentVariable(string, string?, EnvironmentVariableTarget)"/>:
    /// those expand the value on read and write it back as <c>REG_SZ</c> on write, which
    /// would permanently flatten any <c>%VAR%</c> reference (<c>%JAVA_HOME%\bin</c> and
    /// the like) already sitting in the user's PATH the first time this command touches it.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static void EnsureOnWindowsPath(string binDirectory)
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
                $"[yellow]Could not open HKCU\\Environment to add h9k to your PATH[/] — add {binDirectory} to your user PATH by hand, or call h9k by its full path.");
            return;
        }

        using (environmentKey)
        {
            string current = environmentKey.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)
                as string ?? string.Empty;
            string updated = ComputeUserPath(current, binDirectory);
            if (updated == current)
            {
                AnsiConsole.MarkupLineInterpolated($"[green]Already on PATH[/]: {binDirectory}");
                return;
            }

            // Preserve whatever kind the value already carried (REG_EXPAND_SZ is Windows's own
            // default for the user PATH) so existing %VAR% references survive the round trip;
            // only a PATH this account never had before defaults to REG_SZ.
            RegistryValueKind kind = current.Length == 0 ? RegistryValueKind.String : environmentKey.GetValueKind("Path");
            environmentKey.SetValue("Path", updated, kind);

            // SetValue alone only changes the registry; nothing already running (this
            // process's own parent shell included) sees the new PATH until it re-reads its
            // environment block, which happens only on WM_SETTINGCHANGE or a fresh logon —
            // broadcasting it here is the difference between "a new terminal picks this up"
            // being true or merely promised (the Unix side's equivalent defect, fixed in
            // fa01ee6, for the registry instead of a shell rc file).
            BroadcastEnvironmentChange();
            AnsiConsole.MarkupLineInterpolated(
                $"[green]Added to PATH[/]: {binDirectory} (open a new terminal for it to take effect).");
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        string lParam,
        uint flags,
        uint timeoutMilliseconds,
        out UIntPtr result);

    /// <summary>Tells every top-level window — Explorer included — that an environment
    /// variable changed, the same broadcast <see cref="Environment.SetEnvironmentVariable"/>
    /// sends and that a direct registry write skips. Best-effort: a process that never
    /// answers the broadcast (the two-second timeout) does not fail the install over a
    /// PATH refresh it was always going to need a new terminal for anyway.
    /// <para>Internal rather than private: <see cref="UninstallCommand"/> reverses this same
    /// registry write and needs the identical broadcast on the way out.</para></summary>
    [SupportedOSPlatform("windows")]
    internal static void BroadcastEnvironmentChange()
    {
        const int HWND_BROADCAST = 0xffff;
        const uint WM_SETTINGCHANGE = 0x001a;
        const uint SMTO_ABORTIFHUNG = 0x0002;

        _ = SendMessageTimeout(
            HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 2000, out _);
    }

    /// <summary>The pure part of <see cref="EnsureOnWindowsPath"/>: prepend
    /// <paramref name="directory"/> to <paramref name="currentUserPath"/> unless it is
    /// already there (trailing separators and casing ignored, as Windows path comparison is).</summary>
    internal static string ComputeUserPath(string currentUserPath, string directory)
    {
        string normalized = directory.TrimEnd('\\', '/');
        bool alreadyPresent = currentUserPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => string.Equals(entry.TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
        if (alreadyPresent)
        {
            return currentUserPath;
        }

        return currentUserPath.Length == 0 ? directory : directory + Path.PathSeparator + currentUserPath;
    }

    /// <summary>
    /// Name the directory actually looked in and the search actually run. --repo names the
    /// repository root itself and is never walked upward, so describing the upward walk for
    /// a failed --repo — and then prescribing --repo as the remedy — sends an operator (or
    /// an agent self-correcting from the message, per the CLI standard in AGENTS.md) round
    /// the same failing command.
    /// </summary>
    internal static string DescribeMissingRepository(string? configured) =>
        configured is null
            ? $"No hall9k repository found: no Hall9k.slnx in {Directory.GetCurrentDirectory()} "
              + "or any directory above it. Run from inside the repo, or point at its root with --repo <path>."
            : $"No hall9k repository at {Path.GetFullPath(configured)}: no Hall9k.slnx in that directory. "
              + "--repo takes the repository root itself (the directory holding Hall9k.slnx) and is not "
              + "searched upward — pass the root, or drop --repo and run from inside the repo.";

    private static string? ResolveRepositoryRoot(string? configured)
    {
        DirectoryInfo? candidate = new(Path.GetFullPath(configured ?? Directory.GetCurrentDirectory()));
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "Hall9k.slnx")))
            {
                return candidate.FullName;
            }

            candidate = configured is null ? candidate.Parent : null;
        }

        return null;
    }

    /// <summary>
    /// The install owns the canonical skill set (IDEA-skill-layer, Tension 8): a skill the
    /// machinery requires in order to work is part of the machinery, so it ships with the install
    /// and is as self-contained as the binaries. The source is this repository's own
    /// <c>.claude/skills</c> for <c>--repo</c>, or a release payload's bundled <c>skills/</c> for
    /// <c>--from-release</c> and <see cref="UpdateCommand"/> — only <paramref name="source"/>
    /// changes; <see cref="SkillLibraryPaths.CanonicalDirectory"/> is the seam every project home
    /// already points at.
    /// <para>
    /// A project home's <c>skills/</c> entries are symlinks into that directory, so republishing
    /// here updates every project's platform skills in one move. A skill that is <em>new</em>
    /// still needs a link made for it, which is what <c>h9k project init</c> does — so that is
    /// said rather than left to be discovered.
    /// </para>
    /// </summary>
    /// <remarks>The copying itself is <see cref="SkillSeeder.PublishCanonical"/>; this is the
    /// command's half of it, which is finding the source and saying what happened.</remarks>
    private static void PublishSkills(string source)
    {
        string canonical = SkillLibraryPaths.CanonicalDirectory;
        if (!Directory.Exists(source))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]No skills to publish[/]: {source} does not exist, so {canonical} was left alone.");
            return;
        }

        SkillPublication publication = SkillSeeder.PublishCanonical(source);
        if (publication.ManifestUnconfirmed)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Skills not published this run[/]: {SkillLibraryPaths.PublishedManifest.EscapeMarkup()} "
                + "exists but could not be read (likely held open by another process — an antivirus scan or an "
                + "editor). Without it, an already-published skill cannot be told apart from one you wrote "
                + "yourself, so nothing in the canonical set was published, retired, or classified as an override "
                + "this pass. Run h9k install again once it's free.");
            return;
        }

        string names = string.Join(", ", publication.Published);

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Skills[/]: {publication.Published.Count} published to {canonical} ({names})");

        // Named rather than counted: this is the one thing here that removes content, and a
        // number tells nobody which skill their project homes just lost a link to.
        if (publication.Retired.Count > 0)
        {
            string retired = string.Join(", ", publication.Retired).EscapeMarkup();
            AnsiConsole.MarkupLine(
                $"[yellow]Skills retired[/]: {retired} — this install no longer ships them, so they "
                + "were removed from the canonical set. Project homes drop their links at the next "
                + "h9k project init. Anything you wrote into that directory yourself is left alone: "
                + "only what an install published is an install's to retire.");
        }

        // The mirror image of the line above: nothing was destroyed, but the platform's version of
        // these is not what any agent will read, and a shadow nobody is told about is found out
        // later as the skill that would not update no matter how many times somebody reinstalled.
        if (publication.LeftAlone.Count > 0)
        {
            string shadowed = string.Join(", ", publication.LeftAlone).EscapeMarkup();
            AnsiConsole.MarkupLine(
                $"[yellow]Skills left alone[/]: {shadowed} — this install ships a skill of each of "
                + "those names, and each was already in the canonical directory without an install "
                + "having put it there, so yours was kept and the platform's was not written. Yours "
                + "is what every project home links to. Delete the one you want replaced and run "
                + "h9k install again to take the platform's.");
        }

        AnsiConsole.MarkupLine(
            "[dim]Project homes link into that directory, so they already have these. A skill added "
            + "since a home was created reaches it at the next h9k project init.[/]");
    }

    /// <summary>
    /// Publish lands in staging, then the staged files replace the live bin. On Unix a
    /// directory swap does the whole job in one rename: renames keep open files (a running
    /// daemon, this very h9k) valid — inodes outlive the paths — so a re-install under a
    /// running system is safe.
    /// <para>
    /// Windows has no such indirection, and the fact this method's doc comment used to
    /// assert here — that renaming <paramref name="bin"/> succeeds even while <c>h9k.exe</c>
    /// runs from it, because a rename is a directory-entry change rather than a delete — is
    /// true of the FILE but not of a DIRECTORY holding it: Windows locks the full path of a
    /// loaded module, so renaming an ancestor directory of a running <c>h9k.exe</c> or
    /// <c>h9kd.exe</c> throws <c>IOException</c> ("Access ... is denied") the moment either is
    /// running from inside it (origin incident: Brian's first real Windows update, which had
    /// already fetched, verified, and fully staged the new release before this call threw).
    /// Renaming the locked file ITSELF, though, is exactly as permitted on Windows as it is
    /// assumed to be above — the OS loader shares delete access on the module it maps — so on
    /// Windows the swap runs file by file instead of directory by directory, entirely inside
    /// <see cref="SwapFilesIntoPlace"/>: retire every conflicting name first, place every staged
    /// file second, and only once both of those have fully succeeded does
    /// <see cref="RemoveStaleFiles"/> delete whatever <paramref name="bin"/> still has that
    /// staging never carried — a file an earlier release shipped and this one dropped — so a
    /// lock discovered anywhere in either phase rolls the whole merge back with nothing yet
    /// removed, rather than leaving some files on the new version, some on the old one, and a
    /// stale file gone from both (an earlier revision of this method ran the removal first,
    /// reading staging's manifest before the merge touched anything; that avoided a different
    /// bug — reading staging's manifest AFTER the merge finds it already drained by
    /// <see cref="SwapFilesIntoPlace"/>'s own <c>File.Move</c> calls and classifies every file
    /// this run had just installed as stale, caught by the cycle-2 pre-PR review reading the
    /// diff — but left the removal outside the rollback either phase provides. Running it last,
    /// gated on both phases having already succeeded, keeps the fix for the first bug without
    /// re-opening the second: a same-named file <see cref="SwapFilesIntoPlace"/> cannot overwrite
    /// (a locked <c>h9k.exe</c> or <c>h9kd.exe</c> — no special case needed for either one, since
    /// a locked file retires the same way regardless of which process holds it) still retires to
    /// a <c>.old</c> sibling right where it sits, exactly like <see cref="RetireDirectory"/> does
    /// one level up on Unix. Together the three leave <paramref name="bin"/> as exactly staging's
    /// contents rather than the union of every version ever installed.
    /// </para>
    /// </summary>
    private static void SwapIntoPlace(string staging, string bin)
    {
        if (OperatingSystem.IsWindows())
        {
            // Also reclaims a whole bin.old (or bin.old.<random>) directory a machine still
            // carries from before this per-file merge existed — the old code swapped bin as a
            // single directory-level rename, the same as the Unix branch below still does, and
            // a bin.old only that first directory-level swap could have left behind would
            // otherwise never be swept again: every later run takes this per-file branch, which
            // has nothing else that would ever look at a whole retired directory.
            SweepRetiredDirectories(bin, bin + ".old");
            HashSet<string> reportedStuck = SweepRetiredFiles(bin);
            if (Directory.Exists(bin))
            {
                SwapFilesIntoPlace(staging, bin, reportedStuck);
                TryDelete(staging);
            }
            else
            {
                Directory.Move(staging, bin);
            }

            // No trailing sweep here: SwapFilesIntoPlace already reclaims every retiree of
            // its own that placement freed up, the moment the merge finishes successfully.
            // Whatever it could not reclaim is still locked by construction (a file that
            // could be released would have already been deleted), so a second sweep here
            // can only print "still in use" noise on the success path of every Windows
            // update — never actually reclaim anything. The leading sweep on the *next* run
            // is the one that can.
            return;
        }

        string retired = bin + ".old";
        SweepRetiredDirectories(bin, retired);

        if (Directory.Exists(bin))
        {
            RetireDirectory(bin, retired);
        }

        Directory.Move(staging, bin);
        SweepRetiredDirectories(bin, retired);
    }

    /// <summary>The Windows half of <see cref="SwapIntoPlace"/>: merges every file <see
    /// cref="CollectFilePairs"/> finds under <paramref name="sourceDirectory"/> (staging) into
    /// <paramref name="destinationDirectory"/> (bin) in three steps rather than one interleaved
    /// walk, so the merge is transactional rather than merely safe file by file.
    /// Phase one retires every file bin already has under a name staging also carries (<see
    /// cref="RetireFile"/>) — ordinarily an in-place rename, permitted on Windows even for a
    /// file a running module still executes from (see <see cref="SwapIntoPlace"/>'s doc
    /// comment) — before anything staged is placed anywhere; if any one retirement fails (a
    /// lock stronger than share-delete access, most likely an antivirus scan holding a file
    /// exclusively), every retirement this call already made is undone (see <see
    /// cref="RestoreRetiredFile"/> for the one way even that can fall short — a second,
    /// independent lock on the restore itself) and the whole merge throws having placed nothing
    /// and deleted nothing, so bin is left exactly at the old version rather than a mix in every
    /// case but that one. Phase two only starts once every retirement in phase one has
    /// succeeded, so every name it moves a staged file into is already vacated and this
    /// essentially cannot fail; if it somehow still does, everything this call had already
    /// placed is moved back to the staging path it came from (<see
    /// cref="RollBackPlacedFiles"/>) before every retirement is restored the same way phase
    /// one's own failure restores them (<see cref="RestoreRetiredPairs"/>) — so a phase-two
    /// failure rolls back exactly as completely as a phase-one failure does, and bin is left at
    /// the old version in both cases rather than a mix in either one. Once every file is placed,
    /// every retiree gets one immediate
    /// reclaim attempt, quietly: on a self-contained publish the running process (this very
    /// h9k, and h9kd if it is up) has every one of its own loaded assemblies mapped, not just
    /// its main executable, so staying locked through this reclaim is the ordinary outcome for
    /// most retirees on a successful run rather than an exceptional one, and reporting it here
    /// would print "still in use" noise on every update. The next run's leading
    /// <see cref="SweepRetiredFiles"/> reports what is still stuck by then. <paramref
    /// name="reportedStuck"/> is that same leading sweep's own set of files it just told the
    /// operator were still locked, so a retirement that lands on one of those names because
    /// its plain <c>.old</c> slot is still occupied does not print a second, redundant "still
    /// in use" line about the exact file the operator was already told about a moment ago. Only
    /// once both phases have fully succeeded does <see cref="RemoveStaleFiles"/> run, against
    /// the file manifest <see cref="CollectFilePairs"/> already captured rather than a fresh
    /// read of staging (which phase two has by now emptied) — so a failure in either phase
    /// leaves every stale file exactly where it was too.</summary>
    [SupportedOSPlatform("windows")]
    private static void SwapFilesIntoPlace(
        string sourceDirectory, string destinationDirectory, HashSet<string> reportedStuck)
    {
        List<(string Source, string Destination)> files = [];
        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        CollectFilePairs(sourceDirectory, destinationDirectory, files, directories);

        List<(string Original, string Retired)> retired = [];
        foreach ((_, string destination) in files)
        {
            if (!File.Exists(destination))
            {
                continue;
            }

            try
            {
                retired.Add((destination, RetireFile(destination, reportedStuck)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RestoreRetiredPairs(retired);
                throw new InvalidOperationException(
                    $"Could not finish updating {destination} — it is locked and could not even be retired "
                        + "aside. Close whatever holds it (a running h9k or h9kd, an antivirus scan) and run "
                        + "h9k update again; bin is unchanged by this run's version swap, short of the "
                        + "vanishingly rare case where undoing an earlier retirement in this same run hits a "
                        + "second, independent lock of its own — that one file is cleaned up automatically by "
                        + "a future install or update.",
                    exception);
            }
        }

        List<(string Source, string Destination)> placed = [];
        foreach ((string source, string destination) in files)
        {
            try
            {
                File.Move(source, destination);
                placed.Add((source, destination));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RollBackPlacedFiles(placed);
                RestoreRetiredPairs(retired);
                throw new InvalidOperationException(
                    $"Could not finish installing {destination} — {exception.Message} Run h9k update again "
                        + "once that is resolved; bin is unchanged by this run's version swap, short of the "
                        + "vanishingly rare case where undoing an earlier placement or retirement in this same "
                        + "run hits a second, independent lock of its own — those files are cleaned up "
                        + "automatically by a future install or update.",
                    exception);
            }
        }

        foreach ((_, string retiredPath) in retired)
        {
            TryDeleteFile(retiredPath, report: false);
        }

        RemoveStaleFiles(
            destinationDirectory,
            new HashSet<string>(files.Select(pair => pair.Destination), StringComparer.OrdinalIgnoreCase),
            directories);
    }

    /// <summary>Walks <paramref name="sourceDirectory"/> (staging, or one of its subdirectories)
    /// against <paramref name="destinationDirectory"/> (bin, or the matching subdirectory),
    /// creating destination directories as it goes and recording where each staged file lands,
    /// and every destination directory it created or found along the way — the enumeration half
    /// of <see cref="SwapFilesIntoPlace"/>'s merge, kept separate so retiring and placing can
    /// each run as one flat pass over every file instead of being interleaved directory by
    /// directory, which is what lets the whole merge roll back together instead of one directory
    /// at a time. <paramref name="directories"/> is the same manifest idea one level up: it is
    /// what lets <see cref="RemoveStaleFiles"/> run once, after the merge, against a captured
    /// picture of what staging held rather than a live read of a staging directory the merge has
    /// by then already drained. Every destination entry is cleared of a conflict with <see
    /// cref="ClearConflictingDestinationEntry"/> before this walk touches it — a junction or
    /// type mismatch left for the post-merge <see cref="RemoveStaleFiles"/> to find would by then
    /// have already been merged into or through, which is exactly the hazard <see
    /// cref="RemoveStaleFiles"/> moving to run after both phases (rather than before, as it once
    /// did) reopened; clearing it here instead, before <see cref="Directory.CreateDirectory"/> or
    /// <see cref="File.Move"/> ever see the entry, restores the same guarantee without moving the
    /// genuinely-stale cleanup back ahead of a swap that might still fail (origin: cycle-5
    /// pre-PR review). A conflict <see cref="ClearConflictingDestinationEntry"/> cannot actually
    /// clear (an undeletable junction, or a locked file or directory sitting where staging's own
    /// kind belongs) refuses the merge for that one entry with an <see
    /// cref="InvalidOperationException"/> rather than walking through it — the entry is still a
    /// conflict, and neither <see cref="Directory.CreateDirectory"/> nor <see cref="File.Move"/>
    /// is safe to let discover that on their own (origin: cycle-1 pre-PR adversarial review,
    /// which traced the swallowed-failure case through to writes landing outside <c>bin</c> via
    /// an unclearable junction, and to a raw, unhandled <see cref="IOException"/> from <see
    /// cref="Directory.CreateDirectory"/> on the type-mismatch case). Thrown here, before
    /// anything in <paramref name="destinationDirectory"/> has been retired or placed, so bin is
    /// left as this run found it — a directory conflict this same walk already cleared is
    /// retired aside rather than gone (see <see cref="ClearConflictingDestinationEntry"/>), the
    /// one exception being a conflicting file an earlier entry in this walk already deleted
    /// outright, replaced by simply running the command again — the same hedge phase one's own
    /// failure carries. This frame's own <see cref="RequireConflictCleared"/> and <see
    /// cref="Directory.CreateDirectory"/> call, and its own enumeration of <paramref
    /// name="sourceDirectory"/>, are each inside their own try block for the identical reason
    /// every other walk in this file now is: a failure at either step (a permission dropped,
    /// staging removed from under this walk) must report as this same refusal, naming *this*
    /// frame's own directories, rather than escape unhandled or surface with a recursive call's
    /// frame misnamed as the one that failed (origin: cycle-4 pre-PR adversarial review; cycle-5
    /// pre-PR verify pass, which found the top-level call's own conflict-check-and-create pair
    /// still outside every try, and a nested frame's own failure there surfacing through the
    /// parent's listing-failure message naming the wrong directory and the wrong cause). An
    /// <see cref="InvalidOperationException"/> a recursive call already raised — whether from its
    /// own conflict-check-and-create pair or its own enumeration — is already wrapped with the
    /// frame it actually concerns, so it is deliberately left out of the <c>IOException</c>/
    /// <c>UnauthorizedAccessException</c> filters below and simply propagates rather than being
    /// re-wrapped a second time under this frame's directories.</summary>
    [SupportedOSPlatform("windows")]
    private static void CollectFilePairs(
        string sourceDirectory,
        string destinationDirectory,
        List<(string Source, string Destination)> files,
        HashSet<string> directories)
    {
        try
        {
            RequireConflictCleared(destinationDirectory, isDirectory: true);
            Directory.CreateDirectory(destinationDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Could not finish updating {destinationDirectory} — {exception.Message} Run h9k update "
                    + "again once that is resolved; bin is unchanged by this run's version swap, short of "
                    + "a directory conflict this same run already cleared, which is retired aside rather "
                    + "than gone.",
                exception);
        }

        directories.Add(destinationDirectory);

        try
        {
            foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory))
            {
                string destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(sourceFile));
                RequireConflictCleared(destinationFile, isDirectory: false);
                files.Add((sourceFile, destinationFile));
            }

            foreach (string sourceSubdirectory in Directory.EnumerateDirectories(sourceDirectory))
            {
                CollectFilePairs(
                    sourceSubdirectory,
                    Path.Combine(destinationDirectory, Path.GetFileName(sourceSubdirectory)),
                    files,
                    directories);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Could not finish updating {destinationDirectory} — {sourceDirectory} could not be listed "
                    + $"({exception.Message}). Run h9k update again once that is resolved; bin is unchanged "
                    + "by this run's version swap, short of a directory conflict this same run already "
                    + "cleared, which is retired aside rather than gone.",
                exception);
        }
    }

    /// <summary>Calls <see cref="ClearConflictingDestinationEntry"/> and throws when it reports
    /// the conflict is still there — see <see cref="CollectFilePairs"/>'s own doc comment for
    /// why refusing beats proceeding through an unresolved conflict.</summary>
    [SupportedOSPlatform("windows")]
    private static void RequireConflictCleared(string destinationPath, bool isDirectory)
    {
        if (ClearConflictingDestinationEntry(destinationPath, isDirectory))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Could not finish updating {destinationPath} — an existing junction, symlink, file, or "
                + "directory there conflicts with what the new version ships and could not be removed "
                + "(something still has it locked). Close whatever holds it and run h9k update again; "
                + "bin is unchanged by this run's version swap, short of a directory conflict this same "
                + "run already cleared, which is retired aside rather than gone.");
    }

    /// <summary>Clears whatever <paramref name="destinationPath"/> currently holds when it
    /// conflicts with what staging is about to place there, so <see cref="CollectFilePairs"/>'s
    /// own <see cref="Directory.CreateDirectory"/> and <see cref="SwapFilesIntoPlace"/>'s later
    /// <see cref="File.Move"/> land on a genuine, empty slot rather than merge into or through
    /// something staging never shipped:
    /// <list type="bullet">
    /// <item>a directory junction or symlink (an operator's own, or an earlier tool's) is
    /// unlinked without ever being followed — <see cref="TryDeleteDirectoryRecursively"/> already
    /// has this guard, so reusing it here is what keeps every write inside <c>bin</c> instead of
    /// walking through to whatever the link points at (the same hazard <see
    /// cref="RemoveStaleFiles"/> guards on its own, later walk); a <em>file</em> symlink reports a
    /// link target too, but is a file as far as <see cref="File.Delete(string)"/> is concerned,
    /// so it is dispatched to <see cref="TryDeleteReparsePointFile"/> instead — <see
    /// cref="TryDeleteDirectoryRecursively"/> can never delete one (origin: cycle-2 pre-PR
    /// adversarial review, which traced a file symlink through to a permanent, misdiagnosed
    /// "something still has it locked" refusal). The dispatch reads <see
    /// cref="File.GetAttributes(string)"/>, not <see cref="File.Exists(string)"/>, to tell the two
    /// kinds of reparse point apart, because only <see cref="File.GetAttributes(string)"/> is
    /// documented to read the reparse point's own directory entry without opening its target — a
    /// <em>dangling</em> file symlink (its target since removed) still carries no <see
    /// cref="FileAttributes.Directory"/> flag under that read (origin: cycle-4 pre-PR conformance
    /// review, which traced a dangling link through <see cref="File.Exists(string)"/> to a
    /// permanent, misdiagnosed refusal one way or another depending on which of <see
    /// cref="File.Exists(string)"/>'s two possible target-resolution behaviors actually holds on
    /// Windows). The file branch itself deletes the entry directly, through <see
    /// cref="TryDeleteReparsePointFile"/>, rather than through <see cref="TryDeleteFile"/>'s own
    /// <see cref="File.Exists(string)"/> pre-check, so clearing a dangling link no longer depends
    /// on which of those two behaviors is true either (origin: cycle-5 pre-PR verify pass, which
    /// found the dispatch here still routed through that same unresolved <see
    /// cref="File.Exists(string)"/> premise one level down);</item>
    /// <item>a type mismatch — a file where staging ships a directory, or a directory where
    /// staging ships a file — is cleared. A conflicting <em>file</em> is deleted outright: a
    /// single <see cref="File.Delete(string)"/> either lands whole or not at all, so there is no
    /// partial state to worry about. A conflicting <em>directory</em> is retired aside instead of
    /// deleted — moved to a <c>.old</c> sibling via <see cref="TryRetireConflictingDirectory"/>,
    /// the same rename trick <see cref="RetireFile"/> and <see cref="RetireDirectory"/> already
    /// use to retire a locked file or the whole of <c>bin</c>: <see
    /// cref="TryDeleteDirectoryRecursively"/> deletes file by file, so a lock partway through (the
    /// ordinary case this whole feature exists for) used to leave the directory gutted of
    /// everything but the locked file instead of the "bin is unchanged" guarantee <see
    /// cref="RequireConflictCleared"/>'s own message asserts; a rename either lands whole or
    /// throws with nothing touched, so the guarantee now actually holds. The retired sibling
    /// carries no staged counterpart, so <see cref="RemoveStaleFiles"/>'s own post-merge walk
    /// (which runs over the very directory this landed in) sweeps it away as stale the same way
    /// it reclaims anything else staging no longer ships, or leaves it for a later run's own
    /// sweep if something inside is still locked (origin: cycle-4 pre-PR conformance review,
    /// which traced a partial delete here through to a directory left runnable by neither the old
    /// release nor the new one).</item>
    /// </list>
    /// A destination that does not exist yet, or already matches staging's own kind, is left
    /// alone. Returns whether <paramref name="destinationPath"/> is actually clear of a conflict
    /// once this call returns — <see langword="false"/> means whatever was there is still there,
    /// most likely locked, and <see cref="RequireConflictCleared"/> is what turns that into a
    /// refused merge instead of a walk-through.</summary>
    [SupportedOSPlatform("windows")]
    private static bool ClearConflictingDestinationEntry(string destinationPath, bool isDirectory)
    {
        if (new DirectoryInfo(destinationPath).LinkTarget is not null)
        {
            return (File.GetAttributes(destinationPath) & FileAttributes.Directory) != 0
                ? TryDeleteDirectoryRecursively(destinationPath)
                : TryDeleteReparsePointFile(destinationPath);
        }

        if (isDirectory && File.Exists(destinationPath))
        {
            return TryDeleteFile(destinationPath, report: false);
        }

        if (!isDirectory && Directory.Exists(destinationPath))
        {
            return TryRetireConflictingDirectory(destinationPath);
        }

        return true;
    }

    /// <summary>Best-effort rename of a conflicting directory <see
    /// cref="ClearConflictingDestinationEntry"/> found where staging is about to place a file —
    /// the retire-aside half of that method's directory case, kept separate so the rename's own
    /// fallback (a <c>.old</c> slot an earlier run's own leftover already occupies) reads the
    /// same as <see cref="RetireFile"/>'s. <see cref="Directory.Move(string, string)"/> is a
    /// single directory-entry rename — it either lands whole or throws with nothing touched — so
    /// unlike a recursive delete this can never leave <paramref name="path"/> half gone. Returns
    /// whether <paramref name="path"/> is actually gone from its original name once this call
    /// returns; <see langword="false"/> means the rename itself failed (something under it is
    /// locked, most plausibly a loaded module whose path the rename would change out from under
    /// it — see <see cref="SwapIntoPlace"/>'s own doc comment) and nothing moved.</summary>
    [SupportedOSPlatform("windows")]
    private static bool TryRetireConflictingDirectory(string path)
    {
        string retired = path + ".old";
        if (Directory.Exists(retired))
        {
            retired = $"{retired}.{Path.GetRandomFileName()}";
        }

        try
        {
            Directory.Move(path, retired);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Restores every pair <see cref="SwapFilesIntoPlace"/> retired this call back to
    /// its original name — a full rollback when phase one fails partway, or the second half of
    /// a phase-two failure's rollback (see <see cref="RollBackPlacedFiles"/> for the first half),
    /// where <see cref="RestoreRetiredFile"/>'s own already-placed check is what makes this a
    /// no-op for a pair <see cref="RollBackPlacedFiles"/> has not reached yet.</summary>
    [SupportedOSPlatform("windows")]
    private static void RestoreRetiredPairs(List<(string Original, string Retired)> pairs)
    {
        foreach ((string original, string retiredPath) in pairs)
        {
            RestoreRetiredFile(retiredPath, original);
        }
    }

    /// <summary>The first half of a phase-two failure's rollback in <see
    /// cref="SwapFilesIntoPlace"/>: moves every file phase two had already placed on the new
    /// version back to the staging path it came from, so the retirees <see
    /// cref="RestoreRetiredPairs"/> restores immediately after are not restored underneath a
    /// destination phase two already overwrote — without this, an already-placed file's
    /// original stayed a permanent no-op in <see cref="RestoreRetiredFile"/> and bin was left a
    /// genuine mix of old and new files, which is the gap the cycle-1 pre-PR review found.
    /// Best-effort and silent on failure, matching <see cref="RestoreRetiredFile"/>'s own
    /// reasoning: the caller is already mid-throw and reports the original cause, and a move
    /// that cannot complete (a second, independent lock on top of the one that triggered the
    /// rollback) leaves that one file on the new version rather than back in staging — staging
    /// is not deleted on this failure path, so it is still there for a future install or update
    /// to clean up regardless.</summary>
    [SupportedOSPlatform("windows")]
    private static void RollBackPlacedFiles(List<(string Source, string Destination)> placed)
    {
        foreach ((string source, string destination) in placed)
        {
            try
            {
                File.Move(destination, source);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left on the new version at `destination` — see this method's own doc
                // comment for why that is the one case this rollback does not fully hold.
            }
        }
    }

    /// <summary>The third step of the Windows merge in <see cref="SwapIntoPlace"/>:
    /// <see cref="SwapFilesIntoPlace"/>'s two phases only ever add or overwrite, so without this
    /// a file an earlier release shipped and the new one dropped survives every reinstall or
    /// update — unlike the Unix directory swap, which replaces <paramref
    /// name="destinationDirectory"/> outright, the merge would otherwise leave it the union of
    /// every version ever installed. Runs only after both of <see cref="SwapFilesIntoPlace"/>'s
    /// phases have fully succeeded, against <paramref name="stagedFiles"/> and <paramref
    /// name="stagedDirectories"/> — the manifest <see cref="CollectFilePairs"/> captured from
    /// staging before phase two started moving files out of it — rather than a live read of
    /// staging itself, which by now phase two has drained (an earlier revision called this
    /// first, against staging's live contents, specifically to avoid reading a drained staging
    /// directory; running it last instead of reading it live is what keeps both properties at
    /// once — see <see cref="SwapIntoPlace"/>'s doc comment for the fuller history). Deletes
    /// whatever <paramref name="destinationDirectory"/> has that the manifest does not, skipping
    /// this run's own <c>*.old</c> retirees via <see cref="IsRetiredFileName"/> — those are not
    /// stale, they are this run's files that stayed locked through <see
    /// cref="SwapFilesIntoPlace"/>'s own immediate reclaim and are now waiting on the next run's
    /// leading <see cref="SweepRetiredFiles"/> — and best-effort, same as the rest of this file,
    /// so a file still locked this run is simply left for a later one; listing <paramref
    /// name="destinationDirectory"/> failing outright (a permission dropped, or the directory
    /// removed from under this walk) is swallowed the same way, rather than left to escape as an
    /// unhandled exception on a run whose merge has, by the time this runs, already fully
    /// succeeded (origin: cycle-4 pre-PR adversarial review).</summary>
    [SupportedOSPlatform("windows")]
    private static void RemoveStaleFiles(
        string destinationDirectory, HashSet<string> stagedFiles, HashSet<string> stagedDirectories)
    {
        if (!Directory.Exists(destinationDirectory))
        {
            return;
        }

        try
        {
            RemoveStaleEntries(destinationDirectory, stagedFiles, stagedDirectories);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Listing destinationDirectory itself failed, rather than a per-entry failure the
            // loops below already swallow individually — left for a later run's own sweep.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveStaleEntries(
        string destinationDirectory, HashSet<string> stagedFiles, HashSet<string> stagedDirectories)
    {
        foreach (string destinationFile in Directory.EnumerateFiles(destinationDirectory))
        {
            string fileName = Path.GetFileName(destinationFile);
            if (IsRetiredFileName(fileName) || stagedFiles.Contains(destinationFile))
            {
                continue;
            }

            TryDeleteFile(destinationFile);
        }

        foreach (string destinationSubdirectory in Directory.EnumerateDirectories(destinationDirectory))
        {
            // A directory symlink or junction inside bin (an operator's own, or an earlier
            // tool's) still passes Directory.Exists; matching it against a same-named staged
            // subdirectory, or recursing into it as stale, would walk through to whatever it
            // points at and delete that instead. No release ships a symlink of its own, so a
            // link found here is always removed as leftover debris via
            // TryDeleteDirectoryRecursively, which unlinks it without following — the same
            // hazard UninstallCommand.RemoveInstallOwnedEntries already guards against on its
            // own walk.
            if (new DirectoryInfo(destinationSubdirectory).LinkTarget is not null)
            {
                TryDeleteDirectoryRecursively(destinationSubdirectory);
                continue;
            }

            if (stagedDirectories.Contains(destinationSubdirectory))
            {
                RemoveStaleFiles(destinationSubdirectory, stagedFiles, stagedDirectories);
            }
            else
            {
                TryDeleteDirectoryRecursively(destinationSubdirectory);
            }
        }
    }

    /// <summary>Matches a name <see cref="RetireFile"/> could have produced this run (a plain
    /// <c>.old</c> sibling, or its uniquely suffixed double-lock fallback) — the predicate
    /// <see cref="RemoveStaleFiles"/> uses to leave this run's own retirees for
    /// <see cref="SweepRetiredFiles"/> instead of deleting them as stale.</summary>
    private static bool IsRetiredFileName(string fileName) =>
        fileName.EndsWith(".old", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".old.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Best-effort recursive delete for a stale subdirectory <see cref="RemoveStaleFiles"/>
    /// finds in <paramref name="destinationDirectory"/> with no counterpart in staging at all —
    /// deletes what it can and leaves the rest (a locked file inside it) for a later sweep,
    /// rather than let one locked file abort removing everything else the old subdirectory
    /// held. <see cref="RemoveStaleFiles"/> already keeps a top-level directory symlink or
    /// junction out of this call entirely, but a symlink or junction nested deeper (reached via
    /// the recursive call below) needs the identical guard, so it is checked again here. Returns
    /// whether <paramref name="destinationDirectory"/> is actually gone once this call returns —
    /// <see cref="RemoveStaleFiles"/>'s own two call sites ignore it (a locked leftover there is
    /// simply left for a later sweep), but <see cref="ClearConflictingDestinationEntry"/> needs
    /// it to know whether the merge can proceed through this path or must refuse instead. The
    /// enumeration calls below sit inside the same try block as the final <see
    /// cref="Directory.Delete(string)"/> rather than left bare, so a listing failure partway
    /// through (a permission dropped, or the directory removed out from under this walk by a
    /// concurrent process) reports as "still locked" the same as everything else this method
    /// already handles, instead of escaping as an unhandled exception past every caller up to
    /// <c>FinishAsync</c>'s <see cref="InvalidOperationException"/>-only catch — the exact
    /// stack-trace outcome this whole file exists to remove (origin: cycle-4 pre-PR adversarial
    /// review).</summary>
    [SupportedOSPlatform("windows")]
    private static bool TryDeleteDirectoryRecursively(string destinationDirectory)
    {
        try
        {
            if (new DirectoryInfo(destinationDirectory).LinkTarget is null)
            {
                foreach (string file in Directory.EnumerateFiles(destinationDirectory))
                {
                    TryDeleteFile(file);
                }

                foreach (string nested in Directory.EnumerateDirectories(destinationDirectory))
                {
                    TryDeleteDirectoryRecursively(nested);
                }
            }

            Directory.Delete(destinationDirectory);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not empty, or listing/deleting it failed outright — either way, something inside
            // (or the directory itself) is still locked. Left for a later sweep.
            return false;
        }
    }

    /// <summary>Best-effort restore for <see cref="SwapFilesIntoPlace"/>'s failure paths: puts a
    /// file <see cref="RetireFile"/> just retired back under its original name. Swallows its
    /// own failure — the caller is already mid-throw and reports the original cause; a restore
    /// that cannot complete (a second, independent lock on top of the one that triggered the
    /// rollback in the first place — rare, but not impossible) leaves the file at <paramref
    /// name="retired"/> instead of back at <paramref name="destination"/>, which a later <see
    /// cref="SweepRetiredFiles"/> DELETES once nothing holds it — not a recovery of the file,
    /// the same one-way meaning "reclaim" has everywhere else in this class — so this is the one
    /// path where the rollback this method exists to provide does not fully hold.</summary>
    [SupportedOSPlatform("windows")]
    private static void RestoreRetiredFile(string retired, string destination)
    {
        if (File.Exists(destination) || !File.Exists(retired))
        {
            return;
        }

        try
        {
            File.Move(retired, destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left at `retired` — a later SweepRetiredFiles deletes it once nothing holds it,
            // rather than restoring it; see this method's own doc comment.
        }
    }

    /// <summary>Renames a file Windows will not let <see cref="SwapFilesIntoPlace"/> overwrite —
    /// a running <c>h9k.exe</c> or <c>h9kd.exe</c> most likely — to a <c>.old</c> sibling right
    /// beside it: the file-granularity version of <see cref="RetireDirectory"/>, permitted on
    /// Windows for the identical reason a directory-level rename is not (see
    /// <see cref="SwapIntoPlace"/>'s doc comment). Falls back to a uniquely suffixed name when
    /// an earlier update's own <c>.old</c> file is itself still locked — the same double-lock
    /// fallback <see cref="RetireDirectory"/> uses for a whole directory — and returns
    /// whichever name it actually used, so the caller can restore it if placement that follows
    /// fails. <paramref name="reportedStuck"/> names every file this run's leading <see
    /// cref="SweepRetiredFiles"/> already found still locked, whether that sweep named it to the
    /// operator individually or only as part of a count — either way, taking the fallback here
    /// because <c>retired</c> is one of those says nothing the operator has not already been
    /// told, so that case stays quiet instead of printing a second line about the same
    /// file.</summary>
    [SupportedOSPlatform("windows")]
    private static string RetireFile(string path, HashSet<string> reportedStuck)
    {
        string retired = path + ".old";
        if (!File.Exists(retired))
        {
            File.Move(path, retired);
            return retired;
        }

        string fallback = $"{retired}.{Path.GetRandomFileName()}";
        File.Move(path, fallback);
        if (!reportedStuck.Contains(retired))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]{retired} is still in use, so {Path.GetFileName(path)} was retired to {Path.GetFileName(fallback)} instead — it will be cleaned up on a future install or update once nothing holds it.[/]");
        }

        return fallback;
    }

    /// <summary>Deletes every <c>*.old</c> file <see cref="RetireFile"/> left behind (and any
    /// uniquely suffixed <c>*.old.&lt;random&gt;</c> fallback from an earlier double lock),
    /// recursively under <paramref name="bin"/> — the file-granularity sibling of
    /// <see cref="SweepRetiredDirectories"/>, best-effort so a file still locked this run is
    /// simply left for a later one. Walks its own recursion rather than
    /// <c>SearchOption.AllDirectories</c> so it can skip a directory symlink or junction inside
    /// <paramref name="bin"/> (an operator's own, or an earlier tool's) the same way <see
    /// cref="RemoveStaleFiles"/> and <see cref="TryDeleteDirectoryRecursively"/> already do —
    /// otherwise a junction pointing outside <paramref name="bin"/> gets followed and files
    /// under its target matching <c>*.old</c> or <c>*.old.*</c> are deleted, well outside the
    /// install directory, before <see cref="RemoveStaleFiles"/> ever gets a chance to unlink
    /// the junction itself (origin: cycle-2 pre-PR adversarial review). Reports quietly per file
    /// (<see cref="TryDeleteFile"/> with <c>report: false</c>) and prints one summary line of its
    /// own instead: on a self-contained publish with <c>h9kd</c> still up, every one of its
    /// loaded assemblies is a retiree this sweep cannot yet reclaim, and a "still in use" line
    /// per file would be tens of lines of noise ahead of an update's real output for the ordinary
    /// case of one still-running daemon, not the exceptional one. Returns the full path of every
    /// file it found still locked, named individually (the single-file case) or only as part of
    /// the count in the summary line below (the multi-file case) — either way, the operator has
    /// already been told about it here, so <see cref="RetireFile"/> uses the same set to skip
    /// printing its own "still in use" line for the exact same file a moment later, whether the
    /// name behind it was actually given or only counted (origin: cycle-1 pre-PR adversarial
    /// review — the multi-file case used to return no suppression set at all, so a run with
    /// dozens of retirees still locked from an earlier update printed this method's one summary
    /// line followed immediately by one more "still in use" line per retiree from <see
    /// cref="RetireFile"/>, right back to the wall of noise this method's own summary line exists
    /// to avoid).</summary>
    [SupportedOSPlatform("windows")]
    private static HashSet<string> SweepRetiredFiles(string bin)
    {
        HashSet<string> stillStuck = new(StringComparer.OrdinalIgnoreCase);
        SweepRetiredFiles(bin, stillStuck);

        switch (stillStuck.Count)
        {
            case 0:
                break;
            case 1:
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]Could not remove {stillStuck.First()} yet (still in use) — it will be cleaned up on the next install or update.[/]");
                break;
            default:
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]{stillStuck.Count} retired files from an earlier install or update are still in use — they will be cleaned up on the next one.[/]");
                break;
        }

        return stillStuck;
    }

    [SupportedOSPlatform("windows")]
    private static void SweepRetiredFiles(string directory, HashSet<string> stillStuck)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.old"))
            {
                if (!TryDeleteFile(file, report: false))
                {
                    stillStuck.Add(file);
                }
            }

            // FileSystemName's Win32-expression translation rewrites a trailing ".*" into
            // DOS_DOT, which also matches zero characters — so "*.old.*" matches a bare "*.old"
            // too, the same quirk SweepRetiredDirectories documents and skips for "bin.old.*"
            // one level up. Those are already handled by the loop above, so skip them here
            // rather than let a still-locked one print its "still in use" warning twice.
            foreach (string file in Directory.EnumerateFiles(directory, "*.old.*"))
            {
                if (file.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryDeleteFile(file, report: false))
                {
                    stillStuck.Add(file);
                }
            }

            foreach (string subdirectory in Directory.EnumerateDirectories(directory))
            {
                if (new DirectoryInfo(subdirectory).LinkTarget is null)
                {
                    SweepRetiredFiles(subdirectory, stillStuck);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Listing `directory` itself failed (a permission dropped, or the directory removed
            // from under this walk) rather than a per-file failure the loops above already
            // record individually — reported the same way, so it still counts toward the
            // summary line above instead of escaping as an unhandled exception (origin: cycle-4
            // pre-PR adversarial review).
            stillStuck.Add(directory);
        }
    }

    /// <summary>Deletes a file reparse point directly for <see
    /// cref="ClearConflictingDestinationEntry"/>'s file-symlink branch, without <see
    /// cref="TryDeleteFile"/>'s own leading <see cref="File.Exists(string)"/> check: whether that
    /// check reads a dangling link's own directory entry or opens its target to answer is not
    /// settled here (see <see cref="ClearConflictingDestinationEntry"/>'s own doc comment), so
    /// gating the delete on it either silently reports a still-present dangling link already gone,
    /// or is merely redundant — this call cannot afford to depend on which. <see
    /// cref="File.Delete(string)"/> is documented to no-op rather than throw when <paramref
    /// name="path"/> is already gone, so skipping the existence check costs nothing (origin:
    /// cycle-5 pre-PR verify pass).</summary>
    [SupportedOSPlatform("windows")]
    private static bool TryDeleteReparsePointFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Best-effort delete for a single file, the file-granularity sibling of
    /// <see cref="TryDelete"/>. Returns whether <paramref name="file"/> is gone (deleted, or
    /// already absent) once this call returns. <paramref name="report"/> is false for <see
    /// cref="SwapFilesIntoPlace"/>'s own immediate reclaim of its retirees and for <see
    /// cref="SweepRetiredFiles"/>'s walk, where a still-locked file is the ordinary case (a
    /// self-contained publish's own loaded assemblies, not just its main executable — on a
    /// running <c>h9kd</c> that can be dozens of files) rather than something worth a line of
    /// its own on an otherwise successful run; both report what they collectively found still
    /// stuck through their own return value instead, and <see cref="SweepRetiredFiles"/> prints
    /// one summary line for its own findings rather than one per file.</summary>
    [SupportedOSPlatform("windows")]
    private static bool TryDeleteFile(string file, bool report = true)
    {
        if (!File.Exists(file))
        {
            return true;
        }

        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (report)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]Could not remove {file} yet (still in use) — it will be cleaned up on the next install or update.[/]");
            }

            return false;
        }
    }

    /// <summary>Deletes <paramref name="retired"/> (<c>bin.old</c>) and any uniquely suffixed
    /// fallback left behind by an earlier double lock (<c>bin.old.&lt;random&gt;</c>), so a
    /// stray fallback is reclaimed the next time nothing holds it rather than left forever.</summary>
    private static void SweepRetiredDirectories(string bin, string retired)
    {
        TryDelete(retired);

        string? parent = Path.GetDirectoryName(bin);
        if (parent is null || !Directory.Exists(parent))
        {
            return;
        }

        // FileSystemName's Win32-expression translation rewrites a trailing ".*" into
        // DOS_DOT, which also matches zero characters — so "bin.old.*" matches "bin.old"
        // itself, which TryDelete(retired) just above already attempted. Skip it rather
        // than let a still-locked bin.old print its "still in use" warning twice.
        foreach (string fallback in Directory.EnumerateDirectories(parent, $"{Path.GetFileName(retired)}.*"))
        {
            if (string.Equals(fallback, retired, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDelete(fallback);
        }
    }

    /// <summary>Moves <paramref name="bin"/> to <paramref name="retired"/>, falling back to a
    /// uniquely suffixed name when a still-locked copy from an earlier run already occupies
    /// it — the one case <see cref="TryDelete"/> could not clear a moment ago.</summary>
    private static void RetireDirectory(string bin, string retired)
    {
        if (!Directory.Exists(retired))
        {
            Directory.Move(bin, retired);
            return;
        }

        string fallback = $"{retired}.{Path.GetRandomFileName()}";
        Directory.Move(bin, fallback);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]{retired} is still in use, so the previous install was retired to {fallback} instead — it will be cleaned up on a future install or update once nothing holds it.[/]");
    }

    /// <summary>Best-effort delete: a lock held by an antivirus scanner, an indexer, or a
    /// running process must not turn a command into an unhandled exception — the directory is
    /// left for the next install or update's own leading cleanup to retry.</summary>
    internal static void TryDelete(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Could not remove {directory} yet (still in use) — it will be cleaned up on the next install or update.[/]");
        }
    }

    /// <summary>
    /// The managed successor to the hand-made symlink. An h9k symlink already on the
    /// PATH is retargeted in place (never shadowed by a second link elsewhere); a real
    /// file is never clobbered, at the fallback path as much as on the PATH. With
    /// nothing to inherit, a link is created in the first conventional bin directory
    /// that is on the PATH and writable, falling back to ~/.local/bin. A link install
    /// cannot rewrite is the one case that contract cannot honour, so it is reported
    /// rather than passed over.
    /// </summary>
    internal static void LinkOntoPath(string target, string pathVariable, string homeDirectory)
    {
        string installedBin = Path.GetDirectoryName(target)!;
        List<string> pathDirectories = [.. pathVariable
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(directory => directory != installedBin)];
        List<string> unretargetable = [];

        LinkSomewhere(target, homeDirectory, pathDirectories, unretargetable);
        ReportShadowing(unretargetable, target);
    }

    /// <summary>The search itself, in PATH order; <paramref name="unretargetable"/> collects
    /// the h9k links it could not rewrite, for the caller to report once the search is done.</summary>
    private static void LinkSomewhere(
        string target, string homeDirectory, List<string> pathDirectories, List<string> unretargetable)
    {
        foreach (string directory in pathDirectories)
        {
            string existing = Path.Combine(directory, "h9k");
            switch (Classify(existing))
            {
                case PathEntry.Absent:
                    continue;

                case PathEntry.RealFile:
                    ReportRealFile(existing, target);
                    return;

                default:
                    if (AlreadyPointsAt(existing, target) || TryLink(existing, target))
                    {
                        AnsiConsole.MarkupLineInterpolated($"[green]On PATH[/]: {existing} → {target}");
                        return;
                    }

                    unretargetable.Add(existing);
                    break;
            }
        }

        foreach (string directory in new[] { "/opt/homebrew/bin", "/usr/local/bin" })
        {
            string link = Path.Combine(directory, "h9k");
            if (Directory.Exists(directory) && pathDirectories.Contains(directory) && TryLink(link, target))
            {
                AnsiConsole.MarkupLineInterpolated($"[green]On PATH[/]: {link} → {target}");
                return;
            }
        }

        string fallbackDirectory = Path.Combine(homeDirectory, ".local", "bin");
        string fallback = Path.Combine(fallbackDirectory, "h9k");

        // The fallback is the one candidate no PATH entry vetted — it is chosen precisely
        // when ~/.local/bin is absent from the PATH — so the never-clobber check runs here
        // too. Without it, install deleted a real h9k the operator kept there (TryLink
        // unlinks whatever it finds) while refusing the identical file one directory over.
        if (Classify(fallback) is PathEntry.RealFile)
        {
            ReportRealFile(fallback, target);
            return;
        }

        Directory.CreateDirectory(fallbackDirectory);
        if (TryLink(fallback, target))
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Linked[/]: {fallback} → {target}");
            if (!pathDirectories.Contains(fallbackDirectory))
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]{fallbackDirectory} is not on your PATH[/] — add it to your shell profile to call h9k directly.");
            }

            return;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[yellow]Could not link h9k onto the PATH[/] — call {target} directly, or link it by hand.");
    }

    /// <summary>
    /// An h9k link install could not rewrite still wins on the PATH over whatever it linked
    /// instead, so the shell keeps resolving h9k to the stale binary. Announcing only the
    /// link that succeeded would report a refreshed install that is not the one on the PATH:
    /// exactly the installed-binary staleness this command exists to end.
    /// </summary>
    private static void ReportShadowing(List<string> unretargetable, string target)
    {
        foreach (string blocked in unretargetable)
        {
            string blockedPath = Markup.Escape(blocked);
            AnsiConsole.MarkupLine(
                $"[yellow]Could not retarget {blockedPath}[/] (permission denied) — it comes earlier on "
                + "your PATH, so h9k still resolves to that stale link. Point it at the fresh binary "
                + $"yourself: sudo ln -sfn {Markup.Escape(target)} {blockedPath}");
        }
    }

    /// <summary>
    /// A link install cannot rewrite but that already names the installed binary needs no
    /// rewriting. TryLink unlinks before it relinks, so it fails on a directory this user
    /// cannot write even when the link there is already the right one.
    /// </summary>
    private static bool AlreadyPointsAt(string link, string target)
    {
        FileSystemInfo? resolved = new FileInfo(link).ResolveLinkTarget(returnFinalTarget: false);
        return resolved is not null && Path.GetFullPath(resolved.FullName) == Path.GetFullPath(target);
    }

    /// <summary>
    /// The one thing install says about someone else's h9k, wherever it finds it: it is
    /// left where it is, and the installed binary is named so the operator can point at
    /// it themselves.
    /// </summary>
    private static void ReportRealFile(string existing, string target) =>
        AnsiConsole.MarkupLineInterpolated(
            $"[yellow]A real file already sits at {existing}[/] — leaving it alone; the installed binary is {target}.");

    /// <summary>What sits at a PATH entry named h9k: nothing, a symlink install may
    /// retarget, or a real file it must never clobber. A broken symlink reports Symlink,
    /// not Absent — FileInfo.Exists follows the link and says no, while LinkTarget still
    /// names the entry as ours to replace.</summary>
    internal enum PathEntry
    {
        Absent,
        Symlink,
        RealFile,
    }

    internal static PathEntry Classify(string path)
    {
        FileInfo entry = new(path);
        return (entry.Exists, entry.LinkTarget) switch
        {
            (_, not null) => PathEntry.Symlink,
            (true, null) => PathEntry.RealFile,
            _ => PathEntry.Absent,
        };
    }

    private static bool TryLink(string linkPath, string target)
    {
        try
        {
            FileInfo existing = new(linkPath);
            if (existing.Exists || existing.LinkTarget is not null)
            {
                existing.Delete();
            }

            File.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static async Task<int> OfferRestartAsync(
        bool restartRequested, bool noRestartRequested, DaemonProcessDescriptor runningBefore, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLineInterpolated(
            $"[yellow]h9kd is running (pid {runningBefore.ProcessId}) on the previous binaries.[/]");

        bool restart = (NoRestart: noRestartRequested, Restart: restartRequested) switch
        {
            { NoRestart: true } => false,
            { Restart: true } => true,
            _ => AnsiConsole.Profile.Capabilities.Interactive
                && AnsiConsole.Confirm("Restart it onto the fresh install now?"),
        };
        if (!restart)
        {
            AnsiConsole.MarkupLine(
                "[dim]Left running — it picks up the new binaries at its next start "
                + "(h9k daemon stop && h9k daemon start, or re-run install with --restart).[/]");
            return ExitCodes.Ok;
        }

        IDaemonAutostart autostart = DaemonAutostart.ForCurrentPlatform();
        int stopped = await DaemonLifecycle.StopAsync(autostart, cancellationToken);
        return stopped != ExitCodes.Ok
            ? stopped
            : await DaemonLifecycle.StartAsync(autostart, binaryOverride: null, cancellationToken);
    }
}
