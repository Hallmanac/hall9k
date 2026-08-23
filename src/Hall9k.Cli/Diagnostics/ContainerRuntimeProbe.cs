using System.ComponentModel;
using System.Net.Sockets;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.Diagnostics;

/// <summary>
/// Question 4 of the doctor check, asked only when nothing was configured (Decisions Log
/// #73): is a container runtime running, is there a native Postgres on the standard port,
/// and — the nicest possible outcome — is there a stopped <c>hall9k-postgres</c> container
/// from a previous session. Docker only for now; Podman and Apple's container framework
/// are equally valid Postgres hosts (Decisions Log #57) but this probe, and the start
/// offer built on it, speak Docker's CLI specifically.
/// </summary>
public static class ContainerRuntimeProbe
{
    public static async Task<ContainerRuntimeStatus> RuntimeStatusAsync(ProcessRunner runner, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await runner("docker", ["info"], Directory.GetCurrentDirectory(), cancellationToken);
            return result.ExitCode == 0 ? ContainerRuntimeStatus.Running : ContainerRuntimeStatus.NotRunning;
        }
        catch (Win32Exception)
        {
            // Process.Start's own report that there is no docker on PATH to run at all.
            return ContainerRuntimeStatus.NotInstalled;
        }
        catch (TimeoutException)
        {
            return ContainerRuntimeStatus.NotRunning;
        }
    }

    /// <summary>Only meaningful once <see cref="RuntimeStatusAsync"/> says <see cref="ContainerRuntimeStatus.Running"/>.</summary>
    public static async Task<PostgresContainerStatus> Hall9kContainerStatusAsync(ProcessRunner runner, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "docker",
            ["ps", "-a", "--filter", $"name=^/{PostgresRuntime.ContainerName}$", "--format", "{{.State}}"],
            Directory.GetCurrentDirectory(),
            cancellationToken);
        return result.StandardOutput.Trim() switch
        {
            "" => PostgresContainerStatus.Absent,
            "running" => PostgresContainerStatus.Running,
            _ => PostgresContainerStatus.Stopped,
        };
    }

    /// <summary>A bare TCP connect — no Postgres handshake, so it cannot tell native Postgres
    /// apart from anything else bound to the port, and says so in how it is reported.</summary>
    public static async Task<bool> PortListeningAsync(string host, int port, CancellationToken cancellationToken)
    {
        using TcpClient client = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await client.ConnectAsync(host, port, linked.Token);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The internal 2s timeout fired, not the caller's token — that reads as "not
            // listening", the same as a refused connection. A caller cancellation is not
            // this method's to swallow: it propagates, so the doctor check actually stops
            // instead of reporting a port state nobody asked it to keep checking for.
            return false;
        }
    }

    public static async Task<bool> StartStoppedContainerAsync(ProcessRunner runner, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "docker", ["start", PostgresRuntime.ContainerName], Directory.GetCurrentDirectory(), cancellationToken);
        return result.ExitCode == 0;
    }

    /// <summary>Stands up the platform-owned Postgres for the first time — writes the shipped
    /// compose definition if <c>h9k install</c> never got the chance to, then brings it up.</summary>
    public static async Task<bool> ComposeUpAsync(ProcessRunner runner, CancellationToken cancellationToken)
    {
        if (!File.Exists(PostgresRuntime.ComposeFile))
        {
            PostgresRuntime.WriteComposeFile();
        }
        ProcessResult result = await runner(
            "docker", ["compose", "-f", PostgresRuntime.ComposeFile, "up", "-d"],
            PostgresRuntime.ComposeDirectory, cancellationToken);
        return result.ExitCode == 0;
    }
}
