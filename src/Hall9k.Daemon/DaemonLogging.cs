namespace Hall9k.Daemon;

/// <summary>
/// How the daemon writes its log. Stdout IS the log file when the CLI starts the daemon
/// detached, so this is not cosmetic: it decides what an operator (and every h9k daemon
/// status tail) actually sees.
/// </summary>
public static class DaemonLogging
{
    public static ILoggingBuilder Configure(ILoggingBuilder logging)
    {
        // One line per entry keeps the log greppable (h9k daemon start tails it for the
        // catch-up marker) and readable in the h9k daemon status tail.
        logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });

        // Data-access chatter buries the daemon's own diagnostics: one observed 4.7 MB
        // log held 20,955 Npgsql.Command lines against 79 lines from the daemon itself,
        // so an unfiltered status tail shows SQL and nothing else. These filters live in
        // code rather than appsettings.json because the installed daemon's working
        // directory is never its binary directory (~/.hall9k when the CLI starts it, the
        // home directory under launchd), so the published appsettings.json is not read
        // there at all — a configured level would apply in the dev loop and nowhere
        // else. Warnings and errors from both still come through.
        logging.AddFilter("Npgsql", LogLevel.Warning);
        logging.AddFilter("Marten", LogLevel.Warning);

        return logging;
    }
}
