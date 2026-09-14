using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Message;

/// <summary>
/// Who an envelope is addressed to (idea 202383dc, M1a): the whole project, one owner by root
/// fingerprint, or one node by id. A closed shape of three, not a closed vocabulary of flat values
/// (<c>MessageKind</c>'s own discipline) — <see cref="Parse"/> throws on anything else, unlike an
/// unrecognized <see cref="MessageKind"/>, because a malformed "to" field is not a payload a future
/// version might learn to read; it is an envelope this reader cannot route at all.
/// </summary>
public sealed record MessageAudience
{
    private const string OwnerPrefix = "owner:";
    private const string NodePrefix = "node:";

    /// <summary>Every member of the project — team-visible, not scoped to one owner or one node.</summary>
    public static readonly MessageAudience Project = new("project");

    public string Value { get; }

    private MessageAudience(string value) => Value = value;

    public static MessageAudience Owner(string ownerFingerprint) =>
        ownerFingerprint.IsBlank()
            ? throw new DomainValidationException("A message addressed to an owner needs that owner's fingerprint.")
            : new MessageAudience(OwnerPrefix + ownerFingerprint);

    public static MessageAudience Node(Guid nodeId) => new(NodePrefix + nodeId);

    public static MessageAudience Parse(string raw) => raw switch
    {
        "project" => Project,
        _ when raw.StartsWith(OwnerPrefix, StringComparison.Ordinal) && raw.Length > OwnerPrefix.Length =>
            new MessageAudience(raw),
        _ when raw.StartsWith(NodePrefix, StringComparison.Ordinal) && Guid.TryParse(raw[NodePrefix.Length..], out _) =>
            new MessageAudience(raw),
        _ => throw new DomainValidationException(
            $"'{raw}' is not a message audience Hall9k recognizes (expected 'project', "
            + "'owner:<fingerprint>', or 'node:<id>')."),
    };

    /// <summary>Whether an envelope carrying this audience is addressed to the reading node itself,
    /// its own claimed owner, or the project as a whole (idea 202383dc: "inbox as a filter").</summary>
    public bool Matches(Guid myNodeId, string myOwnerFingerprint) => Value switch
    {
        "project" => true,
        _ when Value.StartsWith(OwnerPrefix, StringComparison.Ordinal) =>
            string.Equals(Value[OwnerPrefix.Length..], myOwnerFingerprint, StringComparison.OrdinalIgnoreCase),
        _ when Value.StartsWith(NodePrefix, StringComparison.Ordinal) =>
            Guid.TryParse(Value[NodePrefix.Length..], out Guid nodeId) && nodeId == myNodeId,
        _ => false,
    };

    public override string ToString() => Value;
}
