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
    /// (Decisions Log #83): the data volume is never touched by a plain stop, so a later
    /// reinstall reconnects to exactly what was there.</summary>
    public static async Task<bool> StopRunningContainerAsync(ProcessRunner runner, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "docker", ["stop", PostgresRuntime.ContainerName], Directory.GetCurrentDirectory(), cancellationToken);
        return result.ExitCode == 0;
    }

    /// <summary>Removes the container itself (not its volume) — half of <c>h9k uninstall
    /// --purge-data</c>'s destructive tier (Decisions Log #83). <c>-f</c> so a still-running
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
    /// <c>Confirmed</c> is <see langword="false"/> when <c>docker volume ls</c> itself failed
    /// (nonzero exit — the daemon going away between an earlier call and this one, say), which is
    /// not the same fact as "no such volume": collapsing the two let a caller read a failed
    /// <c>docker volume ls</c> as a confirmed fact — "the legacy volume exists" for one caller,
    /// "the volume is already gone" for another — and report an outcome nobody actually observed,
    /// the identical stdout-versus-exit-code confusion <see cref="DataVolumeNameAsync"/> is
    /// hardened against. A caller sees <c>Confirmed: false, Exists: false</c> for that case, and
    /// must not read the unconfirmed <see langword="false"/> as an observed absence.
    /// </para>
    /// </summary>
    public static async Task<(bool Confirmed, bool Exists)> VolumeExistsAsync(
        ProcessRunner runner, CancellationToken cancellationToken, string? volumeName = null)
    {
        ProcessResult result = await runner(
            "docker",
            ["volume", "ls", "--filter", $"name=^{volumeName ?? PostgresRuntime.VolumeName}$", "--format", "{{.Name}}"],
            Directory.GetCurrentDirectory(),
            cancellationToken);
        return result.ExitCode != 0 ? (false, false) : (true, result.StandardOutput.Trim().Length > 0);
    }

    /// <summary>
    /// Every docker volume whose name contains <c>hall9k-pgdata</c> — deliberately unanchored and
    /// not limited to the two literals <see cref="PostgresRuntime.VolumeName"/> and
    /// <see cref="PostgresRuntime.LegacyVolumeName"/> enumerate, so it also catches a
    /// checkout-dirname-prefixed volume left behind by a pre-pin <c>docker compose up -d</c> run
    /// from the repository's own <c>docker-compose.yml</c> (docs/operations.md's Provisioning
    /// section: <c>&lt;checkout-dirname&gt;_hall9k-pgdata</c>), which neither literal names.
    /// <c>Confirmed</c> is <see langword="false"/> when <c>docker volume ls</c> itself failed —
    /// not the same fact as "no matching volume", the identical distinction
    /// <see cref="VolumeExistsAsync"/> carries.
    /// </summary>
    public static async Task<(bool Confirmed, IReadOnlyList<string> Names)> FindDataVolumesAsync(
        ProcessRunner runner, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "docker",
            ["volume", "ls", "--filter", "name=hall9k-pgdata", "--format", "{{.Name}}"],
            Directory.GetCurrentDirectory(),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            return (false, []);
        }

        string[] names = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (true, names);
    }

    /// <summary>Removes the named data volume — the other half of <c>h9k uninstall
    /// --purge-data</c> (Decisions Log #83), and the one call in this file that actually
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
    /// mount to report — a bind mount, or no volume mount at all; an anonymous volume does not
    /// land here, since <c>docker inspect</c> reports one under its generated hex name the same
    /// as any named volume — which callers must read as "there is no named volume to observe
    /// here" — never as licence to fall back to the pinned literal, which is exactly the guess
    /// this method exists to avoid.
    /// <para>
    /// <c>Confirmed</c> is <see langword="false"/> when <c>docker inspect</c> itself failed
    /// (nonzero exit — the daemon dropping the connection between an earlier call and this one,
    /// say), which is not the same fact as "this container mounts no named volume": collapsing
    /// the two would let a caller read a failed inspect as a confirmed absence and proceed to
    /// destroy the container while believing there was never a volume to observe, the identical
    /// stdout-versus-exit-code confusion <see cref="VolumeExistsAsync"/> is hardened against.
    /// A caller sees <c>Confirmed: false, Name: null</c> for that case, and only ever sees
    /// <c>Confirmed: true, Name: null</c> when the container was actually inspected and reported
    /// no named volume mount.
    /// </para>
    /// </summary>
    public static async Task<(bool Confirmed, string? Name)> DataVolumeNameAsync(ProcessRunner runner, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "docker",
            ["inspect", PostgresRuntime.ContainerName, "--format", "{{range .Mounts}}{{if eq .Type \"volume\"}}{{.Name}}\n{{end}}{{end}}"],
            Directory.GetCurrentDirectory(),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            return (false, null);
        }

        string name = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        return (true, name.Length > 0 ? name : null);
    }

    /// <summary>
    /// Stands up the platform-owned Postgres for the first time — writes the shipped compose
    /// definition if <c>h9k install</c> never got the chance to, then brings it up. Checks for
    /// <see cref="PostgresRuntime.LegacyVolumeName"/> first and refuses rather than starting:
    /// this is the one call site that would otherwise create a fresh, empty, pinned
    /// <see cref="PostgresRuntime.VolumeName"/> volume alongside a pre-pin installation's real
    /// data without a live container left to <c>docker inspect</c> and warn from (<see
    /// cref="PostgresRuntime.LegacyVolumeName"/>'s own remarks) — silently, since a fresh
    /// <c>docker compose up</c> against an absent container exits 0 either way. The reverse
    /// direction (a genuinely fresh machine that happens to have some unrelated volume by that
    /// name) is not a real risk this pins on: nothing but a pre-pin Hall9k install ever wrote
    /// data to that specific literal in the first place.
    /// </summary>
    public static async Task<ComposeUpResult> ComposeUpAsync(ProcessRunner runner, CancellationToken cancellationToken)
    {
        if (await VolumeExistsAsync(runner, cancellationToken, PostgresRuntime.LegacyVolumeName))
        {
            return ComposeUpResult.LegacyVolumeDetected;
        }

        if (!File.Exists(PostgresRuntime.ComposeFile))
        {
            PostgresRuntime.WriteComposeFile();
        }
        ProcessResult result = await runner(
            "docker", ["compose", "-f", PostgresRuntime.ComposeFile, "up", "-d"],
            PostgresRuntime.ComposeDirectory, cancellationToken);
        return result.ExitCode == 0 ? ComposeUpResult.Started : ComposeUpResult.Failed;
    }
}

/// <summary>What <see cref="ContainerRuntimeProbe.ComposeUpAsync"/> actually did — three
/// outcomes, not a <see langword="bool"/>, because <see cref="LegacyVolumeDetected"/> is a
/// refusal a caller must report differently from an ordinary <c>docker compose up</c>
/// failure: the fix is a manual volume migration (docs/operations.md's Provisioning section),
/// never a retry of the same command.</summary>
public enum ComposeUpResult
{
    Started,
    Failed,
    LegacyVolumeDetected,
}
