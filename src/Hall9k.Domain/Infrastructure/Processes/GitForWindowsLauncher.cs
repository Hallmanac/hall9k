using System.Diagnostics;

namespace Hall9k.Domain.Infrastructure.Processes;

/// <summary>
/// Finds the real <c>git.exe</c> behind Git for Windows' launcher, so the daemon starts that one
/// instead. The launcher (<c>cmd\git.exe</c>, and <c>bin\git.exe</c>, which is the same program)
/// starts the real git and is still running when that git exits, and on the Windows build this
/// platform runs on that leaves one kernel access token (pool tag <c>Toke</c>) allocated until
/// reboot. About nine in ten of h9kd's leaked tokens came from its git launches, so every git the
/// daemon starts goes to the real one directly.
/// <para>
/// The launcher is the first <c>git.exe</c> on PATH, and only when it sits in a directory named
/// <c>cmd</c> or <c>bin</c> whose parent also holds the real one under <c>ucrt64\bin</c>,
/// <c>mingw64\bin</c> or <c>clangarm64\bin</c>. That second condition is what tells the launcher's
/// <c>bin</c> from the real git's own <c>ucrt64\bin</c>. When the first git on PATH is anything
/// else, and on macOS and Linux, nothing here changes anything.
/// </para>
/// <para>
/// The real git's directory is also put on PATH, immediately before the launcher's directory and
/// with no other entry moved, so what git starts (<c>sh</c>, <c>git-remote-https</c>) and what the
/// daemon's other children find by name is the real git's own set of programs. It is inserted
/// beside the launcher and not first because that directory carries about sixty other programs
/// (curl, openssl, xz, tclsh and more) that must not shadow anything earlier on the user's PATH.
/// </para>
/// <para>
/// Nothing is copied from the launcher's environment: the real git run under a minimal PATH sets
/// <c>MSYSTEM</c> and its own PATH and finds <c>sh</c> itself.
/// </para>
/// </summary>
public sealed class GitForWindowsLauncher
{
    private const string GitByName = "git";
    private static readonly string[] LauncherDirectoryNames = ["cmd", "bin"];
    private static readonly string[] RealGitFlavours = ["ucrt64", "mingw64", "clangarm64"];

    private readonly object _gate = new();
    private readonly string _path;
    private readonly bool _enabled;
    private bool _searched;
    private string? _launcher;
    private string? _realGit;

    /// <summary>The one every daemon start site shares, over this process's PATH as it is on first use.</summary>
    public static GitForWindowsLauncher Shared { get; } =
        new(Environment.GetEnvironmentVariable("PATH") ?? string.Empty, OperatingSystem.IsWindows());

    /// <param name="path">The PATH to search for the first <c>git.exe</c>.</param>
    /// <param name="enabled">False off Windows, where there is no launcher to avoid.</param>
    internal GitForWindowsLauncher(string path, bool enabled)
    {
        _path = path;
        _enabled = enabled;
    }

    /// <summary>
    /// Makes <paramref name="startInfo"/> run the real git: a file name of <c>git</c> (or
    /// <c>git.exe</c>) becomes the real git's path, and the real git's directory goes into the
    /// start info's PATH before the launcher's directory. Applying it twice changes nothing more.
    /// When the first git on PATH is not the launcher, or the real one cannot be found, git is
    /// left to be started by name. The launcher is searched for once; the real git is looked up
    /// again from the launcher's location whenever the path last resolved no longer exists (a Git
    /// for Windows update replaces the directory).
    /// </summary>
    public void Apply(ProcessStartInfo startInfo)
    {
        if (Current() is not { } resolved)
        {
            return;
        }

        if (startInfo.FileName.Equals(GitByName, StringComparison.OrdinalIgnoreCase)
            || startInfo.FileName.Equals("git.exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = resolved.RealGit;
        }

        if (startInfo.Environment.TryGetValue("PATH", out string? path) && path is not null)
        {
            startInfo.Environment["PATH"] = InsertBeforeLauncher(path, resolved);
        }
    }

    /// <summary>
    /// Puts the real git's directory into this process's own PATH, so every process it starts
    /// afterwards (agent sessions, gate shells, gh) inherits it. The daemon calls it once at start-up.
    /// </summary>
    public void ApplyToCurrentProcess()
    {
        if (Current() is not { } resolved)
        {
            return;
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string inserted = InsertBeforeLauncher(path, resolved);
        if (!string.Equals(path, inserted, StringComparison.Ordinal))
        {
            Environment.SetEnvironmentVariable("PATH", inserted);
        }
    }

    internal Resolved? Current()
    {
        if (!_enabled)
        {
            return null;
        }

        lock (_gate)
        {
            if (!_searched)
            {
                _searched = true;
                _launcher = FindLauncher(_path);
            }

            if (_launcher is null)
            {
                return null;
            }

            if (_realGit is null || !File.Exists(_realGit))
            {
                _realGit = FindRealGit(_launcher);
            }

            return _realGit is null ? null : new Resolved(_launcher, _realGit);
        }
    }

    /// <summary>The first <c>git.exe</c> on <paramref name="path"/>, if it is Git for Windows' launcher.</summary>
    internal static string? FindLauncher(string path)
    {
        string? first = path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim().Trim('"'))
            .Where(entry => entry.Length > 0)
            .Select(entry => Path.Combine(entry, "git.exe"))
            .FirstOrDefault(File.Exists);

        return first is not null && FindRealGit(first) is not null ? first : null;
    }

    /// <summary>
    /// The real <c>git.exe</c> the launcher at <paramref name="launcher"/> starts, or null when
    /// <paramref name="launcher"/> is not in a <c>cmd</c> or <c>bin</c> directory of an install.
    /// </summary>
    internal static string? FindRealGit(string launcher)
    {
        string? directory = Path.GetDirectoryName(launcher);
        string? root = directory is null ? null : Path.GetDirectoryName(directory);
        if (directory is null
            || root is null
            || !LauncherDirectoryNames.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return RealGitFlavours
            .Select(flavour => Path.Combine(root, flavour, "bin", "git.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static string InsertBeforeLauncher(string path, Resolved resolved)
    {
        string launcherDirectory = Normalize(Path.GetDirectoryName(resolved.Launcher) ?? string.Empty);
        string realDirectory = Path.GetDirectoryName(resolved.RealGit) ?? string.Empty;
        string[] entries = path.Split(Path.PathSeparator);

        int at = Array.FindIndex(entries, entry => Normalize(entry).Equals(launcherDirectory, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            return path;
        }

        bool alreadyBefore = at > 0 && Normalize(entries[at - 1]).Equals(Normalize(realDirectory), StringComparison.OrdinalIgnoreCase);
        return alreadyBefore
            ? path
            : string.Join(Path.PathSeparator, entries.Take(at).Append(realDirectory).Concat(entries.Skip(at)));
    }

    private static string Normalize(string entry) => Path.TrimEndingDirectorySeparator(entry.Trim().Trim('"'));

    internal sealed record Resolved(string Launcher, string RealGit);
}
