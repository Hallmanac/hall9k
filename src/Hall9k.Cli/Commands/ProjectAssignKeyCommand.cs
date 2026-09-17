using System.ComponentModel;
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

        string projectKey = Ulid.NewUlid().ToString();
        await WriteProjectKeyAsync(
            ledger, project.RepositoryPath, genesisRoot, projectKey, committer, signingKey, cancellationToken);

        session.Events.Append(project.Id, ProjectDecider.AssignKey(project.Id, projectKey, now));
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine($"[green]Assigned[/] '{project.Name.EscapeMarkup()}'s own key: [bold]{projectKey}[/]");
        AnsiConsole.MarkupLine(
            "[dim]One-time backfill for a ledger whose genesis predates this piece — every other install "
            + "picks it up the next time it runs h9k project join there.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Adds a <c>project_key</c> line to the genesis fingerprint's own, already-existing
    /// <c>members/&lt;fingerprint&gt;.yaml</c> content — never replaces the file, so the
    /// <c>root_fingerprint</c>, <c>role</c>, and <c>issued_at</c> fields genesis itself recorded
    /// survive unchanged. Retried against a fresh read the same way every other conflict-prone
    /// ledger write in this codebase is (<c>ProjectJoinCommand.WriteNodeFileAsync</c>'s own idiom):
    /// content depends only on this call's own arguments plus whatever the current read returns, so
    /// a conflict is always safe to retry.
    /// </summary>
    private static async Task WriteProjectKeyAsync(
        ILedger ledger, string repositoryPath, string genesisFingerprint, string projectKey,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        const string refName = "refs/hall9k/ledger/members";
        string path = $"members/{genesisFingerprint}.yaml";

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            if (!current.Exists || current.Content is not { } currentContent)
            {
                throw new DomainConflictException(
                    $"{path} no longer exists — the genesis member was removed since this command started. "
                    + "Re-run h9k project assign-key once membership settles.");
            }

            if (ExtractYamlValue(currentContent, "project_key") is not null)
            {
                // Another writer's own assign-key won the race between the refusal check above and
                // this write — the key exists either way, whichever one actually landed.
                return;
            }

            string content = currentContent.TrimEnd('\n') + "\n" + $"project_key: \"{projectKey}\"\n";
            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, refName, path, content, current.BlobId,
                    "Assign this project's own key (idea 202383dc, M2 backfill)", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return;
            }
        }

        throw new DomainConflictException(
            $"{path} kept changing out from under this assign after {MaxConflictRetries} attempts — "
            + "something else is writing it at the same time. Re-run h9k project assign-key once that settles.");
    }

    /// <summary>Reverses the small, flat, every-value-double-quoted YAML shape every ledger file in
    /// this feature uses — the same reader <c>GitLedgerChainReader.ExtractQuotedYamlValue</c>
    /// already is, duplicated for the same reason that class's own doc comment gives for its own
    /// duplication of <c>GitLedgerMessageTransport</c>'s reader.</summary>
    private static string? ExtractYamlValue(string yaml, string key)
    {
        foreach (string rawLine in yaml.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string prefix = $"{key}: \"";
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith('"'))
            {
                continue;
            }

            return line[prefix.Length..^1];
        }

        return null;
    }
}
