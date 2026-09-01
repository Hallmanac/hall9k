using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// StreamRenderer's outside-text discipline: a transcript's model id, tool names, malformed-line
/// fallback, and the assistant's own prose are all text hall9k did not author, so none of it may
/// crash <c>h9k logs</c> or reach the terminal unsanitised. Origin incident (Windows field
/// deployment, 2026-08-31): <c>AgentModel.PlatformFallback</c> is literally
/// <c>claude-opus-5[1m]</c>, and Spectre reads <c>[1m]</c> as a color tag it does not recognise,
/// so every install that never overrode the model crashed <c>h9k logs</c> on essentially every
/// run.
/// </summary>
public sealed class StreamRendererTests
{
    private static readonly char EscapeChar = (char)0x1B;

    // A Spectre markup token ("[red]") folded together with a terminal escape sequence and a
    // lone carriage return, the same combination TaskStatusMarkupTests uses for the browse
    // surface's own outside-text sites.
    private static readonly string EscapeSequencePayload =
        "x[red]" + EscapeChar + "[2J" + EscapeChar + "[H" + '\r' + "y";

    // A value carrying its own layout characters, used to tell OneLineMarkup's fold-to-one-line
    // behaviour apart from ForTerminalMarkup's keep-the-layout one — the exact distinction
    // between the model id, tool name, and malformed-line fallback (all framed inside a single
    // line) and the assistant's own prose (rendered as a multi-line block).
    private const string LayoutPayload = "first\nsecond\tthird";

    [Fact]
    public void A_system_init_line_carrying_the_platform_fallback_model_id_renders_without_throwing()
    {
        string line = SystemInitLine("claude-opus-5[1m]");

        string? rendered = StreamRenderer.TryRenderLine(line);

        rendered.Should().NotBeNull();
        Action act = () => RenderPlain(rendered!);
        act.Should().NotThrow("a raw model id used to be read back as Spectre markup and crash the command");
        RenderPlain(rendered!).Should().Contain("claude-opus-5[1m]",
            "the model id is printed literally rather than interpreted as markup");
    }

    [Theory]
    [InlineData("model")]
    [InlineData("tool-name")]
    [InlineData("malformed-line")]
    [InlineData("assistant-text")]
    public void Every_outside_text_site_is_sanitised_for_the_terminal_before_being_escaped_for_markup(string site)
    {
        string line = site switch
        {
            "model" => SystemInitLine(EscapeSequencePayload),
            "tool-name" => AssistantToolUseLine(EscapeSequencePayload),
            "malformed-line" => EscapeSequencePayload,
            "assistant-text" => AssistantTextLine(EscapeSequencePayload),
            _ => throw new ArgumentOutOfRangeException(nameof(site)),
        };

        string? rendered = StreamRenderer.TryRenderLine(line);

        rendered.Should().NotBeNull();
        rendered.Should().NotContain(EscapeChar.ToString(),
            "a terminal escape sequence must never reach the terminal, wherever it was smuggled in from");
        rendered.Should().NotContain("\r",
            "a lone carriage return can overwrite the line printed above it");
        rendered.Should().Contain("[[red]]",
            "a value carrying Spectre's own markup syntax is escaped rather than parsed as a tag");

        Action act = () => RenderPlain(rendered!);
        act.Should().NotThrow("the escaped, sanitised text must still be valid Spectre markup");
    }

    [Theory]
    [InlineData("model")]
    [InlineData("tool-name")]
    [InlineData("malformed-line")]
    public void A_value_framed_inside_a_single_line_has_its_own_layout_characters_folded_to_spaces(string site)
    {
        string line = site switch
        {
            "model" => SystemInitLine(LayoutPayload),
            "tool-name" => AssistantToolUseLine(LayoutPayload),
            "malformed-line" => LayoutPayload,
            _ => throw new ArgumentOutOfRangeException(nameof(site)),
        };

        string? rendered = StreamRenderer.TryRenderLine(line);

        rendered.Should().NotBeNull();
        rendered.Should().NotContain("\n",
            "a stray newline would print lines of its own choosing outside this site's single-line frame");
        rendered.Should().NotContain("\t",
            "a stray tab would misalign this site's single-line frame");
    }

    [Fact]
    public void The_assistant_s_own_prose_keeps_the_line_breaks_it_was_written_with()
    {
        string line = AssistantTextLine(LayoutPayload);

        string? rendered = StreamRenderer.TryRenderLine(line);

        rendered.Should().NotBeNull();
        rendered.Should().Contain("first\nsecond\tthird",
            "assistant prose renders as the multi-line block the assistant wrote, not folded to one line");
    }

    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\"}]}}")]
    [InlineData("{\"type\":\"result\",\"is_error\":\"not-a-bool\"}")]
    [InlineData("{\"type\":\"result\",\"usage\":{\"output_tokens\":\"not-a-number\"}}")]
    public void A_structurally_valid_but_wrong_shaped_line_falls_back_instead_of_throwing(string line)
    {
        Action act = () => StreamRenderer.TryRenderLine(line);

        act.Should().NotThrow(
            "System.Text.Json throws InvalidOperationException/KeyNotFoundException for a shape it " +
            "didn't expect even when the JSON itself parses fine, and TryRenderLine's fallback is " +
            "meant to cover that, not just a JsonException from broken syntax");
    }

    private static string SystemInitLine(string model) =>
        "{\"type\":\"system\",\"subtype\":\"init\",\"model\":" + JsonSerializer.Serialize(model) + "}";

    private static string AssistantToolUseLine(string toolName) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":"
        + JsonSerializer.Serialize(toolName) + "}]}}";

    private static string AssistantTextLine(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":"
        + JsonSerializer.Serialize(text) + "}]}}";

    /// <summary>
    /// A console that adds no escape sequences of its own, so the only ones a rendered line could
    /// carry are the ones the value smuggled in — the same technique <c>TaskStatusMarkupTests</c>
    /// uses to check composed markup without spawning a real terminal.
    /// </summary>
    private static string RenderPlain(string markup)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 200;

        console.MarkupLine(markup);

        return writer.ToString();
    }
}
