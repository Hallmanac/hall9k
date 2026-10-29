using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Cli.Commands;

/// <summary>One member of a solution invite bundle: who it is, the member-of-project invite secret that joins it, and the base branch its project is registered on at the minting node (null when the minting project records none), so a joining node registers it on the same branch.</summary>
internal sealed record SolutionInviteMember(SolutionMember Member, string Secret, string? BaseBranch = null);

/// <summary>
/// What <c>h9k solution invite</c> prints and <c>h9k solution join</c> reads: the solution's key and
/// name and, for each member the minting node could mint for, its project key, name, repository URL,
/// and secret. The member list travels in the bundle because a joining node has no membership record
/// until the daemon's catch-up delivers a member's team settings, so the bundle is all it has to go on.
/// It holds live secrets, so it is a bearer token to handle like any one of them.
/// <para>
/// One line of base64url JSON behind a versioned prefix, so it survives a copy and paste, a chat
/// message, and <c>--invite -</c> (which reads one line of stdin), and so a future shape can be told
/// from this one.
/// </para>
/// </summary>
internal sealed record SolutionInviteBundle(string SolutionKey, string SolutionName, IReadOnlyList<SolutionInviteMember> Members)
{
    private const string Prefix = "h9k-solution-invite-v1.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Encode()
    {
        Wire wire = new(
            SolutionKey,
            SolutionName,
            [.. Members.Select(entry => new WireMember(
                entry.Member.ProjectKey, entry.Member.Name, entry.Member.RepositoryUrl.ToString(), entry.Secret, entry.BaseBranch))]);
        return Prefix + Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(wire, Json));
    }

    /// <summary>
    /// The bundle <paramref name="text"/> carries, or a refusal that says what is wrong with it. Every
    /// member is validated the way a solution's members are (ULID keys, a name and an absolute
    /// repository URL each, none twice) and must carry a secret.
    /// </summary>
    public static SolutionInviteBundle Parse(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new DomainValidationException(
                "That is not a solution invite bundle: it should be the single line h9k solution invite printed, "
                + $"beginning '{Prefix}'. A single project's secret goes to h9k project join <project> --invite instead.");
        }

        Wire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(Base64Url.DecodeFromChars(trimmed.AsSpan(Prefix.Length)), Json);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw new DomainValidationException(
                "The solution invite bundle is damaged or cut short: copy the whole line h9k solution invite "
                + "printed, or read it with --invite - and paste it on stdin.");
        }

        if (wire is not { Members: { } wireMembers })
        {
            throw new DomainValidationException("The solution invite bundle names no members.");
        }

        List<SolutionInviteMember> members = [];
        foreach (WireMember wireMember in wireMembers)
        {
            if (!Uri.TryCreate(wireMember.RepositoryUrl, UriKind.Absolute, out Uri? repositoryUrl))
            {
                throw new DomainValidationException(
                    $"The solution invite bundle's member '{wireMember.Name}' has no absolute repository URL.");
            }

            if (string.IsNullOrWhiteSpace(wireMember.Secret))
            {
                throw new DomainValidationException(
                    $"The solution invite bundle's member '{wireMember.Name}' carries no secret.");
            }

            string? baseBranch = string.IsNullOrWhiteSpace(wireMember.BaseBranch) ? null : wireMember.BaseBranch.Trim();
            if (baseBranch is not null && !GitArgumentValidation.IsLegalBranchName(baseBranch, out string? refusalReason))
            {
                throw new DomainValidationException(
                    $"The solution invite bundle's member '{wireMember.Name}' names a base branch git would not take as a branch name: {refusalReason}");
            }

            members.Add(new SolutionInviteMember(
                new SolutionMember(wireMember.ProjectKey ?? string.Empty, wireMember.Name ?? string.Empty, repositoryUrl),
                wireMember.Secret.Trim(),
                baseBranch));
        }

        SolutionInviteBundle bundle = new(wire.SolutionKey ?? string.Empty, wire.SolutionName ?? string.Empty, members);
        SolutionDecider.Validate(new SolutionMembership(
            bundle.SolutionKey, bundle.SolutionName, SolutionState.Open, CloseReason: null,
            [.. members.Select(entry => entry.Member)]));
        return bundle;
    }

    private sealed record Wire(string? SolutionKey, string? SolutionName, WireMember[]? Members);

    private sealed record WireMember(string? ProjectKey, string? Name, string? RepositoryUrl, string? Secret, string? BaseBranch = null);
}
