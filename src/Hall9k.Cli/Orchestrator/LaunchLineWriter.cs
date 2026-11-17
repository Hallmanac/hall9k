using Hall9k.Domain.Features.Orchestrator;
using Spectre.Console;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// Writes the orchestrator launch line straight to the console's own writer, bypassing Spectre's
/// <c>Text</c> renderable and the word-wrap it applies at the profile width. That wrap fires even
/// when the output is piped (a pipe still measures 80 columns), so the launch line was arriving
/// broken across several lines however it reached the terminal. Writing raw, with a single
/// trailing <c>'\n'</c> — deliberately not <see cref="System.Environment.NewLine"/>, so the line
/// terminator is the same one byte on every platform rather than CRLF on Windows and LF
/// elsewhere — keeps the command one line: pasteable, one triple-click or drag selects it whole,
/// and a pipe sees exactly one line.
/// </summary>
public static class LaunchLineWriter
{
    public static void Write(string text)
    {
        TextWriter writer = AnsiConsole.Console.Profile.Out.Writer;
        writer.Write(text.TrimEnd('\r', '\n'));
        writer.Write('\n');
    }

    /// <summary>
    /// Writes a window's launch line with the bound account on it (<see cref="LaunchLineAccount.ForWindow"/>),
    /// and the sentence beneath it when the account could not be put on the line.
    /// </summary>
    public static void WriteWindowLine(LaunchText launchText, string? configDirectory)
    {
        PrintedLaunchLine printed = LaunchLineAccount.ForWindow(launchText, configDirectory, LaunchTextDefaults.HostShell);
        Write(printed.Line);
        if (printed.Notice is not null)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]{printed.Notice}[/]");
        }
    }
}
