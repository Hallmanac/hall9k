using System.ComponentModel;
using System.Globalization;
using System.Text;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Reaffirms a member's own membership file (idea 6be68ee2): a root-signed, content-changing
/// rewrite of <c>members/&lt;fingerprint&gt;.yaml</c> that bumps <c>issued_at</c> while keeping
/// role and root_fingerprint (and, on the genesis fingerprint's own file, project_key) exactly as
/// the chain's own current, authorized read already has them — never a rewrite this reader would
/// itself refuse. Exists for the one case a same-role re-invite cannot cover: a member file whose
/// own last write was signed by a node that is now merely vouched, never a live root key — the
/// stricter members-write rule this task ships means that write is retroactively no longer
/// authorized the moment the stricter reader lands, since authorization is judged against the
/// chain's own live state at read time, never the signer's state when the write itself landed. A
/// same-role re-invite writes byte-identical content and so makes no commit at all; this command
/// exists to force one, root-signed. Root-only, the identical gate every other members-ref write in
/// this task now applies.
/// <para>
/// The raw file at the ledger tip is never trusted for its role on its own (independent pre-PR
/// review, cycle 4, conformance and adversarial lenses, high): when <c>chain.Members</c> already
/// holds this fingerprint, the raw file's declared role must match the chain's own authorized role
/// exactly, or the command refuses rather than silently root-signing a role a live root key never
/// authorized — the exact write the stricter reader itself refuses (a compromised, merely vouched
/// node rewriting a member's role). The raw role is trusted only as a fallback, for the one case
/// commit f035ee73a introduced this raw read for: a member entirely absent from
/// <c>chain.Members</c>, such as a member whose own last write is itself unauthorized under the
/// stricter rule and so never reaches that authorized view at all.
/// </para>
/// </summary>
public sealed class ProjectMemberReaffirmCommand : Hall9kAsyncCommand<ProjectMemberReaffirmCommand.Settings>
{
    private const int MaxConflictRetries = 5;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;

        [CommandArgument(1, "<FINGERPRINT>")]
        [Description("The member's own root fingerprint to reaffirm — h9k project members prints it.")]
        public string Fingerprint { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        return await RunAsync(session, project, settings.Fingerprint, cancellationToken);
    }

    internal static Task<int> RunAsync(
        IDocumentSession session, ProjectDetails project, string fingerprint, CancellationToken cancellationToken) =>
        RunAsync(
            session, project, fingerprint, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new GitLedgerChainReader(),
            new NodeKeyStore(), cancellationToken);

