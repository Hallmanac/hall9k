using System.ComponentModel;
using System.Diagnostics;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// The real <see cref="IOrchestratorProcessProbe"/>: one read of this machine's own process
/// table. Lives here rather than in the CLI or the daemon because both ask the question — the
/// CLI so its two presence surfaces are honest between sweeps, the daemon so it can record the
/// loss — and the reference graph gives them no other shared home
/// (<c>Cli → Domain</c>, <c>Daemon → Domain</c>, and neither sees the other).
/// </summary>
public sealed class OrchestratorProcessTableProbe : IOrchestratorProcessProbe
{
    public OrchestratorProcessSighting? Probe(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // No process with that id exists any more — the one answer that means gone.
            return null;
        }

        using (process)
        {
            try
            {
                return new OrchestratorProcessSighting(new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // The id is in use, but this user cannot read that process's start time (an
                // elevated or root-owned one, most often). Sighted with an unknown start time
                // rather than reported gone, and never stamped with a plausible substitute:
                // OrchestratorLiveness decides what the unknown means.
                return new OrchestratorProcessSighting(null);
            }
        }
    }
}
