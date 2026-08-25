namespace Hall9k.Daemon.ProcessManagement;

/// <summary>
/// The one piece of shell syntax <c>/bin/sh</c> and <c>cmd.exe</c> already agree on:
/// <c>&lt;</c>, <c>&gt;</c>, and <c>2&gt;</c> file redirection. Shared so both platform
/// spawns build the identical tail of their command line, differing only in which shell
/// interprets the whole of it.
/// </summary>
internal static class ShellRedirection
{
    public static string Wrap(ProcessSpawnRequest request)
    {
        string command = request.StandardInputFile is { } standardInputFile
            ? $"{request.Command} < \"{standardInputFile}\""
            : request.Command;
        return $"{command} > \"{request.StandardOutputFile}\" 2> \"{request.StandardErrorFile}\"";
    }
}
