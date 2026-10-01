namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// One fact about whose root a task belongs to, as a reading node knows it: the task carries no
/// such fact at all, carries one this node cannot turn into a root fingerprint, or carries one it
/// can. The three are never collapsed, because "no assignment" and "an assignment recorded by an
/// owner this node has never heard of" lead to opposite answers in <see cref="TaskOwnerRule"/>.
/// </summary>
public sealed record OwnerRootFact
{
    public static readonly OwnerRootFact Absent = new(OwnerRootFactState.Absent, null);

    public static readonly OwnerRootFact Unresolved = new(OwnerRootFactState.Unresolved, null);

    private OwnerRootFact(OwnerRootFactState state, string? rootFingerprint)
    {
        State = state;
        RootFingerprint = rootFingerprint;
    }

    public OwnerRootFactState State { get; }

    /// <summary>The root fingerprint, set only when <see cref="State"/> is <see cref="OwnerRootFactState.Known"/>.</summary>
    public string? RootFingerprint { get; }

    public static OwnerRootFact Known(string rootFingerprint) => new(OwnerRootFactState.Known, rootFingerprint);

    /// <summary>A fingerprint that may be null or empty on the task: known when there is one, absent otherwise.</summary>
    public static OwnerRootFact KnownOrAbsent(string? rootFingerprint) =>
        string.IsNullOrEmpty(rootFingerprint)
            ? Absent
            : Known(rootFingerprint);
}

/// <summary>An unpersisted, in-process answer, so an enum rather than a closed vocabulary.</summary>
public enum OwnerRootFactState
{
    Absent,
    Unresolved,
    Known,
}
