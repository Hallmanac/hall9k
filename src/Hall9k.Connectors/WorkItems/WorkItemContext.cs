using System.Text;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// The agent context an import writes: a provenance header the agent can trust, then the item's
/// body verbatim.
/// <para>
/// The header exists because the body alone is a lie of omission. An agent reading an issue
/// description has no way to know it is reading a one-time snapshot rather than the live item,
/// and would reasonably act on "the issue says it is open" months after someone closed it. So
/// the state is stamped with the moment it was read and the text says outright that nothing
/// refreshes it (AGENTS.md, never guess at unobserved facts). Import is a snapshot; mirroring
/// is deliberately not built.
/// </para>
/// </summary>
public static class WorkItemContext
{
    /// <summary>
    /// The imported item as agent context, with <paramref name="additionalContext"/> — anything
    /// the human passed with --context — appended after it. The human's words come last so they
    /// read as the operator's instruction on top of the source material rather than as part of it.
    /// <para>
    /// Only the text this method adds is trimmed. The body is copied in whole, trailing blank
    /// lines and all, because "verbatim" has to survive the composing step too: trailing spaces
    /// are a Markdown line break, and a body that read one way with --context and another way
    /// without it would make the agent's copy depend on how the human happened to invoke import.
    /// </para>
    /// </summary>
    public static string Compose(ImportedWorkItem item, string? additionalContext = null)
    {
        StringBuilder context = new();
        context.AppendLine($"Imported from {item.Reference}.");
        context.AppendLine(
            $"State as observed at import ({item.ObservedAt:yyyy-MM-dd HH:mm:ss}Z): {item.Status}. "
            + "Hall9k took a one-time snapshot and does not track the item afterwards, so treat "
            + "this as history rather than as the item's current state.");
        if (item.Url is { } url)
        {
            context.AppendLine(url.ToString());
        }

        context.AppendLine();
        context.Append(item.Body ?? "The item had no description when it was imported.");

        if (additionalContext.IsNotBlank())
        {
            context.AppendLine();
            context.AppendLine();
            context.Append(additionalContext.Trim());
        }

        return context.ToString();
    }
}
