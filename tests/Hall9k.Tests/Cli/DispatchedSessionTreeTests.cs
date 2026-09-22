using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Walks the live <see cref="CliCommandTree"/> exactly the way <see cref="CommandTreeHelpTests"/>
/// does (task: a dispatched session cannot drive the project's own lifecycle) and proves every
/// registered command's own <c>CommandSettings</c> type has an entry in
/// <see cref="DispatchedSessionCommandClassification.BySettingsType"/>. A future command that
/// registers with no entry fails here rather than silently reaching a dispatched session unrefused
/// — the same "check what ships" discipline <see cref="CommandTreeHelpTests"/> already applies to
/// descriptions and examples.
/// </summary>
public sealed class DispatchedSessionTreeTests
{
    public static TheoryData<string> EveryCommand() => CommandTreeHelpTests.EveryCommand();

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public void Every_command_is_classified_read_only_allowed_or_refused(string path)
    {
        string[] segments = path.Split(' ');
        string help = CommandTreeHelpTests.Help(segments);
        IReadOnlyList<string> examples = CommandTreeHelpTests.Examples(help);
        examples.Should().NotBeEmpty($"h9k {path} needs a worked example to resolve its own settings type from");

        Type settingsType = SettingsTypeOf(examples[0]);

        DispatchedSessionCommandClassification.BySettingsType.Should().ContainKey(
            settingsType,
            $"h9k {path} (settings {settingsType.Name}) reaches a dispatched session unclassified — " +
            "add it to DispatchedSessionCommandClassification as read-only, allowed, or refused");
    }

    /// <summary>
    /// Runs one printed example through the shipped tree, stopping the instant Spectre has bound
    /// and validated its settings — the same technique <see cref="CommandTreeHelpTests.Parse"/>
    /// uses to prove an example is runnable, repurposed here to capture which settings CLR type the
    /// example actually bound to.
    /// </summary>
    private static Type SettingsTypeOf(string example)
    {
        IReadOnlyList<string> tokens = CommandTreeHelpTests.Tokenize(example);

        CaptureSettingsType interceptor = new();
        CommandApp app = new();
        app.Configure(config =>
        {
            // Never the real process environment (Configure's single-arg overload): this test
            // process can itself inherit HALL9K_DISPATCHED_RUN_ID from a dispatched session running
            // this very suite, which would let DispatchedSessionInterceptor — registered ahead of
            // this class's own interceptor below, in Spectre's own registration order — throw before
            // CaptureSettingsType ever runs (independent pre-PR review, cycle 1, conformance finding).
            CliCommandTree.Configure(config, _ => null);
            config.SetInterceptor(interceptor);
            config.UseStrictParsing();
        });

        try
        {
            app.Run(BarePositionalRecording.Normalise([.. tokens.Skip(1)]));
        }
        catch (CaptureSettingsType.Captured)
        {
            // Parsed, bound and validated — exactly the point this needs to stop at.
        }

        return interceptor.SettingsType
            ?? throw new InvalidOperationException($"'{example}' never reached a command with its settings bound.");
    }

    /// <summary>Stops an example the instant its settings are bound, recording their CLR type.</summary>
    private sealed class CaptureSettingsType : ICommandInterceptor
    {
        public Type? SettingsType { get; private set; }

        public void Intercept(CommandContext context, CommandSettings settings)
        {
            SettingsType = settings.GetType();
            throw new Captured();
        }

        public sealed class Captured : Exception;
    }
}
