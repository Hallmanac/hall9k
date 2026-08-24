using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Whether a run's agent session already wrote its terminal result to disk. A dead process
/// with a result on disk is a finished session, not a dead one: startup adoption resumes it
/// (<see cref="RunSupervisor.AdoptOrphansAsync"/> re-monitors on alive-or-result) and the
/// lease-expiry sweep must therefore not record it as failed. The two live on opposite sides
/// of the daemon, so the question is asked here, once, and answered the same way for both.
/// </summary>
public static class RunResultFile
{
    /// <summary>
    /// True when the run's stream file holds a terminal result line. The file is opened
    /// share-everything because the agent may still be appending to it, and a missing file
    /// simply means nothing was written yet.
    /// </summary>
    public static async Task<bool> AlreadyWrittenAsync(string runDirectory, CancellationToken cancellationToken)
    {
        string streamFile = RunPaths.StreamFile(runDirectory);
        if (!File.Exists(streamFile))
        {
            return false;
        }

        using StreamReader reader = new(new FileStream(
            streamFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (StreamJsonParser.TryParseResult(line, out _))
            {
                return true;
            }
        }

        return false;
    }
}
