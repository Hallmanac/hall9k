using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;

namespace Hall9k.Cli.Commands;

/// <summary>A member of a solution this node minted a member-of-project invite for.</summary>
internal sealed record SolutionMintedMember(SolutionMember Member, ProjectInviteCommand.MintedInvite Invite);

/// <summary>A member of a solution this node did not mint for, and the refusal's own reason.</summary>
internal sealed record SolutionMintRefusal(SolutionMember Member, string Reason);

internal sealed record SolutionMintResult(IReadOnlyList<SolutionMintedMember> Minted, IReadOnlyList<SolutionMintRefusal> Refused)
{
    /// <summary>The bundle of what was minted, for the solution named <paramref name="membership"/>.</summary>
    public SolutionInviteBundle ToBundle(SolutionMembership membership) =>
        new(membership.Key, membership.Name, [.. Minted.Select(minted => new SolutionInviteMember(minted.Member, minted.Invite.Secret))]);
}

/// <summary>
/// The loop <c>h9k solution invite</c> runs: one member-of-project invite for each member of a solution
/// this node holds, through whatever <c>mint</c> it is given (the real command's own mint, in
/// production). A member this node does not hold, or the mint refuses for any reason, is listed with
/// that reason and left out, so the others still get theirs.
/// </summary>
internal static class SolutionInviteMinter
{
    public static async Task<SolutionMintResult> MintAsync(
        SolutionMembership membership,
        IReadOnlyList<ProjectDetails> projects,
        Func<Guid, string?> rootFingerprintOf,
        Func<ProjectDetails, CancellationToken, Task<ProjectInviteCommand.MintedInvite>> mint,
        CancellationToken cancellationToken)
    {
        List<SolutionMintedMember> minted = [];
        List<SolutionMintRefusal> refused = [];
        foreach (SolutionMember member in membership.Members)
        {
            if (SolutionDecider.HeldProject(member, projects, rootFingerprintOf) is not { } held)
            {
                refused.Add(new SolutionMintRefusal(
                    member,
                    "it is not registered and joined on this node (h9k project add --repo-url <url>, then h9k project join <project>)"));
                continue;
            }

            try
            {
                minted.Add(new SolutionMintedMember(member, await mint(held, cancellationToken)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                refused.Add(new SolutionMintRefusal(member, exception.Message));
            }
        }

        return new SolutionMintResult(minted, refused);
    }
}
