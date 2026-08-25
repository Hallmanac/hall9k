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

    /// <summary>Stops the container without removing it — <c>h9k uninstall</c>'s default tier
    /// (Decisions Log #82): the data volume is never touched by a plain stop, so a later
    /// reinstall reconnects to exactly what was there.</summary>
    public static async Task<bool> StopRunningContainerAsync(ProcessRunner runner, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "docker", ["stop", PostgresRuntime.ContainerName], Directory.GetCurrentDirectory(), cancellationToken);
        return result.ExitCode == 0;
    }

    /// <summary>Removes the container itself (not its volume) — half of <c>h9k uninstall
    /// --purge-data</c>'s destructive tier (Decisions Log #82). <c>-f</c> so a still-running
    /// container is stopped and removed in one call rather than needing the stop above first.</summary>
    public static async Task<bool> RemoveContainerAsync(ProcessRunner runner, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "docker", ["rm", "-f", PostgresRuntime.ContainerName], Directory.GetCurrentDirectory(), cancellationToken);
        return result.ExitCode == 0;
    }

    /// <summary>Whether the named data volume exists at all — asked before
    /// <see cref="RemoveVolumeAsync"/> so a machine that never started Postgres (install
    /// deliberately does not start it, Decisions Log #58) reads as "nothing to purge" rather than
    /// a failed <c>docker volume rm</c> against a volume nobody ever created. Defaults to
    /// <see cref="PostgresRuntime.VolumeName"/>, the name a fresh install's pinned compose file
    /// creates, but a caller purging an existing container asks <see cref="DataVolumeNameAsync"/>
    /// what it actually mounts first and checks that name instead — see that method's own
    /// remarks for why the bare literal cannot be trusted on its own.
    /// <para>
    /// A failed <c>docker volume ls</c> (nonzero exit — the daemon going away between an earlier
    /// call and this one, say) is not the same fact as "no such volume", and answering
    /// <see langword="false"/> for it would read exactly like a confirmed absence to a caller
    /// that treats "does not exist" as "already gone, nothing left to remove". Answering
    /// <see langword="true"/> instead is the fail-safe direction: it sends a purge on to actually
    /// attempt (and, if docker is really unreachable, honestly fail) the removal, rather than
    /// skip it and report a volume destroyed that nobody observed being destroyed.
    /// </para>
    /// </summary>
    public static async Task<bool> VolumeExistsAsync(
        ProcessRunner runner, CancellationToken cancellationToken, string? volumeName = null)
    {
        ProcessResult result = await runner(
            "docker",
            ["volume", "ls", "--filter", $"name=^{volumeName ?? PostgresRuntime.VolumeName}$", "--format", "{{.Name}}"],
            Directory.GetCurrentDirectory(),
            cancellationToken);
        return result.ExitCode != 0 || result.StandardOutput.Trim().Length > 0;
    }

    /// <summary>Removes the named data volume — the other half of <c>h9k uninstall
    /// --purge-data</c> (Decisions Log #82), and the one call in this file that actually
    /// destroys recorded work rather than merely pausing or starting a container. Callers check
    /// <see cref="VolumeExistsAsync"/> first: this call is not itself idempotent against an
    /// absent volume.</summary>
    public static async Task<bool> RemoveVolumeAsync(
        ProcessRunner runner, CancellationToken cancellationToken, string? volumeName = null)
    {
        ProcessResult result = await runner(
            "docker", ["volume", "rm", volumeName ?? PostgresRuntime.VolumeName],
            Directory.GetCurrentDirectory(), cancellationToken);
        return result.ExitCode == 0;
    }

    /// <summary>
    /// Asks <c>hall9k-postgres</c> itself which named volume it has mounted, rather than
    /// assuming the bare literal <see cref="PostgresRuntime.VolumeName"/> — the name only a
    /// compose file carrying this branch's <c>name:</c> pin actually produces. A container
    /// created before that pin landed (or brought up from a checkout whose own
    /// <c>docker-compose.yml</c> was never pinned) mounts a Compose-project-prefixed name
    /// instead, e.g. <c>postgres_hall9k-pgdata</c>, and <c>h9k uninstall --purge-data</c>
    /// destroying the container while guessing at the volume's name would either miss the real
    /// volume and report destruction that never happened, or — worse — hit an unrelated volume
    /// that happens to carry the guessed literal, such as the Aspire dev loop's own
    /// pre-migration <c>hall9k-pgdata</c> (see <see cref="Hall9k.Domain.Infrastructure.Storage.PostgresRuntime.VolumeName"/>'s
    /// own remarks). Returns <see langword="null"/> when the container has no named volume
    /// mount to report (absent, or an anonymous-volume/bind-mount container), which callers
    /// read as "fall back to the pinned literal" for the case that literal is actually right.
    /// </summary>
    public static async Task<string?> DataVolumeNameAsync(ProcessRunner runner, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "docker",
            ["inspect", PostgresRuntime.ContainerName, "--format", "{{range .Mounts}}{{if eq .Type \"volume\"}}{{.Name}}\n{{end}}{{end}}"],
            Directory.GetCurrentDirectory(),
            cancellationToken);
        string name = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        return name.Length > 0 ? name : null;
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
