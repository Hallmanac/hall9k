using System.Text;
using System.Text.Json;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Human-readable rendering of a run's stream.jsonl. Tolerant by design: unknown or
/// malformed lines render as dimmed raw text rather than failing the command.
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
        catch (JsonException)
        {
            return $"  [dim]{line}[/]";
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
        return $"[dim]— session started ({model}) —[/]";
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
                        output.AppendLine(Spectre.Console.Markup.Escape(text.Trim()));
                    }

                    break;
                case "tool_use":
                    string tool = item.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "?" : "?";
                    output.AppendLine($"  [blue]⚙ {tool}[/]");
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
