using System.ComponentModel;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Publish-and-refresh installation (Decisions Log #31): release binaries into
/// ~/.hall9k/bin, h9k linked onto the PATH, and — deliberately — no background service,
/// no login item, no autostart of any kind. Re-running after a merge republishes the
/// binaries idempotently and offers to restart a running daemon, which is the answer to
/// installed-binary staleness (origin incident: the hand-made h9k symlink went stale
/// the moment main advanced).
/// </summary>
public sealed class InstallCommand : Hall9kAsyncCommand<InstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--repo <PATH>")]
        [Description("The hall9k repository root to publish from — the directory holding Hall9k.slnx, taken as given and never searched upward (default: found by walking up from the current directory)")]
        public string? Repo { get; init; }

        [CommandOption("--restart")]
        [Description("Restart a running daemon onto the fresh binaries without asking")]
        public bool Restart { get; init; }

        [CommandOption("--no-restart")]
        [Description("Leave a running daemon on its current binaries (it picks up the new ones at its next start)")]
        public bool NoRestart { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync(
                "h9k install on Windows arrives with S1-14 (Decisions Log #3); macOS (and other Unix) only for now.");
            return ExitCodes.Error;
        }

        string? repoRoot = ResolveRepositoryRoot(settings.Repo);
        if (repoRoot is null)
        {
            await Console.Error.WriteLineAsync(DescribeMissingRepository(settings.Repo));
            return ExitCodes.Error;
        }

        DaemonProcessDescriptor? runningBefore = DaemonProcess.Probe();

        string staging = DaemonRuntime.StagingBinDirectory;
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
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

        SwapIntoPlace(staging, DaemonRuntime.BinDirectory);
        AnsiConsole.MarkupLineInterpolated(
            $"[green]Installed[/]: h9k and h9kd release binaries in {DaemonRuntime.BinDirectory}");

        // Ships Hall9k's own Postgres definition into ~/.hall9k (Decisions Log #73), so
        // h9k daemon start's reachability probe and h9k doctor's start-offer never need a
        // repo checkout — an installed user has no dev worktree to run compose from. No
        // prompt and nothing started here: install stays boring (Decisions Log #58).
        PostgresRuntime.WriteComposeFile();
        AnsiConsole.MarkupLine(
            $"[dim]Wrote Hall9k's own Postgres definition to {PostgresRuntime.ComposeFile.EscapeMarkup()} "
            + "(not started — h9k doctor or h9k daemon start will offer to when it's needed).[/]");

        PublishSkills(repoRoot);

        LinkOntoPath(
            Path.Combine(DaemonRuntime.BinDirectory, "h9k"),
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        AnsiConsole.MarkupLine(
            "[dim]No background service was registered — the daemon runs on demand (h9k daemon start / stop). "
            + "Start-at-login is a separate, explicit opt-in: h9k daemon autostart enable.[/]");

        return runningBefore is null
            ? ExitCodes.Ok
            : await OfferRestartAsync(settings, runningBefore, cancellationToken);
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
    /// and is as self-contained as the binaries. Today the source is this repository's own
    /// <c>.claude/skills</c>; once release delivery lands (backlog 42) it is a release artefact,
    /// and only this method changes — <see cref="SkillLibraryPaths.CanonicalDirectory"/> is the
    /// seam every project home already points at.
    /// <para>
    /// A project home's <c>skills/</c> entries are symlinks into that directory, so republishing
    /// here updates every project's platform skills in one move. A skill that is <em>new</em>
    /// still needs a link made for it, which is what <c>h9k project init</c> does — so that is
    /// said rather than left to be discovered.
    /// </para>
    /// </summary>
    /// <remarks>The copying itself is <see cref="SkillSeeder.PublishCanonical"/>; this is the
    /// command's half of it, which is finding the source and saying what happened.</remarks>
    private static void PublishSkills(string repoRoot)
    {
        string source = Path.Combine(repoRoot, ".claude", "skills");
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
    /// </summary>
    private static void SwapIntoPlace(string staging, string bin)
    {
        string retired = bin + ".old";
        if (Directory.Exists(retired))
        {
            Directory.Delete(retired, recursive: true);
        }

        if (Directory.Exists(bin))
        {
            Directory.Move(bin, retired);
        }

        Directory.Move(staging, bin);
        if (Directory.Exists(retired))
        {
            Directory.Delete(retired, recursive: true);
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
        Settings settings, DaemonProcessDescriptor runningBefore, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLineInterpolated(
            $"[yellow]h9kd is running (pid {runningBefore.ProcessId}) on the previous binaries.[/]");

        bool restart = settings switch
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
