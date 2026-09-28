using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Lists this project's members as the ledger's own chain read currently sees them (idea 202383dc,
/// T1) — root fingerprint, the GitHub accounts that root's nodes declare for themselves in their own
/// signed node files, role, that root's own fleet (<see cref="TrustedOwner.FleetNodeIds"/>: its own
/// root node plus every currently vouched node), and how each declared account stands against the
/// repository's collaborator roster. A declaration is a claim, so the column says "declared" and
/// what the roster showed, never "verified". The roster is re-read through GitHub first when gh
/// answers and taken from the stored mirror, dated, when it cannot. The chain is recomputed fresh
/// every run, so a revocation or a removal another node made shows up the moment this runs again.
/// The Root cell also carries the newest display name declared across that root's own nodes (task
/// e6744304), dimmed on its own line beneath the fingerprint, when one is declared: a label only,
/// never part of any trust or cross-check decision, and never a new column, since adding one would
/// break the Login and Verified cells' own line-for-line alignment for nothing this table needs.
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
        // A writing session: refreshing the collaborator roster appends the observation to the project's stream.
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, new GitLedgerChainReader(), new ProjectGitHubRosterReader(), cancellationToken);
    }

    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, ILedgerChainReader chainReader, ProjectGitHubRosterReader rosterReader,
        CancellationToken cancellationToken)
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

        ProjectGitHubRoster roster = await rosterReader.ReadAsync(session, project, DateTimeOffset.UtcNow, cancellationToken);

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("Root", "Login", "Role", "Nodes", "Verified");
        foreach (ProjectMember member in chain.Members.OrderByDescending(m => m.Role == MembershipRole.Owner).ThenBy(m => m.IssuedAt))
        {
            IReadOnlyList<DeclaredGitHubAccount> accounts = chain.DeclaredAccountsOf(member.RootFingerprint);
            (string loginCell, string standingCell) = RenderAccounts(accounts, roster);
            IReadOnlyList<Guid> nodes = chain.OwnerChains.TryGetValue(member.RootFingerprint, out TrustedOwner? owner)
                ? [.. owner.FleetNodeIds()]
                : [];
            string nodesCell = DescribeFleet(nodes, owner);

            table.AddRow(
                RenderRoot(member.RootFingerprint, chain.DisplayNameOf(member.RootFingerprint)),
                loginCell,
                member.Role == MembershipRole.Owner ? "owner" : "member",
                nodesCell,
                standingCell);
        }

        AnsiConsole.Write(table);
        if (RosterNote(roster) is { } note)
        {
            AnsiConsole.MarkupLine($"[dim]{note.EscapeMarkup()}[/]");
        }

        WriteUnverifiedWrites(chain);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The Nodes cell: one line per fleet node id, each carrying its own succession state (idea
    /// 6be68ee2) — <c>(root key)</c> for a node whose own vouched key a validated rotation promoted
    /// into this root's live key set, <c>(successor)</c> for a node with a currently live successor
    /// record awaiting one, and no suffix at all for the root's own original node or an ordinary
    /// fleet node with neither. A one-node fleet with no successor listed at all says so plainly
    /// (idea 6be68ee2, journal finding 10: "an owner with only one node has no heir"), since a quiet
    /// cell here would otherwise look identical to a fleet that simply has not been asked about yet.
    /// </summary>
    internal static string DescribeFleet(IReadOnlyList<Guid> nodes, TrustedOwner? owner)
    {
        if (nodes.Count == 0)
        {
            return "[dim]none yet[/]";
        }

        string cell = string.Join("\n", nodes.Select(nodeId => DescribeFleetNode(nodeId, owner)));
        if (owner is not null && nodes.Count == 1 && owner.SuccessorNodeIds.Count == 0 && owner.RootKeys.Count == 1)
        {
            cell += "\n[dim]no successor[/]";
        }

        return cell;
    }

    private static string DescribeFleetNode(Guid nodeId, TrustedOwner? owner)
    {
        string label = nodeId.ToString().EscapeMarkup();
        if (owner is null)
        {
            return label;
        }

        string nodeIdText = nodeId.ToString();
        if (owner.RootKeys.Any(key => key.IntroducedByNodeId == nodeIdText))
        {
            return $"{label} [dim](root key)[/]";
        }

        if (owner.SuccessorNodeIds.Contains(nodeIdText))
        {
            return $"{label} [dim](successor)[/]";
        }

        return label;
    }

    /// <summary>
    /// The Root cell for one member: the fingerprint, plus a second, dimmed line carrying the
    /// newest display name declared across that root's own nodes when one is declared. Never a new
    /// column, so it never disturbs the Login and Verified cells' own line-for-line alignment.
    /// </summary>
    internal static string RenderRoot(string rootFingerprint, DisplayName displayName) =>
        displayName.HasValue
            ? $"{rootFingerprint.EscapeMarkup()}\n[dim]{RenderDisplayName(displayName)}[/]"
            : rootFingerprint.EscapeMarkup();

    /// <summary>The longest display name this reader ever wrote itself (<c>DisplayName</c>'s own
    /// 1-to-64 rule) — also the bound applied to a peer's, since that value never goes through
    /// <c>DisplayName.Parse</c> to enforce it (see <see cref="RenderDisplayName"/>).</summary>
    private const int DisplayNameRenderLimit = 64;

    /// <summary>
    /// A display name is read from another node's own <c>node.yaml</c> through
    /// <c>NodeFileWriter.ReadDisplayName</c>, which wraps it in <c>DisplayName.Trusted</c> and
    /// deliberately skips <c>DisplayName.Parse</c>'s own length and control-character rule — a
    /// self-signed rewrite of a peer's own file is all a member needs to put anything at all into
    /// this value, including a raw escape sequence meant to repaint or overwrite this table
    /// (independent pre-PR review, cycle 1, adversarial lens, medium). Sanitized here the same way
    /// any other text this node relays but did not author is before it reaches a terminal
    /// (<see cref="ExternalText.OneLineMarkup"/>), plus the length bound this node's own
    /// <c>h9k owner set</c> would have enforced had this member set it locally.
    /// </summary>
    private static string RenderDisplayName(DisplayName displayName) =>
        ExternalText.OneLineMarkup(RelayedText.Truncate(displayName.Value, DisplayNameRenderLimit));

    /// <summary>
    /// The Login and Verified cells for one member: one line per distinct declared account, the two
    /// columns aligned line for line, or "unknown" in both when none of the member's nodes declares one.
    /// </summary>
    internal static (string Login, string Standing) RenderAccounts(
        IReadOnlyList<DeclaredGitHubAccount> accounts, ProjectGitHubRoster roster) =>
        accounts.Count == 0
            ? ("[dim]unknown[/]", "[dim]unknown[/]")
            : (
                string.Join("\n", accounts.Select(account => account.Login.EscapeMarkup())),
                string.Join("\n", accounts.Select(account => DescribeStanding(roster.Check(account)))));

    internal static string DescribeStanding(DeclaredAccountStanding standing) =>
        standing switch
        {
            DeclaredAccountStanding.PushConfirmed => "[green]declared, push confirmed[/]",
            DeclaredAccountStanding.ReadOnly => "[yellow]declared, read only[/]",
            DeclaredAccountStanding.NotACollaborator => "[red]declared, not a collaborator[/]",
            _ => "[dim]declared, unchecked here[/]",
        };

    /// <summary>The line printed under the table when the roster is not a fresh read, or null when it is.</summary>
    internal static string? RosterNote(ProjectGitHubRoster roster) =>
        roster switch
        {
            { Live: true } => null,
            { Held: true, AsOf: { } asOf } => $"collaborator roster as of {asOf:u}",
            _ => "collaborator roster unavailable: gh did not answer and this node has none stored",
        };

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
