using System.ComponentModel;
using Hall9k.Cli.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The tool check (<see cref="ToolDoctor"/>), the container port-binding check
/// (<see cref="DatabaseDoctor.CheckContainerPortBindingAsync(bool,System.Threading.CancellationToken)"/>,
/// security review idea 6be68ee2), and the database doctor check (Decisions Log #58, #73), in that
/// order. The tool check runs first — and needs no daemon and no reachable database itself — so
/// it still runs on the database section's own early-return path below, exactly the moment the
/// generated project <c>AGENTS.md</c> promises it will. The port-binding check runs unconditionally
/// too, on every invocation of this command, whether or not the four database questions that
/// follow it find anything wrong: it is a question about the container Docker actually created,
/// not about the connection string. The database check itself is the same four questions any other
/// command runs automatically when it hits an unreachable database, on demand, whether or not
/// anything is actually broken right now.
/// </summary>
public sealed class DoctorCommand : Hall9kAsyncCommand<DoctorCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--yes")]
        [Description(
            "Remediate without asking: start Hall9k's own Postgres via the generated compose file "
            + "and create the schema, or — if hall9k-postgres is already confirmed running — record "
            + "the connection string that points at it, non-interactively — the shape a script or a "
            + "dispatched agent needs, since there is no terminal there to answer a prompt. Also "
            + "recreates hall9k-postgres when it is publishing port 5432 on anything but 127.0.0.1, "
            + "and migrates its password off the shipped default onto a generated one when config.json "
            + "still names that default — both guarded the same way: only when the container mounts "
            + "exactly the pinned hall9k-pgdata volume, was created from this install's own compose "
            + "file, and no daemon is running, otherwise this prints the exact commands to fix it by "
            + "hand instead.")]
        public bool Yes { get; init; }

        [CommandOption("--no-configure")]
        [Description(
            "Repair, but never record a connection string: with nothing configured, diagnose and stop "
            + "rather than writing Hall9k's default into the platform config file. What --yes does for "
            + "an address that IS configured — starting a stopped hall9k-postgres, creating or updating "
            + "the schema — is unaffected. h9k update --restart and h9k install --restart pass this to "
            + "the doctor step they run between the stop and the start, because a daemon whose database "
            + "is named by HALL9K_CONNECTION_STRING in another shell, or by a .hall9k-connection file "
            + "elsewhere on disk, would otherwise come back up against a guessed default that outranks "
            + "both from then on (Decisions Log #118).")]
        public bool NoConfigure { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        await ToolDoctor.RunAsync(cancellationToken);

        await DatabaseDoctor.CheckContainerPortBindingAsync(settings.Yes, cancellationToken);

        if (await DatabaseDoctor.RunAsync(
            offerFixes: true, settings.Yes, cancellationToken,
            recordConnectionStringIfUnconfigured: !settings.NoConfigure) is null)
        {
            return ExitCodes.Error;
        }

        await JiraDoctor.RunAsync(cancellationToken);
        return ExitCodes.Ok;
    }
}
