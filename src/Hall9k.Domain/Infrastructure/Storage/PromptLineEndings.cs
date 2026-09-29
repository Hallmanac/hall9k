using System.Text;

namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The one place a prompt's line-ending convention is decided: <c>\n</c> and nothing else, on
/// every platform. A builder assembles with <see cref="StringBuilder.AppendLine()"/>, which is
/// CRLF on Windows, and appends template files a Windows checkout may have converted to CRLF, so
/// each builder's public return passes through here and the same call yields the same bytes on
/// macOS and on windows-latest.
/// </summary>
public static class PromptLineEndings
{
    public static string Finish(StringBuilder prompt) => Normalize(prompt.ToString());

    /// <summary>
    /// A lone <c>\r</c> is converted too, so relayed text (a pull request body, an operator's
    /// note) that arrives with any terminator leaves as <c>\n</c>.
    /// </summary>
    public static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
