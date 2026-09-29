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
    /// <summary>What a lone carriage return is shown as: SYMBOL FOR CARRIAGE RETURN (U+240D).</summary>
    public const string LoneCarriageReturnMark = "\u240D";

    public static string Finish(StringBuilder prompt) => Normalize(prompt.ToString());

    /// <summary>
    /// Folds every Windows line break (<c>\r\n</c>) to <c>\n</c>. A lone <c>\r</c> is not a line
    /// break, as <c>RelayedText.Printable</c> already rules, and it is never turned into one here:
    /// this runs after a builder has quoted or prefixed untrusted text line by line, so a lone
    /// carriage return that became a line break would add a line the quoting never saw (a
    /// <c>-</c> or <c>FINDING:</c> line inside a quoted diff hunk or a quoted fix summary), and
    /// parsers such as C# and YAML do treat it as one. It is rendered as the visible
    /// <see cref="LoneCarriageReturnMark"/> instead, so the prompt stays free of <c>\r</c> on every
    /// platform and a reader can still see that the bytes were there.
    /// </summary>
    public static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", LoneCarriageReturnMark, StringComparison.Ordinal);
}
