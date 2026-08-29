using Hall9k.Cli.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Shared.Exceptions;
using Npgsql;
using Spectre.Console.Cli;

CommandApp app = new();
app.Configure(CliCommandTree.Configure);

// Without this, Ctrl-C takes SIGINT's default action and the process terminates immediately —
// no command's own cancellation handling (e.g. TaskWorkCommand's started/ended pairing, PLAN.md
// #101) ever runs, because RunAsync(args) alone is handed CancellationToken.None and nothing else
// requests a stop (independent pre-PR review, cycle 2). Cancelling here instead of letting the
// default handler fire is what lets a command's own try/catch(OperationCanceledException) close
// out cleanly before the process actually exits.
using CancellationTokenSource cancellation = new();
int cancelKeyPresses = 0;
DateTimeOffset lastCancelKeyPressAt = DateTimeOffset.MinValue;
// A repeat press only escalates within this window of the previous one — the "the first press
// didn't work, so kill it" case the escalation exists for. Ctrl-C is also the ordinary Claude
// Code keystroke for "stop generating" (h9k task work's own attached session, PLAN.md #101), and
// a lifetime counter that never resets treated a second, unrelated press an hour later as the
// same escalation: it killed h9k while the still-attached child survived the very keystroke that
// killed its parent, silently leaving InteractiveSessionStarted unpaired (independent pre-PR
// review, cycle 1). Long enough is what matters here, not short: a command blocked in a
// synchronous prompt that observes no token (h9k task deliver's handoff prompt, Spectre's
// AnsiConsole.Prompt) leaves an operator pressing Ctrl-C, waiting to see whether it took, then
// pressing again — a retry cadence of several seconds is the ordinary shape of that, not a
// deliberate rapid double-press, so a three-second window silently swallowed every press and
// never escalated (independent pre-PR review, cycle 2). Half a minute is short next to the "an
// hour later" gap the window exists to catch, and long next to any realistic wait-and-retry.
TimeSpan escalationWindow = TimeSpan.FromSeconds(30);
Console.CancelKeyPress += (_, e) =>
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    if (now - lastCancelKeyPressAt > escalationWindow)
    {
        cancelKeyPresses = 0;
    }

    lastCancelKeyPressAt = now;

    // Only the first press in the window is suppressed. A command blocked in a synchronous
    // prompt that takes no token (Spectre's AnsiConsole.Prompt, which reads through
    // Console.ReadKey) never observes the cancellation below, so without an escalation path it
    // would hang forever with every further Ctrl-C swallowed the same way (independent pre-PR
    // review, cycle 1). A second press within the window leaves e.Cancel at its default (false)
    // and lets SIGINT's ordinary action terminate the process, exactly as it did before this
    // handler existed.
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
