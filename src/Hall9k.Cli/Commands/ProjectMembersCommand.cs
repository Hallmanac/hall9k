using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Lists this project's members as the ledger's own chain read currently sees them (idea 202383dc,
/// T1) — root fingerprint, this install's own login for that root when it happens to be known
/// locally, role, the nodes currently vouched under that root, and verified state. Recomputed
/// fresh every run; nothing here is read from a local cache, so a revocation or a removal another
/// node made shows up the moment this command runs again.
/// </summary>
public sealed class ProjectMembersCommand : Hall9kAsyncCommand<ProjectMembersCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();
        return await RunAsync(session, settings, new GitLedgerChainReader(), cancellationToken);
    }

    internal static async Task<int> RunAsync(
        IQuerySession session, Settings settings, ILedgerChainReader chainReader, CancellationToken cancellationToken)
    {
        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);

        TrustChain chain;
        try
        {
            chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
        }
        // GitLedgerChainReader now throws on a genuine network or credential failure rather than
        // folding it into an empty chain (independent pre-PR review, cycle 1, adversarial lens,
        // medium) — reported here with the real cause, rather than the misleading "No verified
        // members yet" this command printed before for the identical failure.
        catch (InvalidOperationException exception)
        {
            throw new DomainValidationException(
                $"Could not read '{project.Name}'s own ledger chain: {exception.Message} Re-run "
                + $"h9k project members {project.Name} once the remote is reachable again.");
        }

        if (chain.Members.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]No verified members yet for '{project.Name.EscapeMarkup()}' — h9k project join "
                + "establishes the first one.[/]");
            WriteUnverifiedWrites(chain);
            return ExitCodes.Ok;
        }

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("Root", "Login", "Role", "Nodes", "Verified");
        foreach (ProjectMember member in chain.Members.OrderByDescending(m => m.Role == MembershipRole.Owner).ThenBy(m => m.IssuedAt))
        {
            OwnerDetails? localOwner = await session.Query<OwnerDetails>()
                .Where(owner => owner.RootFingerprint == member.RootFingerprint)
                .FirstOrDefaultAsync(cancellationToken);
            string login = localOwner is not null ? localOwner.Name.EscapeMarkup() : "[dim]unknown[/]";
            IReadOnlyList<TrustedNode> nodes = chain.OwnerChains.TryGetValue(member.RootFingerprint, out TrustedOwner? owner)
                ? owner.Nodes
                : [];
            string nodesCell = nodes.Count == 0
                ? "[dim]none vouched yet[/]"
                : string.Join("\n", nodes.Select(node => node.NodeId.EscapeMarkup()));

            table.AddRow(
                member.RootFingerprint.EscapeMarkup(),
                login,
                member.Role == MembershipRole.Owner ? "owner" : "member",
                nodesCell,
                "[green]verified[/]");
        }

        AnsiConsole.Write(table);
        WriteUnverifiedWrites(chain);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Names every vouch, revocation, or membership write the chain read found but could not
    /// verify — a stranger's forged vouch, an unsigned mutation, a member-role root trying to write
    /// a membership — so the writer is named here rather than vanishing without a trace (idea
    /// 202383dc, T1 criterion 3: "an unverifiable writer's files and envelopes are ignored and the
    /// writer is named"; independent pre-PR review, cycle 1, conformance lens, medium). Silent when
    /// there is nothing to say, the same "a quiet pane says nothing" posture <c>StatusCommand</c>'s
    /// own panes already follow.
    /// </summary>
    private static void WriteUnverifiedWrites(TrustChain chain)
    {
        if (chain.UnverifiedWrites.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine("\n[yellow]Unverifiable writes ignored:[/]");
        foreach (UnverifiedLedgerWrite write in chain.UnverifiedWrites)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  {write.Kind}[/] {write.Identifier.EscapeMarkup()} [dim](under {write.RootFingerprint.EscapeMarkup()}): "
                + $"{write.Reason.EscapeMarkup()}[/]");
        }
    }
}
