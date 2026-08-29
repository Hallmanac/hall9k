using System.ComponentModel;
using Hall9k.Cli.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The standalone half of the database doctor check (Decisions Log #58, #73): the same
/// four questions any other command runs automatically when it hits an unreachable
/// database, on demand, whether or not anything is actually broken right now.
/// </summary>
public sealed class DoctorCommand : Hall9kAsyncCommand<DoctorCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--yes")]
        [Description(
            "Remediate without asking: start Hall9k's own Postgres via the generated compose file "
            + "and create the schema, non-interactively — the shape a script or a dispatched agent "
            + "needs, since there is no terminal there to answer a prompt.")]
        public bool Yes { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken) =>
        await DatabaseDoctor.RunAsync(offerFixes: true, settings.Yes, cancellationToken) is not null
            ? ExitCodes.Ok
            : ExitCodes.Error;
}
