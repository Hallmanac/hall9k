using System.ComponentModel;
using System.Globalization;
using System.Text;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The one-time backfill for a project whose ledger predates the project key (idea 202383dc, M2,
/// Brian's ruling 2026-09-17): a ledger genesis written before this piece shipped never minted a
/// <c>project_key</c>, so nothing derives one from replaying it. Writes it as a signed commit onto
/// the genesis fingerprint's own <c>members/&lt;fingerprint&gt;.yaml</c> — the identical file
/// <c>h9k project join</c> writes it into fresh at genesis — refused for anyone but the genesis
/// owner, and refused a second time once a key already exists: this is a single, load-bearing fact
/// about the ledger, never a value to overwrite.
/// </summary>
public sealed class ProjectAssignKeyCommand : Hall9kAsyncCommand<ProjectAssignKeyCommand.Settings>
{
    private const int MaxConflictRetries = 5;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        return await RunAsync(session, project, cancellationToken);
    }

    internal static Task<int> RunAsync(
        IDocumentSession session, ProjectDetails project, CancellationToken cancellationToken) =>
        RunAsync(
            session, project, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new GitLedgerChainReader(),
            new NodeKeyStore(), cancellationToken);

    /// <summary>The whole assign flow, seamed on <see cref="ILedger"/>, <see cref="ILedgerChainReader"/>,
    /// and <see cref="NodeKeyStore"/> so a test drives it against fakes (Brian's 2026-09-13 testing rule).</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, ProjectDetails project, ILedger ledger, ILedgerChainReader chainReader,
        NodeKeyStore keyStore, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No owner {context.OwnerId}.");
        if (owner.RootFingerprint is not { } myRoot)
        {
            throw new DomainValidationException("This node has no root fingerprint yet — run h9k project join first.");
        }

        TrustChain chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
        if (chain.GenesisRootFingerprint is not { } genesisRoot)
        {
            throw new DomainValidationException(
                $"'{project.Name}' has no genesis recorded yet — h9k project join {project.Name} is the step "
                + "that establishes genesis (and mints the project's own key at the same time on a build "
                + "that ships this piece), not h9k project assign-key.");
        }

        if (genesisRoot != myRoot)
        {
            throw new DomainValidationException(
                $"Only '{project.Name}'s own genesis owner ({genesisRoot}) may assign its key — this node's "
                + $"own root ({myRoot}) is not it.");
        }

        if (chain.ProjectKey is not null)
        {
            throw new DomainValidationException(
                $"'{project.Name}' already has a key ({chain.ProjectKey}) — h9k project assign-key is a "
                + "one-time backfill for a ledger whose genesis predates this piece, refused once a key "
                + "already exists.");
        }

        NodeSigningKey key = await keyStore.EnsureAsync(context.NodeId, cancellationToken);
        LedgerCommitter committer = new(
            owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
            owner.Email.IsNotBlank() ? owner.Email : $"{context.NodeId}@hall9k.local");
        LedgerSigningKey signingKey = new(key.PrivateKeyPath);

        string mintedKey = Ulid.NewUlid().ToString();
        string writtenKey = await WriteProjectKeyAsync(
            ledger, chainReader, project.RepositoryPath, chain, genesisRoot, mintedKey, committer, signingKey, cancellationToken);

        session.Events.Append(project.Id, ProjectDecider.AssignKey(project.Id, writtenKey, now));
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine($"[green]Assigned[/] '{project.Name.EscapeMarkup()}'s own key: [bold]{writtenKey}[/]");
        AnsiConsole.MarkupLine(
            "[dim]One-time backfill for a ledger whose genesis predates this piece — every other install "
            + "picks it up the next time it runs h9k project join there.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Rebuilds the genesis fingerprint's own <c>members/&lt;fingerprint&gt;.yaml</c> from the facts
    /// the authorized chain replay itself vouches for — <paramref name="chain"/>'s own
    /// <see cref="ProjectMember"/> entry for <paramref name="genesisFingerprint"/> — plus the freshly
    /// minted <paramref name="projectKey"/>, rather than from the ref's raw tip content: that raw
    /// content is never authorized the way <see cref="ILedgerChainReader"/>'s own replay is, so any
    /// field an unauthorized push previously planted on this exact file (a rewritten <c>role</c> or
    /// <c>issued_at</c>, not only <c>project_key</c>) would otherwise be signed and re-pushed by the
    /// genesis owner themselves, laundering a write the chain replay refuses into one it accepts
    /// (independent pre-PR review, cycle 3, both lenses, high) — the identical "content depends only
    /// on authorized facts, never on what is currently on disk" discipline
    /// <c>ProjectJoinCommand.WriteNodeFileAsync</c>'s own idiom already applies. Retried against a
    /// fresh read the same way every other conflict-prone ledger write in this codebase is: on a
    /// conflict, this recomputes the chain both to ask whether a concurrent, genuinely authorized
    /// <c>h9k project assign-key</c> run already won the race and landed its own key — if so, that
    /// key is returned rather than fought over — and to re-derive the genesis member's own
    /// authorized facts for the next attempt's content, never the losing attempt's stale copy.
    /// </summary>
    private static async Task<string> WriteProjectKeyAsync(
        ILedger ledger, ILedgerChainReader chainReader, string repositoryPath, TrustChain chain,
        string genesisFingerprint, string projectKey, LedgerCommitter committer, LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        const string refName = "refs/hall9k/ledger/members";
        string path = $"members/{genesisFingerprint}.yaml";

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            ProjectMember genesisMember = chain.Members.FirstOrDefault(member => member.RootFingerprint == genesisFingerprint)
                ?? throw new DomainConflictException(
                    $"{genesisFingerprint} is no longer a recorded member of this project's ledger — "
                    + "re-run h9k project assign-key once membership settles.");

            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            if (!current.Exists)
            {
                throw new DomainConflictException(
                    $"{path} no longer exists — the genesis member was removed since this command started. "
                    + "Re-run h9k project assign-key once membership settles.");
            }

            string content = BuildYaml(
                ("root_fingerprint", genesisMember.RootFingerprint),
                ("role", genesisMember.Role == MembershipRole.Owner ? "owner" : "member"),
                ("issued_at", genesisMember.IssuedAt.ToString("o", CultureInfo.InvariantCulture)),
                ("project_key", projectKey));
            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, refName, path, content, current.BlobId,
                    "Assign this project's own key (idea 202383dc, M2 backfill)", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return projectKey;
            }

            // Someone else's write landed between our read and ours — ask the authorized chain,
            // never the raw content we would otherwise reread next iteration, whether that write
            // was a genuinely authorized assign-key racing this one. If so, its key is the ledger's
            // real answer and this attempt backs off rather than appending a second, competing line.
            chain = await chainReader.ComputeAsync(repositoryPath, cancellationToken);
            if (chain.ProjectKey is { } authorizedKey)
            {
                return authorizedKey;
            }
        }

        throw new DomainConflictException(
            $"{path} kept changing out from under this assign after {MaxConflictRetries} attempts — "
            + "something else is writing it at the same time. Re-run h9k project assign-key once that settles.");
    }

    /// <summary>Builds the small, flat, every-value-double-quoted YAML shape every ledger file in
    /// this feature uses — the same shape reader <c>GitLedgerChainReader.ExtractQuotedYamlValue</c>
    /// parses, duplicated for the same reason that class's own doc comment gives for its own
    /// duplication of <c>GitLedgerMessageTransport</c>'s reader, and the identical shape
    /// <c>ProjectJoinCommand</c>'s own <c>BuildYaml</c>/<c>QuoteYaml</c> write at genesis.</summary>
    private static string BuildYaml(params (string Key, string Value)[] fields)
    {
        StringBuilder builder = new();
        foreach ((string key, string value) in fields)
        {
            builder.Append(key).Append(": ").AppendLine(QuoteYaml(value));
        }

        return builder.ToString();
    }

    private static string QuoteYaml(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
