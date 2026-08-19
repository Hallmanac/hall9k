using System.Diagnostics;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon;

/// <summary>
/// The daemon's single-instance guard (Decisions Log #31): an advisory lock file held
/// open for the process lifetime, plus a pid file recording pid + process start time
/// for the CLI to read. The CLI's own pre-start check is the polite refusal; this is
/// the race-proof backstop — two daemons started in the same instant cannot both hold
/// the lock. Origin context: starting the daemon is now a routine human act
/// (h9k daemon start), so double-starts stop being hypothetical.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly FileStream _lock;
    private readonly string _pidFilePath;

    private SingleInstanceGuard(FileStream lockStream, string pidFilePath)
    {
        _lock = lockStream;
        _pidFilePath = pidFilePath;
    }

    /// <summary>
    /// Null when another instance already holds the lock. Paths are parameters so tests
    /// run against a temp directory; production passes the DaemonRuntime paths.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(string lockFilePath, string pidFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockFilePath)!);
        FileStream lockStream;
        try
        {
            // FileShare.None maps to an exclusive advisory lock on Unix; the stream is
            // held open until Dispose, so the lock lives exactly as long as the process.
            lockStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }

        using Process current = Process.GetCurrentProcess();
        DaemonPidFile.Write(pidFilePath, new DaemonProcessDescriptor(
            current.Id,
            new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero)));
        return new SingleInstanceGuard(lockStream, pidFilePath);
    }

    public void Dispose()
    {
        DaemonPidFile.Delete(_pidFilePath);
        _lock.Dispose();
    }
}
