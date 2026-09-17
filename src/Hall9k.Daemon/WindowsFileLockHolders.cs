using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Hall9k.Daemon;

/// <summary>
/// Names the processes holding an open handle on a file, for the one diagnosis a bare
/// "Win32 error 32" cannot give: <em>who</em> the sharing violation was lost to.
/// <para>
/// Windows exposes no supported way to walk a file's handle table from user mode, but the
/// Restart Manager (<c>rstrtmgr.dll</c>) answers exactly this question — it is what an MSI
/// installer uses to ask "which applications must I close to replace this file?" — and it
/// needs no elevation for a same-user, same-session holder, which is every holder h9kd can
/// plausibly lose <c>h9kd.log</c> to. Origin incident: two reports on mailbox issue #1 (the
/// Windows project window at the v0.4.0 restart, 2026-09-08 00:28 EDT, and the Windows node
/// window after the v0.5.1 install, 2026-09-09 09:40 EDT) both ended at the same dead end,
/// a fallback line that said error 32 and nothing about the holder, and both guessed wrong
/// about who it was.
/// </para>
/// <para>
/// Every failure path here reports the gap rather than filling it in: an unavailable holder
/// reads as explicitly unknown, with the Restart Manager result code that made it unknown,
/// because an audit line that guesses at provenance is worse than one that admits it does
/// not know (AGENTS.md, "Never guess at unobserved facts").
/// </para>
/// </summary>
internal static class WindowsFileLockHolders
{
    // CCH_RM_SESSION_KEY + 1, CCH_RM_MAX_APP_NAME + 1, CCH_RM_MAX_SVC_NAME + 1 from restartmanager.h.
    private const int SessionKeyCharacters = 33;
    private const int ApplicationNameCharacters = 256;
    private const int ServiceShortNameCharacters = 64;

    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;

    /// <summary>
    /// A one-sentence description of who holds <paramref name="path"/> right now, suitable
    /// for appending to an exception message or a log line. Never throws: the caller is
    /// already on a failure path, and a diagnosis that can itself fail the diagnosis is
    /// worse than one that reports "unknown".
    /// </summary>
    public static string Describe(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            // POSIX has no mandatory sharing, so nothing here has a caller off Windows —
            // but a test tier that runs on both must be able to call it and read an honest
            // answer rather than a platform exception.
            return "Holder lookup is Windows-only (POSIX has no mandatory file sharing to lose).";
        }

        try
        {
            return DescribeThroughRestartManager(path);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return $"The holder is unknown (the Restart Manager is unavailable on this machine: {exception.Message}).";
        }
    }

    private static string DescribeThroughRestartManager(string path)
    {
        StringBuilder sessionKey = new(SessionKeyCharacters);
        int started = RmStartSession(out uint session, 0, sessionKey);
        if (started != ErrorSuccess)
        {
            return $"The holder is unknown (Restart Manager RmStartSession returned {started}).";
        }

        try
        {
            int registered = RmRegisterResources(session, 1, [path], 0, null, 0, null);
            if (registered != ErrorSuccess)
            {
                return $"The holder is unknown (Restart Manager RmRegisterResources returned {registered}).";
            }

            uint rebootReasons = 0;
            uint count = 0;
            int listed = RmGetList(session, out uint needed, ref count, null, ref rebootReasons);
            if (listed == ErrorSuccess && needed == 0)
            {
                // A real answer, not a gap: the Restart Manager looked and found nobody. The
                // holder has closed its handle since the open failed, or it is one the
                // Restart Manager cannot see (a kernel-mode filter, a handle in another
                // terminal-services session).
                return "The Restart Manager named no holder — whoever held it has let go, or is not a process it can see.";
            }

            if (listed != ErrorMoreData)
            {
                return $"The holder is unknown (Restart Manager RmGetList returned {listed}).";
            }

            count = needed;
            RestartManagerProcessInfo[] holders = new RestartManagerProcessInfo[count];
            listed = RmGetList(session, out needed, ref count, holders, ref rebootReasons);
            if (listed != ErrorSuccess)
            {
                return $"The holder is unknown (Restart Manager RmGetList returned {listed} on its second call).";
            }

            if (count == 0)
            {
                return "The Restart Manager named no holder — whoever held it has let go, or is not a process it can see.";
            }

            IEnumerable<string> described = holders.Take((int)count).Select(Describe);
            return $"Held by {string.Join("; ", described)}.";
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private static string Describe(RestartManagerProcessInfo holder)
    {
        DateTime? startedAt = StartTime(holder.Process);

        // The Restart Manager answers with the executable's FileDescription resource, which
        // is both localized and marketing prose — "Windows Command Processor" rather than
        // "cmd". The image name is what a reader can act on, so it leads when it is
        // available, with the descriptive name kept beside it because a generic host process
        // ("dotnet", "svchost") is the case where the description is the informative half.
        string image = ImageName(holder.Process.ProcessId, startedAt);
        bool described = !string.IsNullOrWhiteSpace(holder.ApplicationName);
        string name = (image, described) switch
        {
            ("", true) => $"\"{holder.ApplicationName}\"",
            ("", false) => "an unidentified process",
            _ => image,
        };
        string description = image.Length > 0 && described ? $", \"{holder.ApplicationName}\"" : string.Empty;

        // A zero or unrepresentable FILETIME is the Restart Manager saying it has no start
        // time for this process, not 1601-01-01 — reported as the gap it is, never formatted
        // into a stamp nothing observed.
        string started = startedAt is { } stamp ? $"started {stamp:u}" : "start time unreported";
        return $"{name} (pid {holder.Process.ProcessId}{description}, {started})";
    }

    private static DateTime? StartTime(RestartManagerUniqueProcess process)
    {
        long fileTime = ((long)process.ProcessStartTimeHigh << 32) | (uint)process.ProcessStartTimeLow;
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// The holder's own image name, or the empty string when it cannot be read. A pid alone
    /// is not an identity — Windows recycles them — so the Restart Manager's start time is
    /// checked against the live process's before its name is believed, the same pid-plus-
    /// start-time rule <c>DaemonPidFile</c> follows for the daemon's own identity
    /// (Decisions Log #2).
    /// </summary>
    private static string ImageName(int processId, DateTime? startedAt)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (startedAt is { } stamp
                && Math.Abs((process.StartTime.ToUniversalTime() - stamp).TotalSeconds) > 1)
            {
                return string.Empty;
            }

            return process.ProcessName;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Gone between RmGetList and here, or a process this one may not query at all
            // (an elevated or another-session holder). The pid and the Restart Manager's own
            // name still stand on their own.
            return string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RestartManagerUniqueProcess
    {
        public int ProcessId;
        public int ProcessStartTimeLow;
        public int ProcessStartTimeHigh;
    }

    /// <summary>
    /// <c>RM_PROCESS_INFO</c>. Every field after <see cref="ApplicationName"/> is here for its
    /// size alone: the Restart Manager writes a fixed-layout array, so dropping an unread
    /// field would silently misalign every element after the first rather than fail to
    /// compile. Nothing here is read but the process identity and the two names.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RestartManagerProcessInfo
    {
        public RestartManagerUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ApplicationNameCharacters)]
        public string ApplicationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ServiceShortNameCharacters)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint ApplicationStatus;
        public uint TerminalServicesSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint dwSessionHandle,
        uint nFiles,
        string[]? rgsFilenames,
        uint nApplications,
        RestartManagerUniqueProcess[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RestartManagerProcessInfo[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint dwSessionHandle);
}
