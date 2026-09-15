using System.Text.Json;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Prompts;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The <c>PreToolUse</c> hook body behind
/// <see cref="ClaudeSettingsFile.ReviewThreadReplyGuardHook"/> (task: a review-feedback follow-up
/// never answers a human reviewer in the owner's name on its own). Claude Code runs it before
/// every Bash tool call in a follow-up session, hands it the call as JSON on stdin, and it
/// refuses the shell routes that write inside somebody's review thread — so the only way to one
/// is <c>h9k pr reply</c>, which can tell a bot's thread from a person's.
/// <para>
/// Not a command an operator ever types. It is registered in the tree anyway rather than hidden,
/// because a hook that fails silently is the worst kind: an operator debugging why a session's
/// reply was refused needs to be able to run the same check by hand, with the same JSON, and see
/// the same answer.
/// </para>
/// <para>
/// <b>It touches no database and opens no store.</b> It runs on every Bash call in a follow-up
/// session, so its cost is a process start and a regex; anything more would be paid hundreds of
/// times a lap for a check that only ever looks at the command text.
/// </para>
/// <para>
/// <b>It fails open, everywhere.</b> Unreadable stdin, unparseable JSON, a tool that is not Bash,
/// a payload shaped differently by a future Claude Code — every one of them exits 0 and lets the
/// call run. A guard that failed closed would block every command in every follow-up the first
/// time it mis-parsed something, which is a far worse failure than the one it prevents, and the
/// prompt's own rule still stands over the session either way.
/// </para>
/// </summary>
public sealed class PullRequestReplyGuardCommand : Hall9kAsyncCommand<PullRequestReplyGuardCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    /// <summary>
    /// Claude Code's own contract for a blocking PreToolUse hook: exit 2, with the reason on
    /// stderr, which the harness feeds back to the model so it can correct itself. The structured
    /// <c>permissionDecision</c> object is written to stdout alongside it — belt and braces
    /// across the two shapes the hook protocol accepts, neither of which this repository can
    /// verify from inside a test.
    /// </summary>
    private const int DenyExitCode = 2;

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        string payload = await Console.In.ReadToEndAsync(cancellationToken);
        if (!Denies(payload))
        {
            return ExitCodes.Ok;
        }

        await Console.Out.WriteLineAsync(DenialJson());
        await Console.Error.WriteLineAsync(ReviewThreadReplyRoutes.RefusalReason);
        return DenyExitCode;
    }

    /// <summary>
    /// Whether this hook payload names a Bash call that writes into a review thread. Internal so
    /// the decision is testable against real payload shapes rather than only through a process.
    /// Every parse failure answers false — see the class doc on failing open.
    /// </summary>
    internal static bool Denies(string? payload)
    {
        if (payload.IsBlank())
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // The tool name is checked rather than assumed even though the hook is registered
            // with a Bash matcher: a settings file an operator edited, or a future matcher
            // spelling, must not turn this into a guard over tools whose input it cannot read.
            if (root.TryGetProperty("tool_name", out JsonElement tool)
                && tool.ValueKind == JsonValueKind.String
                && !string.Equals(tool.GetString(), "Bash", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return root.TryGetProperty("tool_input", out JsonElement input)
                && input.ValueKind == JsonValueKind.Object
                && input.TryGetProperty("command", out JsonElement command)
                && command.ValueKind == JsonValueKind.String
                && ReviewThreadReplyRoutes.WritesIntoAReviewThread(command.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The structured half of the refusal. Built with the serializer rather than by hand so the
    /// reason's own quotes and newlines are escaped correctly — this string is read by another
    /// program, and a hand-built one that broke on an apostrophe would fail open silently.
    /// </summary>
    internal static string DenialJson() => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new
        {
            hookEventName = "PreToolUse",
            permissionDecision = "deny",
            permissionDecisionReason = ReviewThreadReplyRoutes.RefusalReason,
        },
    });
}
