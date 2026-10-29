using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Mints one member-of-project invite for each member of a solution this node can, through the same
/// mint <c>h9k project invite</c> performs (no new trust semantics: the owner role in that project's
/// own chain is still what gates it), and prints them once as a single invite bundle.
/// </summary>
public sealed class SolutionInviteCommand : Hall9kAsyncCommand<SolutionInviteCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SOLUTION>")]
        [Description("The solution: its name, or a prefix of its key (h9k solution list shows them)")]
        public string Solution { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();

        SolutionContext context;
        await using (IDocumentSession session = store.LightweightSession())
        {
            context = await SolutionContext.LoadAsync(session, cancellationToken);
        }

        SolutionView view = SolutionRegistry.Match(context.Solutions, settings.Solution);
        SolutionDecider.RefuseClosed(view.Membership, "have invites minted for its members");
        ILedger ledger = new GitLedger(new ConsoleWorktreeLogger<GitLedger>());
        ILedgerChainReader chainReader = new GitLedgerChainReader();
        NodeKeyStore keyStore = new();

        SolutionMintResult result = await SolutionInviteMinter.MintAsync(
            view.Membership,
            context.Projects,
            context.RootFingerprintOf,
            async (project, token) =>
            {
                // One session per mint: a refusal after the mint began must not leave a half-written stream for the next member's save.
                await using IDocumentSession mintSession = store.LightweightSession();
                return await ProjectInviteCommand.MintAsync(
                    mintSession, project, ProjectMemberRole.Member, ledger, chainReader, keyStore, token);
            },
            cancellationToken);

        // With nothing minted the refusals are the failure's own message below; listing them first would say each twice.
        if (result.Minted.Count == 0)
        {
            throw new DomainValidationException(
                $"No invite was minted for any member of '{view.Membership.Name}': "
                + string.Join(
                    "; ", result.Refused.Select(refusal => $"'{refusal.Member.Name}': {refusal.Reason}")));
        }

        foreach (SolutionMintRefusal refusal in result.Refused)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No invite for '{refusal.Member.Name.EscapeMarkup()}'[/] ({refusal.Member.RepositoryUrl.ToString().EscapeMarkup()}): "
                + refusal.Reason.EscapeMarkup());
        }

        foreach (SolutionMintedMember minted in result.Minted)
        {
            AnsiConsole.MarkupLine(
                $"[green]Minted[/] a member-of-project ({minted.Invite.Role.Value}) invite for '{minted.Member.Name.EscapeMarkup()}', "
                + $"expiring {minted.Invite.ExpiresAt:u}.");
        }

        AnsiConsole.MarkupLine(
            $"[bold]Invite bundle for '{view.Membership.Name.EscapeMarkup()}' (shown once, copy it now):[/]");

        // Written raw, not through the markup renderer, which would wrap a long line at the console's width.
        Console.Out.WriteLine(result.ToBundle(view.Membership).Encode());

        AnsiConsole.MarkupLine(
            "[dim]Give the whole line to the new teammate. On their own node: h9k solution join --invite - "
            + "(pipe it in rather than pasting it into a terminal prompt, whose line editor can cut a long line) registers and joins each member they have not already joined. Each member still "
            + "needs push access on its repository, and this node's own daemon must be running to vouch each join in "
            + "on its next sweep; no further prompt is needed.[/]");
        return ExitCodes.Ok;
    }
}
