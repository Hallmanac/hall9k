using Hall9k.Connectors.Text;
using Spectre.Console;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// Text the CLI prints that Hall9k did not author. Since adoption (PLAN.md §3.1a) an issue
/// title and body reach the terminal having been written by anyone who can file an issue, and
/// printed raw they are executed by the terminal rather than read by the human: an escape
/// sequence can clear the screen, recolour it, or repaint the row above, and a bidirectional
/// override can reverse what a line appears to say — either of which would let a work item make
/// 'h9k task show' lie about the task it is showing.
/// <para>
/// So the text is sanitised where it is displayed rather than where it is stored: the source
/// stays byte-for-byte what the item said, which is what the import contract promises and what
/// the agent is handed. Spectre's <c>EscapeMarkup()</c> is a different job and not a substitute
/// for this one: it neutralises Spectre's own markup syntax, not the terminal's control
/// characters. Outside text runs through here first and is escaped for markup afterwards.
/// </para>
/// <para>
/// Which characters act rather than read is <see cref="RelayedText"/>'s answer, not this type's.
/// The daemon asks the same question of the same text on its way into a commit subject, and the
/// two of them cannot reference each other; what belongs here is the pairing with Spectre.
/// </para>
/// </summary>
public static class ExternalText
{
    /// <summary>
    /// Outside text made safe to print as a block: the layout of a Markdown body survives and
    /// everything the terminal would obey is dropped (<see cref="RelayedText.Printable"/>).
    /// </summary>
    public static string ForTerminal(string text) => RelayedText.Printable(text);

    /// <summary>
    /// Outside text made safe to print inside a line: a prompt, a heading, a table cell. It drops
    /// everything <see cref="ForTerminal"/> drops and then folds the layout characters that one
    /// keeps down to spaces, because a title free to emit a newline can print lines of its own
    /// choosing underneath the question a human is being asked, which is the same lie told
    /// without a single escape sequence.
    /// </summary>
    public static string OneLine(string text) => RelayedText.OneLine(text);

    /// <summary>
    /// Outside text ready to be interpolated into a markup string: sanitised for the terminal
    /// first, escaped for Spectre second. The order matters — escaping only neutralises Spectre's
    /// own syntax, so text escaped and never sanitised still reaches the terminal with its escape
    /// sequences intact, which is precisely the hole this type exists to close.
    /// <para>
    /// A task objective goes through here on every surface that prints one. Adoption
    /// (PLAN.md §3.1a) seeds the objective from an issue title, so a field Hall9k used to author
    /// entirely can now be quoting someone who merely has permission to file an issue.
    /// </para>
    /// </summary>
    public static string OneLineMarkup(string text) => OneLine(text).EscapeMarkup();
}
