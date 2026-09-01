using System.Text;
using System.Text.Json;
using Hall9k.Cli.Infrastructure;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Human-readable rendering of a run's stream.jsonl. Tolerant by design: unknown or
/// malformed lines render as dimmed raw text rather than failing the command.
/// <para>
/// A transcript is outside text (<see cref="ExternalText"/>): the model id, tool names, and the
/// assistant's own prose are not hall9k's, and a malformed line's raw fallback is unparsed input
/// straight from the file. Every interpolation site that carries any of that routes through
/// <see cref="ExternalText.OneLineMarkup"/> (the model id, tool name, and malformed-line
/// fallback, each framed inside a single line of its own) or
/// <see cref="ExternalText.ForTerminalMarkup"/> (the assistant's own prose, rendered as a block
/// that is meant to keep the line breaks the assistant wrote) so a value that happens to look
/// like Spectre markup (<c>claude-opus-5[1m]</c>, parsed as a color tag named <c>1m</c> if left
/// raw) or that carries a terminal escape sequence can neither crash the command nor reach the
/// terminal unsanitised.
/// </para>
/// </summary>
public static class StreamRenderer
{
    public static IEnumerable<string> Render(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            if (line.IsBlank())
            {
                continue;
            }

            string? rendered = TryRenderLine(line);
            if (rendered.IsNotBlank())
            {
                yield return rendered;
            }
        }
    }

    internal static string? TryRenderLine(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string? type = root.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null;

            return type switch
            {
                "system" => RenderSystem(root),
                "assistant" => RenderAssistant(root),
                "user" => null,             // tool results echoed back — transcript noise
                "result" => RenderResult(root),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            // JsonException is a parse failure; the rest are System.Text.Json's own reaction to a
            // shape it didn't expect (a missing property, a root that isn't an object, a value of
            // the wrong kind) - structurally valid JSON that is still not the transcript shape
            // this method assumes. Every such line is malformed from this renderer's point of
            // view, so it gets the same raw-text fallback as a JSON syntax error.
            return $"  [dim]{ExternalText.OneLineMarkup(line)}[/]";
        }
    }

    private static string? RenderSystem(JsonElement root)
    {
        string? subtype = root.TryGetProperty("subtype", out JsonElement s) ? s.GetString() : null;
        if (subtype != "init")
        {
            return null;
        }

        string model = root.TryGetProperty("model", out JsonElement m) ? m.GetString() ?? "?" : "?";
        return $"[dim]— session started ({ExternalText.OneLineMarkup(model)}) —[/]";
    }

    private static string? RenderAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        StringBuilder output = new();
        foreach (JsonElement item in content.EnumerateArray())
        {
            string? itemType = item.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
            switch (itemType)
            {
                case "text":
                    string text = item.GetProperty("text").GetString() ?? string.Empty;
                    if (text.IsNotBlank())
                    {
                        output.AppendLine(ExternalText.ForTerminalMarkup(text.Trim()));
                    }

                    break;
                case "tool_use":
                    string tool = item.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "?" : "?";
                    output.AppendLine($"  [blue]⚙ {ExternalText.OneLineMarkup(tool)}[/]");
                    break;
            }
        }

        string result = output.ToString().TrimEnd();
        return result.IsBlank() ? null : result;
    }

    private static string RenderResult(JsonElement root)
    {
        // The result's text duplicates the final assistant message (already rendered);
        // only the outcome and token count are new information here.
        bool isError = root.TryGetProperty("is_error", out JsonElement e) && e.GetBoolean();
        long tokens = root.TryGetProperty("usage", out JsonElement usage)
            && usage.TryGetProperty("output_tokens", out JsonElement output)
            ? output.GetInt64()
            : 0;

        string suffix = tokens > 0 ? $" ({tokens} output tokens)" : string.Empty;
        return isError
            ? $"[red]— agent finished with an error{suffix} —[/]"
            : $"[green]— agent finished{suffix} —[/]";
    }
}
