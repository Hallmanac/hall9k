using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Reads a solution invite bundle and, for each member this node has not already joined, registers it
/// (when no local project is that member) and joins it with that member's secret, through the same
/// registration and join <c>h9k project add --repo-url</c> and <c>h9k project join</c> run. It needs no
/// solution record, membership, or member project on this node, never prompts, and a member that does
/// not finish never stops the others.
/// </summary>
public sealed class SolutionJoinCommand : Hall9kAsyncCommand<SolutionJoinCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--invite <BUNDLE>")]
        [Description(
            "The invite bundle h9k solution invite printed: one line holding the solution and, for each member, its "
            + "project key, name, repository URL and single-use secret. Pass '-' to read it from stdin instead of argv "
            + "(keeps the secrets out of shell history); passing it directly still works but prints a one-line warning")]
        public string? Invite { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Invite.IsBlank())
        {
            throw new DomainValidationException(
                "Pass --invite <bundle>, the line h9k solution invite printed on the node that minted it, or --invite - "
                + "to read it from stdin.");
        }

        string? raw = await InviteTokenInput.ResolveAsync(settings.Invite, cancellationToken);
        SolutionInviteBundle bundle = SolutionInviteBundle.Parse(raw ?? string.Empty);

        using var store = CliStore.Open();
        SolutionContext context;
        await using (IDocumentSession session = store.LightweightSession())
        {
            context = await SolutionContext.LoadAsync(session, cancellationToken);
        }

        IReadOnlyList<SolutionJoinStep> plan = SolutionJoinPlanner.Plan(bundle, context.Projects, context.RootFingerprintOf);

        if (plan.Any(step => step.Action is SolutionJoinAction.Register or SolutionJoinAction.Join))
        {
            await RefreshGitHubIdentityAsync(store, cancellationToken);
        }

        IReadOnlyList<SolutionJoinOutcome> outcomes = await SolutionJoinRunner.RunAsync(
            plan, (step, token) => JoinMemberAsync(store, step, token), cancellationToken);

        SolutionJoinReport.Write(bundle.SolutionName, outcomes);
        return SolutionJoinReport.ExitCode(outcomes);
    }

    /// <summary>The same live refresh h9k project join makes before its own join, so an install bootstrapped before gh was authenticated is not refused for a stale identity. Best-effort.</summary>
    private static async Task RefreshGitHubIdentityAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        GhIdentityReader ghIdentityReader = ProjectGitHubClient.AmbientIdentityReader(Environment.CurrentDirectory);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken, ghIdentityReader);
        await NodeBootstrap.RefreshGitHubIdentityAsync(session, context.ConnectionId, cancellationToken, ghIdentityReader);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>One member's real work, on a session of its own so one member's failed write cannot reach the next.</summary>
    private static async Task<SolutionJoinOutcome> JoinMemberAsync(
        IDocumentStore store, SolutionJoinStep step, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        SolutionMember member = step.Entry.Member;
        string secret = step.Entry.Secret;

        if (step.Action == SolutionJoinAction.Join && step.Local is { } local)
        {
            ProjectJoinCommand.JoinOutcome joined = await ProjectJoinCommand.RunAsync(
                session, local, claimedOwnerOverride: null, secret, fromProject: null,
                new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new NodeKeyStore(), new ProjectGitHubAccessMirror(),
                new GitLedgerChainReader(), new GitLedgerCommitReader(), promptForInviteToken: null, cancellationToken);
            return joined.Deferred
                ? SolutionJoinOutcome.NotFinished(
                    member, "the join was deferred rather than written", SolutionJoinHandCommands.Join(local.Name))
                : SolutionJoinOutcome.Joined(member);
        }

        ProjectAddCommand.Settings settings = new() { Name = member.Name, RepositoryUrl = member.RepositoryUrl.ToString() };
        ProjectAddCommand.Registration registration;
        try
        {
            registration = await ProjectAddCommand.RegisterAsync(
                session, settings, homeOverride: null, pendingRenameMessage: null, secret, unattended: true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SolutionJoinOutcome.NotFinished(member, exception.Message, SolutionJoinHandCommands.Add(member));
        }

        return registration.Join switch
        {
            { Joined: true } => SolutionJoinOutcome.RegisteredAndJoined(member),
            { RepositoryUnreachable: true } => SolutionJoinOutcome.NotFinished(
                member,
                "registered, but its repository did not finish cloning, so there was nothing to join against "
                + $"({registration.RepositoryPath})",
                SolutionJoinHandCommands.Join(member.Name)),
            { Failure: { } failure } => SolutionJoinOutcome.NotFinished(
                member, $"registered, but the join did not finish: {failure}", SolutionJoinHandCommands.Join(member.Name)),
            _ => SolutionJoinOutcome.NotFinished(
                member, "registered, but the join was deferred rather than written", SolutionJoinHandCommands.Join(member.Name)),
        };
    }
}