    /// <summary>The whole reaffirm flow, seamed on <see cref="ILedger"/>, <see cref="ILedgerChainReader"/>,
    /// and <see cref="NodeKeyStore"/> so a test drives it against fakes (Brian's 2026-09-13 testing rule).</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, ProjectDetails project, string fingerprint, ILedger ledger, ILedgerChainReader chainReader,
        NodeKeyStore keyStore, CancellationToken cancellationToken)
    {
        if (fingerprint.IsBlank() || !NodeKeyStore.IsFingerprint(fingerprint))
        {
            throw new DomainValidationException(
                $"'{fingerprint}' is not a fingerprint — h9k project members prints the exact value to pass here.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No owner {context.OwnerId}.");
        if (owner.RootFingerprint is not { } myRoot)
        {
            throw new DomainValidationException("This node has no root fingerprint yet — run h9k project join first.");
        }

        NodeSigningKey key = await keyStore.EnsureAsync(context.NodeId, cancellationToken);

        TrustChain chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
        if (chain.RoleOf(myRoot) != MembershipRole.Owner)
        {
            throw new DomainValidationException(
                $"This node's own owner ({myRoot}) does not currently hold the owner role in "
                + $"'{project.Name}' — only an owner-role member's own node may reaffirm another member.");
        }

        // Root-only (idea 6be68ee2, trust-ledger finding 2): the identical gate every other
        // members-ref write in this task now applies — never merely enrolled, never this node's own
        // identity fingerprint (a promoted successor's key can never equal it), and never
        // TrustChain.RootNodeId (null on an older ledger).
        if (!chain.IsLiveRootKeyOfOwner(key.Fingerprint, myRoot))
        {
            throw new DomainValidationException(
                $"This node ({key.Fingerprint}) does not currently hold a live root key for owner {myRoot} — "
                + "only the root itself may reaffirm a member (idea 6be68ee2, trust-ledger finding 2). "
                + $"Re-run h9k project member reaffirm {project.Name} {fingerprint} from a node holding a "
                + $"root key for {myRoot}{RootNodeDescription.Of(chain, myRoot)}.");
        }

        LedgerCommitter committer = new(
            owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
            owner.Email.IsNotBlank() ? owner.Email : $"{context.NodeId}@hall9k.local");
        LedgerSigningKey signingKey = new(key.PrivateKeyPath);

        MembershipRole reaffirmedRole = await ReaffirmMemberAsync(
            ledger, chainReader, project.RepositoryPath, chain, fingerprint, now, committer, signingKey, cancellationToken);

        string roleLabel = reaffirmedRole == MembershipRole.Owner ? "owner" : "member";
        AnsiConsole.MarkupLine(
            $"[green]Reaffirmed[/] {roleLabel} [dim]{fingerprint}[/] in '{project.Name.EscapeMarkup()}' — "
            + "issued_at bumped by a fresh root-signed commit.");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Rebuilds <c>members/&lt;fingerprint&gt;.yaml</c> from the file's own raw content at the
    /// ledger's tip, with <c>issued_at</c> bumped to <paramref name="now"/> so the write is
    /// content-changing: a same-role re-invite would write byte-identical content and make no
    /// commit at all. <c>chain.Members</c> is this reader's own AUTHORIZED view, filtered by the
    /// identical stricter live-root-key rule this command exists to route around: the one case this
    /// command is for (idea 6be68ee2's own doc: "a member file whose own last write was signed by a
    /// node that is now merely vouched") is exactly the case the stricter reader now refuses, so a
    /// lookup keyed on <c>chain.Members</c> could never find the very member it was written to
    /// reaffirm (independent pre-PR review, cycle 1, both lenses, high/medium).
    /// <para>
    /// The raw file's role is trusted unconditionally only when <paramref name="fingerprint"/> is
    /// absent from <c>chain.Members</c> altogether — the one case above. Whenever the fingerprint
    /// already has an authorized role, the raw file's declared role must match it exactly; a
    /// mismatch means the ledger's tip holds a role change no live root key ever authorized (a
    /// compromised, merely vouched node's own rewrite), and reaffirming it would root-sign that
    /// change into legitimacy instead of merely re-landing the file unchanged (independent pre-PR
    /// review, cycle 4, conformance and adversarial lenses, high). Root re-signing this exact
    /// content, right now, is what makes a write authorized — the write itself is refused before
    /// any push unless this node's own key is a live root key (checked above in
    /// <see cref="RunAsync"/>) — but that gate gives no cover for signing a role this reader would
    /// never have accepted from anyone in the first place.
    /// </para>
    /// </summary>
    private static async Task<MembershipRole> ReaffirmMemberAsync(
        ILedger ledger, ILedgerChainReader chainReader, string repositoryPath, TrustChain chain, string fingerprint,
        DateTimeOffset now, LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        const string refName = "refs/hall9k/ledger/members";
        string path = $"members/{fingerprint}.yaml";

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            if (!current.Exists)
            {
                throw new DomainValidationException(
                    $"'{fingerprint}' is not currently a member of this project's ledger — nothing to reaffirm.");
            }

            MembershipRole rawRole = ParseRole(current.Content)
                ?? throw new DomainConflictException(
                    $"{path} does not currently declare a valid role (\"owner\" or \"member\") — nothing to reaffirm.");

            MembershipRole? acceptedRole = chain.RoleOf(fingerprint);
            if (acceptedRole is { } accepted && accepted != rawRole)
            {
                throw new DomainConflictException(
                    $"{path} currently declares role \"{RoleLabel(rawRole)}\", but the chain's own current, "
                    + $"authorized read already has '{fingerprint}' as \"{RoleLabel(accepted)}\" — this looks "
                    + "like a role change no live root key ever authorized (idea 6be68ee2, trust-ledger "
                    + "finding 2). Reaffirm never rewrites a role; re-run the intended role change itself "
                    + "(h9k project invite or h9k project member remove) from a node holding a live root key "
                    + "if the role change is genuine.");
            }

            MembershipRole role = acceptedRole ?? rawRole;

            List<(string Key, string Value)> fields =
            [
                ("root_fingerprint", fingerprint),
                ("role", RoleLabel(role)),
                ("issued_at", now.ToString("o", CultureInfo.InvariantCulture)),
            ];
            if (fingerprint == chain.GenesisRootFingerprint && chain.ProjectKey is { } projectKey)
            {
                fields.Add(("project_key", projectKey));
            }

            string content = BuildYaml([.. fields]);
            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, refName, path, content, current.BlobId,
                    $"Reaffirm member {fingerprint} (idea 6be68ee2)", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return role;
            }

            // Someone else's write landed between our read and ours — re-derive the genesis
            // fingerprint's own project_key (the one field this command still reads from the
            // authorized chain rather than the raw file) from a fresh chain for the next attempt,
            // never the losing attempt's stale copy (the identical conflict handling
            // WriteProjectKeyAsync applies). The role and root_fingerprint fields are re-read
            // straight from the ledger at the top of the loop regardless.
            chain = await chainReader.ComputeAsync(repositoryPath, cancellationToken);
        }

        throw new DomainConflictException(
            $"{path} kept changing out from under this reaffirm after {MaxConflictRetries} attempts — "
            + "something else is writing it at the same time. Re-run h9k project member reaffirm once that settles.");
    }

    private static string RoleLabel(MembershipRole role) => role == MembershipRole.Owner ? "owner" : "member";

    private static string BuildYaml(params (string Key, string Value)[] fields)
    {
        StringBuilder builder = new();
        foreach ((string key, string value) in fields)
        {
            builder.Append(key).Append(": \"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).AppendLine("\"");
        }

        return builder.ToString();
    }

    /// <summary>Null for anything other than exactly "owner" or "member" — the identical closed-pair
    /// read <c>GitLedgerChainReader.ParseRole</c> applies, duplicated here for the same reason
    /// <c>GitLedgerChainReader.ExtractQuotedYamlValue</c>'s own doc gives: no assembly boundary lets
    /// this command reach that reader's internal parser directly.</summary>
    private static MembershipRole? ParseRole(string? content)
    {
        string? raw = content is null ? null : ExtractQuotedYamlValue(content, "role");
        return raw switch
        {
            _ when string.Equals(raw, "owner", StringComparison.OrdinalIgnoreCase) => MembershipRole.Owner,
            _ when string.Equals(raw, "member", StringComparison.OrdinalIgnoreCase) => MembershipRole.Member,
            _ => null,
        };
    }

    /// <summary>Reverses the small, flat, every-value-double-quoted YAML shape every ledger file in
    /// this task uses — the same reader <c>GitLedgerChainReader.ExtractQuotedYamlValue</c> already
    /// is, duplicated for the identical reason that class' own doc gives for its own duplication of
    /// <c>GitLedgerMessageTransport.ExtractQuotedYamlValue</c>.</summary>
    private static string? ExtractQuotedYamlValue(string yaml, string key)
    {
        foreach (string rawLine in yaml.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string prefix = $"{key}: \"";
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith('"'))
            {
                continue;
            }

            string inner = line[prefix.Length..^1];
            return inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        return null;
    }
}
