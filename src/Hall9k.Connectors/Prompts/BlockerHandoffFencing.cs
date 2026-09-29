using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// What a dependent's prompt is allowed to inherit from a blocker's handoff (security review idea
/// 6be68ee2, prompt-builders findings 1 to 6). <see cref="BlockerContextDocument"/> lives in the
/// Domain and cannot reach the fence, so each surface that renders it (the daemon's
/// <c>BlockerContextAssembler</c>, <c>h9k task work</c>, and <c>h9k task show</c>'s starting
/// context) runs the handoffs through here first, which keeps the prompt and the screen identical.
/// <para>
/// Two things change, and only two. A blocker's objective is one-lined unconditionally: it lands in a
/// Markdown heading (<c>### 1. ...</c>), and a newline in it would open a second, unauthored heading
/// underneath the real one. A summary whose verified sender is outside the local owner's fleet is
/// labelled as a note from that sender and fenced, with no length cap, since the whole value of a
/// handoff is its length. A blocker's acceptance criteria are its task's own instruction and are left
/// alone.
/// </para>
/// </summary>
public static class BlockerHandoffFencing
{
    /// <summary>
    /// The handoffs as a prompt should render them. <paramref name="readFleet"/> is called at most
    /// once, and only when a summary here carries a replicated sender, so a solo project's blockers
    /// never cost a ledger read; a null fleet fences every replicated summary.
    /// </summary>
    public static async ValueTask<IReadOnlyList<BlockerHandoff>> ApplyAsync(
        IReadOnlyList<BlockerHandoff> blockers,
        Func<CancellationToken, Task<LocalFleet?>> readFleet,
        CancellationToken cancellationToken)
    {
        LocalFleet? localFleet = blockers.Any(blocker => blocker.HasSummary && blocker.SummaryReceivedFromNodeId is not null)
            ? await readFleet(cancellationToken)
            : null;

        return [.. blockers.Select(blocker => blocker with
        {
            Objective = RelayedText.OneLine(blocker.Objective).Trim(),
            Summary = FencedSummary(blocker, localFleet),
        })];
    }

    private static string? FencedSummary(BlockerHandoff blocker, LocalFleet? localFleet) =>
        blocker.HasSummary
        && blocker.Summary is { } summary
        && blocker.SummaryReceivedFromNodeId is { } sender
        && ReplicatedNote.IsForeign(sender, blocker.SummaryOriginNodeId, localFleet)
            ? PromptTemplates.Load(ReplicatedNote.TemplateFile, "foreign-blocker-summary", new Dictionary<string, string>
            {
                ["Origin"] = ReplicatedNote.Origin(sender, localFleet),
                ["Summary"] = ReplicatedNote.Block(summary),
            })
            : blocker.Summary;
}
