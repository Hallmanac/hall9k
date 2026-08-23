using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// <para>
/// A command line that never reached a command is a teaching moment, not a crash. The CLI runs with
/// <c>PropagateExceptions</c> so that domain failures can be mapped to exit codes by hand, and the
/// cost of that posture was that Spectre's own parse and binding failures had nobody to catch them:
/// they reached the runtime and printed a .NET stack trace. Origin incident (2026-08-20): a bare
/// <c>h9k task publish</c> answered a human — and would answer an agent — with
/// <c>Unhandled exception. Spectre.Console.Cli.CommandRuntimeException</c> and eight frames of
/// Spectre internals, when the one thing it had to say was "you left out the task id, here is what a
/// call looks like".
/// </para>
/// <para>
/// So a usage failure prints what was wrong and then the offending command's own help, which already
/// carries the description, the arguments, the options and at least one real example (AGENTS.md, CLI
/// command standards). Quoting the help rather than a hand-written hint keeps this honest: there is
/// one source for what a command looks like, and an agent reading the refusal can self-correct from
/// it without a second call.
/// </para>
/// </summary>
public static class UsageError
{
    /// <summary>
    /// The narrowest the help is ever rendered at, whatever the terminal says.
    /// </summary>
    /// <remarks>
    /// Spectre wraps to the console width, and a wrapped example is not a command: at the default
    /// 80 columns <c>h9k task resolve</c> prints its example broken after
    /// <c>--reason "Work merged as PR #7; only the daemon's</c>, with the rest on an unindented
    /// line of its own, so an agent pasting the correction it was just handed gets an unterminated
    /// quote. The longest example in the tree renders at 141 columns including its indent; this
    /// leaves room for a longer one, and <c>CommandTreeHelpTests</c> walks the tree at exactly this
    /// width so an example that outgrows it fails there rather than shipping wrapped.
    /// </remarks>
    internal const int MinimumHelpWidth = 160;

    /// <summary>
    /// Explain a Spectre parse or binding failure on <paramref name="error"/> and report the exit
    /// code for it.
    /// </summary>
    /// <param name="exception">The failure Spectre propagated instead of handling.</param>
    /// <param name="arguments">The command line as invoked — the source of the command path to quote help for.</param>
    /// <param name="error">Where the explanation goes; stderr in the running CLI.</param>
    /// <param name="cancellationToken">Cancellation for the writes.</param>
    public static async Task<int> ExplainAsync(
        CommandAppException exception,
        string[] arguments,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        await error.WriteLineAsync(exception.Message.AsMemory(), cancellationToken);
        await error.WriteLineAsync();
        await WriteHelpAsync(CommandPath(arguments), error, cancellationToken);
        return ExitCodes.Usage;
    }

    /// <summary>
    /// The leading tokens that could name a command: everything before the first option. A token
    /// after an option is a value, not a command name, so the path stops at the first dash.
    /// </summary>
    internal static string[] CommandPath(string[] arguments) =>
        [.. arguments.TakeWhile(argument => !argument.StartsWith('-'))];

    /// <summary>
    /// Render the help for the deepest prefix of <paramref name="path"/> that names something.
    /// </summary>
    /// <remarks>
    /// The path is a guess: it comes from a command line that already failed to parse, so its last
    /// token may be a stray argument (<c>h9k idea revise 28b19893</c>, one argument short) or a
    /// misspelling that names nothing (<c>h9k tsak</c>). Shortening until the help renders lands on
    /// the most specific thing the caller did get right, and the empty path — the root help, every
    /// branch listed — always renders, so the loop cannot fall through with nothing said.
    /// </remarks>
    private static async Task WriteHelpAsync(string[] path, TextWriter error, CancellationToken cancellationToken)
    {
        CommandApp help = new();
        help.Configure(config =>
        {
            CliCommandTree.Configure(config);
            config.ConfigureConsole(ConsoleWriting(error));
        });

        for (int depth = path.Length; depth >= 0; depth--)
        {
            try
            {
                await help.RunAsync([.. path.Take(depth), "--help"], cancellationToken);
                return;
            }
            catch (CommandAppException)
            {
                // That prefix names no command either. Try a shorter one; depth 0 is the root help.
            }
        }
    }

    /// <summary>
    /// A console over <paramref name="writer"/> that emits plain text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spectre's default profile enrichers switch ANSI back on whenever they recognise the host —
    /// CI runners among them — whatever <see cref="AnsiSupport.No"/> asked for, and help written to
    /// a redirected stderr with escape sequences in it is help a log file cannot show anyone.
    /// </para>
    /// <para>
    /// The width is widened to <see cref="MinimumHelpWidth"/> rather than pinned to it: a wide
    /// terminal keeps its own width and reads naturally, and a narrow one gets prose the terminal
    /// soft-wraps instead of examples the renderer hard-breaks. Soft-wrapped prose is a cosmetic
    /// cost; a hard-broken example is a command nobody can paste, which is the one thing this
    /// output exists to hand back.
    /// </para>
    /// </remarks>
    private static IAnsiConsole ConsoleWriting(TextWriter writer)
    {
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = Math.Max(console.Profile.Width, MinimumHelpWidth);
        return console;
    }
}
