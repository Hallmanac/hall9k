using System.Text.Json;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Prompts;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The <c>PreToolUse</c> hook body behind a pr-review session's own git read-only guard
/// (<see cref="ClaudeSettingsFile.GitReadOnlyGuardMatcher"/>, independent pre-PR review, cycle 1,
/// both lenses): Claude Code runs it before every shell tool call in a pr-review session's own
/// permission file — Bash, and on a Windows node the PowerShell tool beside it — hands it the call
/// as JSON on stdin, and it refuses a <c>git diff</c>/<c>git log</c> call that carries an
/// <c>--output</c> flag, which is the one way either subcommand writes outside the checkout
/// despite the allow list's own prefix rules treating both as read-only.
/// <para>
/// Not a command an operator ever types. It is registered in the tree anyway rather than hidden,
/// on the identical terms <see cref="PullRequestReplyGuardCommand"/> states for itself: an
/// operator debugging an unexpected refusal needs to run the same check by hand, with the same
/// JSON, and see the same answer.
/// </para>
/// <para>
/// <b>It fails open, everywhere</b> — unreadable stdin, unparseable JSON, a tool that is neither
/// shell, a payload shaped differently by a future Claude Code all exit 0 and let the call run,
/// for the identical reason <see cref="PullRequestReplyGuardCommand"/> fails open: a guard that
/// failed closed would block every shell call in every pr-review session the first time it
/// mis-parsed something, which is worse than the one write this prevents.
/// </para>
/// </summary>
public sealed class PrReviewGitOutputGuardCommand : Hall9kAsyncCommand<PrReviewGitOutputGuardCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    /// <summary>Claude Code's own contract for a blocking PreToolUse hook — see <see cref="PullRequestReplyGuardCommand"/>'s identical constant.</summary>
    private const int DenyExitCode = 2;

    private static readonly HashSet<string> GuardedShellTools = new(
        ClaudeSettingsFile.GitReadOnlyGuardMatcher.Split('|'), StringComparer.OrdinalIgnoreCase);

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        string payload = await Console.In.ReadToEndAsync(cancellationToken);
        if (!Denies(payload))
        {
            return ExitCodes.Ok;
        }

        await Console.Out.WriteLineAsync(DenialJson());
        await Console.Error.WriteLineAsync(GitReadOnlyGuardRoutes.RefusalReason);
        return DenyExitCode;
    }

    /// <summary>
    /// Whether this hook payload names a shell call that writes outside the checkout through
    /// <c>git diff</c>/<c>git log</c>'s own <c>--output</c> flag. Internal so the decision is
    /// testable against real payload shapes rather than only through a process. Every parse
    /// failure answers false — see the class doc on failing open.
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

            if (root.TryGetProperty("tool_name", out JsonElement tool)
                && tool.ValueKind == JsonValueKind.String
                && !GuardedShellTools.Contains(tool.GetString() ?? string.Empty))
            {
                return false;
            }

            return root.TryGetProperty("tool_input", out JsonElement input)
                && input.ValueKind == JsonValueKind.Object
                && input.TryGetProperty("command", out JsonElement command)
                && command.ValueKind == JsonValueKind.String
                && GitReadOnlyGuardRoutes.WritesOutsideTheCheckout(command.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The structured half of the refusal — see <see cref="PullRequestReplyGuardCommand.DenialJson"/>'s identical reasoning.</summary>
    internal static string DenialJson() => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new
        {
            hookEventName = "PreToolUse",
            permissionDecision = "deny",
            permissionDecisionReason = GitReadOnlyGuardRoutes.RefusalReason,
        },
    });
}
