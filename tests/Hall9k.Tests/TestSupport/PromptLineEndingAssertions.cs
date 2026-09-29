using FluentAssertions;

namespace Hall9k.Tests.TestSupport;

/// <summary>
/// The check a prompt test runs on a builder's raw output, before any normalization: every prompt
/// leaves its builder with <c>\n</c> as its only line terminator (<c>PromptLineEndings</c>), so a
/// carriage return here means a builder returned text that skipped that convention, which only
/// windows-latest would show.
/// </summary>
internal static class PromptLineEndingAssertions
{
    public static void ShouldHaveOnlyLineFeeds(this string prompt) =>
        prompt.Should().NotContain("\r", "a prompt uses \\n as its only line terminator on every platform");
}
