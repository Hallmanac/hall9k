using System.ComponentModel;
using Hall9k.Cli.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The tool check (<see cref="ToolDoctor"/>) and the database doctor check (Decisions Log #58,
/// #73), in that order. The tool check runs first — and needs no daemon and no reachable
/// database itself — so it still runs on the database section's own early-return path below,
/// exactly the moment the generated project <c>AGENTS.md</c> promises it will. The database
/// check is the same four questions any other command runs automatically when it hits an
/// unreachable database, on demand, whether or not anything is actually broken right now.
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
            + "dispatched agent needs, since there is no terminal there to answer a prompt.")]
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
