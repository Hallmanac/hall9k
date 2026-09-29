using Hall9k.Connectors.Text;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// How text another node replicated here reaches a prompt (security review idea 6be68ee2,
/// prompt-builders findings 1 to 6). A retry reason, a handback reason, a park resolution, a
/// handoff note and a blocker's closeout summary are each free text a teammate's node can write
/// into this project's stream, and before this they landed in a prompt under the local operator's
/// own heading: "a human gave this instruction". They now render as a note from the node that sent
/// them, fenced as data, unless that node is in the local owner's fleet.
/// <para>
/// The test is the verified SENDER (<see cref="Hall9k.Domain.Features.Replication.ReplicatedSender"/>,
/// the header this node's own inbox stamped), never a field of the payload: a payload author id is a
/// value the sender chose to write. Null is native text and is always local. A node id is local
/// when the fleet contains it, and foreign otherwise, including when the fleet is not known.
/// </para>
/// </summary>
public static class ReplicatedNote
{
    /// <summary>The package this file's prose ships under; shared by every builder that quotes a note.</summary>
    public const string TemplateFile = $"{WorkPromptBuilder.TemplateDirectory}/replicated-note.md";

    /// <summary>
    /// How much of a foreign reason or verdict rides into a prompt, matching what every other
    /// free-text field a human types in this platform is bounded to
    /// (<c>AgentPromptBuilder</c>'s ruling-reason bound). Not applied to a handoff note or a
    /// blocker's closeout summary, whose whole value is their length.
    /// </summary>
    public const int MaxReasonLength = 500;

    /// <summary>
    /// Whether text with this verified sender is another node's rather than the local owner's.
    /// Native text (no sender) is always the local owner's. A replicated note is local only when the
    /// node that delivered it is in the fleet and, where the sender says it began on a different
    /// node, that node is too: a catch-up answer serves replicated events as well as native ones, so
    /// a teammate's note relayed by one of your own nodes must not read as your fleet's. An origin
    /// that is absent is taken to be the sender itself, which is what a direct delivery is and what
    /// a row written before the origin was recorded can only mean until the backfill replays it.
    /// </summary>
    public static bool IsForeign(Guid? senderNodeId, Guid? originNodeId, LocalFleet? localFleet) =>
        senderNodeId is { } sender
        && (localFleet is null
            || !localFleet.NodeIds.Contains(sender)
            || (originNodeId is { } origin && origin != sender && !localFleet.NodeIds.Contains(origin)));

    /// <summary>
    /// Whether rendering this task's retry, handback or handoff text needs to know the fleet at
    /// all. False for every native field, which is what lets a solo project skip the ledger read.
    /// </summary>
    public static bool CarriesSender(TaskDetails task) =>
        task.RetryReceivedFromNodeId is not null || task.HandoffNoteReceivedFromNodeId is not null;

    /// <summary>
    /// The words that name where a note came from, for the prompt: "a note from &lt;owner&gt;
    /// (node &lt;short id&gt;)", where the owner is the node's own from the chain (the GitHub
    /// account its fleet declared, else its short root fingerprint), or "an owner not verified"
    /// when the chain cannot place the node. A sender that was never recorded
    /// (<see cref="Guid.Empty"/>) names no node at all and says so. The label names the SENDER, the
    /// one node this node authenticated, except when one of the local owner's own nodes relayed a
    /// note that began on a node outside the fleet: the sender is then a trusted relay, and naming it
    /// would put a teammate's words under the local owner's own name, so the label names the node the
    /// note claims to have begun on and says which of the fleet's nodes relayed it.
    /// </summary>
    public static string Origin(Guid senderNodeId, Guid? originNodeId, LocalFleet? localFleet)
    {
        if (senderNodeId == Guid.Empty)
        {
            return PromptTemplates.Load(TemplateFile, "origin-unidentified");
        }

        if (originNodeId is { } origin
            && origin != Guid.Empty
            && origin != senderNodeId
            && localFleet is { } fleet
            && fleet.NodeIds.Contains(senderNodeId)
            && !fleet.NodeIds.Contains(origin))
        {
            return PromptTemplates.Load(TemplateFile, "origin-relayed", new Dictionary<string, string>
            {
                ["Owner"] = OwnerLabel(origin, fleet.Chain),
                ["Node"] = DomainId.Short(origin),
                ["Relay"] = DomainId.Short(senderNodeId),
            });
        }

        return PromptTemplates.Load(TemplateFile, "origin", new Dictionary<string, string>
        {
            ["Owner"] = OwnerLabel(senderNodeId, localFleet?.Chain),
            ["Node"] = DomainId.Short(senderNodeId),
        });
    }

