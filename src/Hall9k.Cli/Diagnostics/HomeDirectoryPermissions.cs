using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;

namespace Hall9k.Cli.Diagnostics;

/// <summary>
/// Enforces that <see cref="PlatformPaths.Home"/> is readable and traversable only by the
/// owning account on Unix (security review idea 6be68ee2, secrets-files-network finding 8):
/// one directory mode closes everything beneath it — config.json, keys/, run transcripts,
/// database dumps — whatever each file's own mode happens to be, so no per-file created-mode
/// rule is needed once this holds. A running daemon's open handles, Docker Desktop, and
/// launchd are unaffected, since all of them run as the same owning account.
/// <para>
/// Two entry points, one for each caller: <see cref="NarrowIfWider"/> is what <c>h9k install</c>
/// (and <c>h9k update</c>, through <see cref="Commands.InstallCommand.FinishAsync"/>) runs
/// unconditionally, narrowing without asking; <see cref="Check"/> is what <c>h9k doctor</c> runs,
/// reporting always and repairing only with <c>--yes</c> — the same shape as every other doctor
/// remediation in this project. Both are a single stat and, when something is actually wrong, a
/// single chmod: no recursive walk over a home that can hold thousands of run entries, and no
/// marker file recording that either one ran.
/// </para>
/// <para>
/// On Windows, <see cref="File.SetUnixFileMode(string, UnixFileMode)"/> throws
/// <see cref="PlatformNotSupportedException"/>, so both methods are guarded by
/// <see cref="OperatingSystem.IsWindows"/> rather than left to the exception — the profile ACL is
/// the boundary on that platform instead.
/// </para>
/// </summary>
public static class HomeDirectoryPermissions
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// h9k install and h9k update: narrows <see cref="PlatformPaths.Home"/> — a redirected
    /// <c>HALL9K_HOME</c> included, since it holds the same secrets — to 0700 whenever it is
    /// currently wider, printing one line only when it actually narrows something. A home already
    /// at 0700, or narrower (an operator's own tighter umask), is left alone and reported nowhere.
    /// </summary>
    public static void NarrowIfWider()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string home = PlatformPaths.Home;
        if (!Directory.Exists(home))
        {
            return;
        }

        UnixFileMode current = File.GetUnixFileMode(home);
        if (!IsWiderThanOwnerOnly(current))
        {
            return;
        }

        File.SetUnixFileMode(home, OwnerOnly);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Narrowed {home} from {FormatOctal(current)} to {FormatOctal(OwnerOnly)}[/]");
    }

    /// <summary>
    /// h9k doctor: reports a <see cref="PlatformPaths.Home"/> wider than 0700 — this also catches
    /// a home the daemon created before install ever ran (the dev loop, or a temp home) — and
    /// repairs it only when <paramref name="assumeYes"/> is set (<c>h9k doctor --yes</c>), the
    /// same bar every other doctor remediation in this project sets for an unattended fix. A
    /// missing home, or one already at 0700 or narrower, is silent: there is nothing to report.
    /// </summary>
    public static void Check(bool assumeYes)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string home = PlatformPaths.Home;
        if (!Directory.Exists(home))
        {
            return;
        }

        UnixFileMode current = File.GetUnixFileMode(home);
        if (!IsWiderThanOwnerOnly(current))
        {
            return;
        }

        AnsiConsole.MarkupLine(
            $"[red]{home.EscapeMarkup()} is {FormatOctal(current)}[/] — every other local account on this "
            + "machine can read into it (config.json, keys/, run transcripts, database dumps), whatever "
            + "each file's own mode is. Should be 0700.");

        if (assumeYes)
        {
            File.SetUnixFileMode(home, OwnerOnly);
            AnsiConsole.MarkupLine($"[green]Narrowed[/] {home.EscapeMarkup()} to {FormatOctal(OwnerOnly)}.");
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]Not narrowing it — run h9k doctor --yes to fix it.[/]");
        }
    }

    /// <summary>
    /// Whether <paramref name="mode"/> grants anything to the group or to others — the exact
    /// condition that makes a home reachable by another local account, regardless of setuid,
    /// setgid, or the sticky bit, none of which grant read or traverse access on their own.
    /// </summary>
    private static bool IsWiderThanOwnerOnly(UnixFileMode mode) => (mode & ~OwnerOnly) != 0;

    /// <summary>The standard four-digit octal rendering (<c>"0755"</c>) — <see cref="UnixFileMode"/>'s
    /// own flag values already line up with the POSIX permission bits, so this is a plain base-8
    /// conversion with no bit remapping of its own.</summary>
    private static string FormatOctal(UnixFileMode mode) => Convert.ToString((int)mode, 8).PadLeft(4, '0');
}
