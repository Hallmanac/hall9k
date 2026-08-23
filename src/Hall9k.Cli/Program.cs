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
catch (NpgsqlException exception)
{
    await Console.Error.WriteLineAsync(
        $"Cannot reach Postgres: {exception.Message}\n" +
        "Is it running? Start it with: docker compose up -d  (or the Aspire AppHost)");
    return ExitCodes.Error;
}
