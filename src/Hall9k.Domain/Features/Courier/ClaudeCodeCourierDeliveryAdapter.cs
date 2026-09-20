using Hall9k.Domain.Features.Orchestrator;

namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// Delivery through Claude Code's own cross-session mesh (idea 89471598, piece 3) — the
/// <c>ListAgents</c>/<c>SendMessage</c> tools every headless session carries regardless of
/// <c>--strict-mcp-config</c>, since they are Claude Code's own built-in capability rather than
/// an MCP server. The same mechanism <c>WorkPromptBuilder.AppendOutboundMilestoneRules</c> already
/// addresses a human's registered interactive session through.
/// </summary>
public sealed class ClaudeCodeCourierDeliveryAdapter : ICourierDeliveryAdapter
{
    public string Cli => LaunchText.DefaultCli;

    public string BuildDeliveryInstruction(string sessionName) =>
        $"Use the SendMessage tool addressed to `{sessionName}` — the orchestrator's own registered "
        + "session, reached through the cross-session mesh — with the message above as the `message` "
        + "argument, verbatim.";
}

/// <summary>Every delivery adapter this build ships, looked up by the presence record's own <c>Cli</c> field.</summary>
public static class CourierDeliveryAdapterRegistry
{
    private static readonly IReadOnlyList<ICourierDeliveryAdapter> Adapters = [new ClaudeCodeCourierDeliveryAdapter()];

    /// <summary>
    /// The adapter for <paramref name="cli"/>, or null when nothing fits — the daemon's own spawn
    /// gate leaves the feed undrained and logs it rather than guessing at a mechanism no adapter
    /// here claims (idea 89471598, piece 3's own acceptance criteria).
    /// </summary>
    public static ICourierDeliveryAdapter? ForCli(string cli) =>
        Adapters.FirstOrDefault(adapter => string.Equals(adapter.Cli, cli, StringComparison.OrdinalIgnoreCase));
}
