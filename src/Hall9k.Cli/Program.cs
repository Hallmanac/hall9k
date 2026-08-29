using Hall9k.Cli.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Shared.Exceptions;
using Npgsql;
using Spectre.Console.Cli;

CommandApp app = new();
app.Configure(CliCommandTree.Configure);

// Without this, Ctrl-C takes SIGINT's default action and the process terminates immediately —
// no command's own cancellation handling (e.g. TaskWorkCommand's started/ended pairing, PLAN.md
// #99) ever runs, because RunAsync(args) alone is handed CancellationToken.None and nothing else
// requests a stop (independent pre-PR review, cycle 2). Cancelling here instead of letting the
// default handler fire is what lets a command's own try/catch(OperationCanceledException) close
// out cleanly before the process actually exits.
using CancellationTokenSource cancellation = new();
int cancelKeyPresses = 0;
Console.CancelKeyPress += (_, e) =>
{
    // Only the first Ctrl-C is suppressed. A command blocked in a synchronous prompt that takes
    // no token (Spectre's AnsiConsole.Prompt, which reads through Console.ReadKey) never observes
    // the cancellation below, so without an escalation path it would hang forever with every
    // further Ctrl-C swallowed the same way (independent pre-PR review, cycle 1). The second
    // press leaves e.Cancel at its default (false) and lets SIGINT's ordinary action terminate
    // the process, exactly as it did before this handler existed.
    if (Interlocked.Increment(ref cancelKeyPresses) == 1)
    {
        e.Cancel = true;
        cancellation.Cancel();
    }
};

try
{
    return await app.RunAsync(args, cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    // The command's own cleanup (if any) already ran against this same token before this
    // propagated here; there is nothing left to explain to the operator that Ctrl-C did not
    // already tell them.
    return ExitCodes.Error;
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
    await DatabaseDoctor.RunAsync(offerFixes: false, assumeYes: false, CancellationToken.None);
    return ExitCodes.Error;
}
catch (NpgsqlException)
{
    await DatabaseDoctor.RunAsync(offerFixes: false, assumeYes: false, CancellationToken.None);
    return ExitCodes.Error;
}
