using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.ProjectHomes;
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
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        string version;
        string? skillsSource;
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

            version = CliVersion.Current;
            skillsSource = Path.Combine(repoRoot, ".claude", "skills");
        }

        return await FinishAsync(staging, skillsSource, version, settings.Restart, settings.NoRestart, cancellationToken);
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
        CancellationToken cancellationToken,
        bool linkOntoPath = true)
    {
        DaemonProcessDescriptor? runningBefore = DaemonProcess.Probe();

        SwapIntoPlace(staging, DaemonRuntime.BinDirectory);
        AnsiConsole.MarkupLineInterpolated(
            $"[green]Installed[/] {version}: h9k and h9kd release binaries in {DaemonRuntime.BinDirectory}");

        // Ships Hall9k's own Postgres definition into ~/.hall9k (Decisions Log #73), so
        // h9k daemon start's reachability probe and h9k doctor's start-offer never need a
        // repo checkout — an installed user has no dev worktree to run compose from. No
        // prompt and nothing started here: install stays boring (Decisions Log #58).
        PostgresRuntime.WriteComposeFile();
        AnsiConsole.MarkupLine(
            $"[dim]Wrote Hall9k's own Postgres definition to {PostgresRuntime.ComposeFile.EscapeMarkup()} "
            + "(not started — h9k doctor or h9k daemon start will offer to when it's needed).[/]");

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

        AnsiConsole.MarkupLine(
            "[dim]No background service was registered — the daemon runs on demand (h9k daemon start / stop). "
            + "Start-at-login is a separate, explicit opt-in: h9k daemon autostart enable.[/]");

        return runningBefore is null
            ? ExitCodes.Ok
            : await OfferRestartAsync(restart, noRestart, runningBefore, cancellationToken);
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

    private static string? ReadVersionFile(string fromRelease)
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
    /// subdirectory or the VERSION marker, neither of which belongs in ~/.hall9k/bin) from an
    /// extracted release payload into staging. Checked per file rather than left to run to
    /// completion: the payload is a self-contained publish of two apps, tens of megabytes, and
    /// the zip-extraction step immediately before this one earned its own per-entry
    /// cancellation check for the same reason (60bc393).</summary>
    internal static void StageFromRelease(string fromRelease, string staging, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(staging);
        foreach (string file in Directory.EnumerateFiles(fromRelease))
        {
            if (Path.GetFileName(file) is "VERSION")
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(staging, Path.GetFileName(file)), overwrite: true);
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
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string nested in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectoryRecursively(nested, Path.Combine(destinationDirectory, Path.GetFileName(nested)), cancellationToken);
        }
    }

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
    /// PATH refresh it was always going to need a new terminal for anyway.</summary>
    [SupportedOSPlatform("windows")]
    private static void BroadcastEnvironmentChange()
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
    /// Publish lands in staging, then a directory swap replaces the live bin. Renames
    /// keep open files (a running daemon, this very h9k) valid on Unix — inodes outlive
    /// the paths — so a re-install under a running system is safe.
    /// <para>
    /// On Windows, renaming <paramref name="bin"/> succeeds even while <c>h9k.exe</c> is
    /// running from it (a rename is a directory-entry change, not a delete), but
    /// <c>h9k update</c> runs from the installed binary itself, so the retired copy still
    /// holds the running process's image when this method tries to delete it — and Windows
    /// refuses to delete an executable that is mapped into a running process. Deleting is
    /// therefore best-effort: a copy Windows would not let go of is left as <c>bin.old</c>
    /// and swept up by the next install or update, whose leading cleanup step is this same
    /// best-effort delete on what is by then a retired copy nothing is running from.
    /// </para>
    /// <para>
    /// That best-effort delete can itself fail twice in a row — an antivirus scanner or
    /// indexer holding one of the freshly written executables is the same kind of lock that
    /// stops the leading cleanup — so the retire step below falls back to a uniquely named
    /// directory rather than let <see cref="Directory.Move(string, string)"/> throw into an
    /// install that has already fully staged its new binaries. That leaves two abandoned
    /// copies for a human to clear by hand instead of one, which is the honest cost of a
    /// double lock, not a defect this method can close on its own.
    /// </para>
    /// </summary>
    private static void SwapIntoPlace(string staging, string bin)
    {
        string retired = bin + ".old";
        TryDelete(retired);

        if (Directory.Exists(bin))
        {
            RetireDirectory(bin, retired);
        }

        Directory.Move(staging, bin);
        TryDelete(retired);
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

        Directory.Move(bin, $"{retired}.{Path.GetRandomFileName()}");
    }

    private static void TryDelete(string directory)
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