    /// <summary>
    /// The note's text as a fenced block, printable and, when <paramref name="maxLength"/> is given,
    /// cut on a text-element boundary before the fence is chosen (cutting after could sever a
    /// backtick run the fence was sized around). A cut adds a labelled line after the fence, outside
    /// the quote, so the reader knows text was dropped and how much was kept.
    /// </summary>
    public static string Block(string text, int? maxLength = null)
    {
        string printable = RelayedText.Printable(text);
        if (maxLength is not { } limit || printable.Length <= limit)
        {
            return RelayedText.Fenced(printable);
        }

        string kept = printable[..RelayedText.CutLength(printable, limit)].TrimEnd();
        string notice = PromptTemplates.Load(
            TemplateFile, "truncated", new Dictionary<string, string> { ["Limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return $"{RelayedText.Fenced(kept)}\n{notice}";
    }

    /// <summary>
    /// A needs-fixes review resolution that another owner's node replicated here, in place of the
    /// "human review verdict" heading the local operator's own resolve gets: labelled with where it
    /// came from, capped, and fenced, so a fix session weighs it against the code rather than
    /// obeying it.
    /// </summary>
    public static string ForeignReviewResolution(string humanFindings, Guid senderNodeId, Guid? originNodeId, LocalFleet? localFleet) =>
        PromptTemplates.Load(TemplateFile, "foreign-review-resolution", new Dictionary<string, string>
        {
            ["Origin"] = Origin(senderNodeId, originNodeId, localFleet),
            ["Findings"] = Block(humanFindings, MaxReasonLength),
        });

    /// <summary>
    /// A review-park resolution another owner's node replicated here, as a settled-rulings entry:
    /// labelled with where it came from, the verdict named as that note's own claim, and its reason
    /// capped and fenced. Blank reasons (a merge-ready resolution may carry none) say so.
    /// </summary>
    public static string ForeignRuling(string verdictWord, string? reason, Guid senderNodeId, Guid? originNodeId, LocalFleet? localFleet) =>
        PromptTemplates.Load(TemplateFile, reason.IsNotBlank() ? "foreign-ruling" : "foreign-ruling-no-reason", new Dictionary<string, string>
        {
            ["Origin"] = Origin(senderNodeId, originNodeId, localFleet),
            ["Verdict"] = verdictWord,
            ["Reason"] = reason.IsNotBlank() ? Block(reason, MaxReasonLength) : string.Empty,
        });

    private static string OwnerLabel(Guid senderNodeId, TrustChain? chain)
    {
        string? root = chain?.OwnerChains
            .Where(owner => owner.Value.FleetNodeIds().Contains(senderNodeId))
            .Select(owner => owner.Key)
            .FirstOrDefault();
        if (chain is null || root is null)
        {
            return PromptTemplates.Load(TemplateFile, "owner-unverified");
        }

        return chain.NewestDeclaredAccountOf(root) is { } account
            ? PromptTemplates.Load(TemplateFile, "owner-account", new Dictionary<string, string>
            {
                ["Login"] = RelayedText.OneLine(account.Login).Trim(),
            })
            : PromptTemplates.Load(TemplateFile, "owner-root", new Dictionary<string, string>
            {
                ["Root"] = root[..Math.Min(12, root.Length)],
            });
    }
}
