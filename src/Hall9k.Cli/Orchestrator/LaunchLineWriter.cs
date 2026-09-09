using Spectre.Console;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// Writes the orchestrator launch line straight to the console's own writer, bypassing Spectre's
/// <c>Text</c> renderable and the word-wrap it applies at the profile width. That wrap fires even
/// when the output is piped (a pipe still measures 80 columns), so the launch line was arriving
/// broken across several lines however it reached the terminal. Writing raw, with a single
/// trailing <c>'\n'</c> — the same line terminator Spectre itself writes internally, never
/// <see cref="System.Environment.NewLine"/> — keeps the command one line: pasteable, one
/// triple-click or drag selects it whole, and a pipe sees exactly one line.
/// </summary>
public static class LaunchLineWriter
{
    public static void Write(string text)
    {
        TextWriter writer = AnsiConsole.Console.Profile.Out.Writer;
        writer.Write(text);
        writer.Write('\n');
    }
}
