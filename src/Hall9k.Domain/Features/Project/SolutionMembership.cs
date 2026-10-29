namespace Hall9k.Domain.Features.Project;

/// <summary>
/// The solution a project belongs to, carried as a team setting on every member project: there is
/// no separate Solution stream, the membership on the members is the solution. It names the
/// solution (<see cref="Key"/>, a 26-character ULID minted once, and <see cref="Name"/>), its
/// <see cref="State"/> with the <see cref="CloseReason"/> a closed one carries, and every member
/// including the project it sits on, so a node that receives the membership for any one member
/// knows the whole solution from it. Replace-the-whole-record semantics, the
/// <see cref="ContextLink"/> idiom: every change rewrites the record on every member.
/// </summary>
public sealed record SolutionMembership(
    string Key,
    string Name,
    SolutionState State,
    string? CloseReason,
    IReadOnlyList<SolutionMember> Members)
{
    /// <summary>Whether this membership holds its project to this one solution (an open one does, a closed one does not).</summary>
    public bool Holds => State == SolutionState.Open;
}
