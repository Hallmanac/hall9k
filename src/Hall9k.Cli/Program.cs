using Hall9k.Cli.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Shared.Exceptions;
using Npgsql;
using Spectre.Console.Cli;

CommandApp app = new();
app.Configure(CliCommandTree.Configure);

try
{
    return await app.RunAsync(args);
}
catch (CommandAppException exception)
{
    // Spectre's own parse and binding failures: a missing argument, an unknown command, a settings
    // rule the caller broke. PropagateExceptions handed them to us, so we owe them an explanation
    // rather than a stack trace (UsageError carries the origin incident). No token: this is the
    // process's last act, printing help nobody can usefully interrupt.
    return await UsageError.ExplainAsync(exception, args, Console.Error, CancellationToken.None);
}
catch (DomainValidationException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return ExitCodes.Validation;
}
catch (DomainNotFoundException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return ExitCodes.NotFound;
}
catch (DomainConflictException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return ExitCodes.Conflict;
}
catch (DomainBusinessRuleException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return ExitCodes.BusinessRule;
}
catch (DatabaseNotConfiguredException)
{
    // The first command that needs a database and cannot reach one runs the doctor check
    // instead of failing raw (Decisions Log #58, #73) — it re-derives the whole situation
    // fresh rather than this catch trying to explain it from the exception alone. The
    // command itself still failed regardless of what the doctor finds, so the exit code
    // is always Error here.
    await DatabaseDoctor.RunAsync(offerFixes: false, CancellationToken.None);
    return ExitCodes.Error;
}
catch (NpgsqlException)
{
    await DatabaseDoctor.RunAsync(offerFixes: false, CancellationToken.None);
    return ExitCodes.Error;
}
