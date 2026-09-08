using System.Diagnostics;
using System.Text;

namespace Hall9k.Tests.TestSupport;

/// <summary>
/// The one place a test decides which shell to hand a script to, mirroring the choice the
/// daemon's own spawns make (<c>UnixProcessManager</c> versus <c>WindowsProcessManager</c>):
/// <c>/bin/sh -c</c> on Unix, Windows PowerShell on Windows. A script is written twice, once per
/// dialect, because the two shells agree on almost nothing a scripted stand-in needs — Unix's
/// <c>sleep</c>, <c>printf</c> and single-quoted literals have no <c>cmd.exe</c> equivalent at
/// all, and <c>cmd.exe</c> cannot pause for less than a second.
/// <para>
/// PowerShell rather than <c>cmd.exe</c> for exactly that reason, and it is handed its script
/// <c>-EncodedCommand</c> (base64 UTF-16LE) rather than <c>-Command</c>: encoding sidesteps the
/// whole of <c>cmd.exe</c>'s and PowerShell's overlapping quoting rules, so a script carrying
/// JSON — every quote character of it — reaches the shell exactly as written.
/// <c>powershell.exe</c> rather than <c>pwsh</c> because only the former is present on every
/// Windows machine.
/// </para>
/// </summary>
internal static class PlatformShell
{
    /// <summary>
    /// True where the platform's shell is <c>/bin/sh</c>. Callers render their script in the
    /// dialect this selects; it is exposed so a test can also pick between two spellings of
    /// something that is not a whole script (a gate command's path syntax, say).
    /// </summary>
    public static bool IsPosix => !OperatingSystem.IsWindows();

    /// <summary>
    /// A start info that runs <paramref name="posixScript"/> under <c>/bin/sh -c</c>, or
    /// <paramref name="powerShellScript"/> under <c>powershell.exe</c>, depending on the platform.
    /// </summary>
    public static ProcessStartInfo StartInfo(string posixScript, string powerShellScript)
    {
        ProcessStartInfo startInfo = new() { UseShellExecute = false };
        if (IsPosix)
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(posixScript);
            return startInfo;
        }

        startInfo.FileName = "powershell.exe";
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(powerShellScript)));
        return startInfo;
    }

    /// <summary>
    /// A literal for <c>/bin/sh</c>: single-quoted, with any embedded single quote closed,
    /// escaped and reopened, which is the only way <c>sh</c> lets one appear inside such a quote.
    /// </summary>
    public static string PosixLiteral(string value) => $"'{value.Replace("'", @"'\''")}'";

    /// <summary>
    /// A literal for PowerShell: single-quoted, where an embedded single quote is doubled and
    /// nothing else — no expansion of any kind happens inside such a quote.
    /// </summary>
    public static string PowerShellLiteral(string value) => $"'{value.Replace("'", "''")}'";
}
