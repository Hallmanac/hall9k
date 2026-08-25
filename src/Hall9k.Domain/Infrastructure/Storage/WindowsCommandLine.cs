namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// cmd.exe's own <c>/c</c> argument parsing does not follow the CommandLineToArgvW
/// convention every ordinary Windows program (and <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>,
/// which assumes it) uses: passing a command that carries its own embedded quotes — a
/// quoted path, a quoted redirection target — through <c>ArgumentList</c> gets it
/// C-runtime-escaped (<c>\"</c>), and cmd.exe does not undo that escaping the way a normal
/// argv-parsing program would, so the quotes land in the command wrong.
/// <para>
/// The documented workaround (cmd's own <c>/?</c> describes the fallback rule this relies
/// on): wrap the whole command in one EXTRA pair of quotes, set it as the raw
/// <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/> string — never
/// <c>ArgumentList</c>, which would re-escape it — and cmd.exe's own "first character is a
/// quote" fallback strips exactly that outer pair, leaving every quote inside (around a
/// path, a redirected file) exactly as written. Shared by every cmd.exe invocation that
/// carries embedded quotes: <c>WindowsProcessManager</c>, <c>DaemonLifecycle.SpawnDetachedWindows</c>,
/// and the Task Scheduler action <c>WindowsDaemonAutostart</c> registers.
/// </para>
/// </summary>
public static class WindowsCommandLine
{
    public static string WrapForCmdExe(string command) => $"/c \"{command}\"";
}
