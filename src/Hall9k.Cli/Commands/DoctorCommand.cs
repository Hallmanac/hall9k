using Hall9k.Cli.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The standalone half of the database doctor check (Decisions Log #58, #73): the same
/// four questions any other command runs automatically when it hits an unreachable
/// database, on demand, whether or not anything is actually broken right now.
/// </summary>
public sealed class DoctorCommand : Hall9kAsyncCommand<EmptyCommandSettings>
{
    protected override async Task<int> ExecuteAsync(EmptyCommandSettings settings, CancellationToken cancellationToken) =>
        await DatabaseDoctor.RunAsync(offerFixes: true, cancellationToken) is not null ? ExitCodes.Ok : ExitCodes.Error;
}
