using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The CLI half of "only the idea's assignee, or its creator when it has none, decides its fate"
/// (card D of idea 8d0b724b), the twin of <see cref="TaskOwnerGuard"/> for an idea. It asks
/// <see cref="TaskOwnerRule"/> over verified owner roots (an idea has no ledger holder, so that fact
/// is always absent), the same question every peer's receive gate asks of the matching event, and
/// refuses with the same words and the same Owner-role override a task's guard gives
/// (<c>--holder</c> with <c>--reason</c>). Cutting a task from an idea and recording a spike's
/// verdict never come through here: they stay open to any member.
/// </summary>
internal static class IdeaOwnerGuard
{
    /// <summary>
    /// Decides whether this node's owner root may <paramref name="verb"/> <paramref name="idea"/>,
    /// and when it may not, whether an Owner-role member's override was asked for and granted. The
    /// Owner-role check, which reads the project's ledger, runs only once everything cheaper has
    /// agreed. Returns the own-act decision, or the override, and throws otherwise. A node whose owner
    /// has no root fingerprint yet has nothing a teammate could reach or fork, so it is let through.
    /// </summary>
    public static async Task<TaskOwnerOverrideDecision> AuthorizeAsync(
        IDocumentSession session, IdeaAggregate idea, BootstrapContext context, string verb,
        string? holder, string? reason, ILedgerChainReader chainReader, NodeKeyStore keyStore,
        CancellationToken cancellationToken)
    {
        IdeaOwnerEvaluation evaluation = await EvaluateAsync(session, idea, context, chainReader, cancellationToken);
        TaskOwnerOverrideDecision decision = TaskOwnerOverride.Decide(
            idea.Id, verb, evaluation.Check, evaluation.OwnerLabel, evaluation.AssigneeLabel, holder, reason,
            OwnerRoleCheck.NotChecked, TaskOwnerRefusal.IdeaNoun);

        if (decision.Outcome == TaskOwnerOverrideOutcome.NeedsRoleCheck)
        {
            OwnerRoleCheck roleCheck = await CheckOwnerRoleAsync(
                session, idea, context, verb, chainReader, keyStore, cancellationToken);
            decision = TaskOwnerOverride.Decide(
                idea.Id, verb, evaluation.Check, evaluation.OwnerLabel, evaluation.AssigneeLabel, holder, reason,
                roleCheck, TaskOwnerRefusal.IdeaNoun);
        }

        return decision.Outcome == TaskOwnerOverrideOutcome.Refused
            ? throw new DomainBusinessRuleException(decision.Message ?? string.Empty)
            : decision;
    }

    /// <summary>Whether this node's owner root may act on <paramref name="idea"/> without an override.</summary>
    public static async Task<bool> MayActAsync(
        IDocumentSession session, IdeaAggregate idea, BootstrapContext context, ILedgerChainReader chainReader,
        CancellationToken cancellationToken) =>
        (await EvaluateAsync(session, idea, context, chainReader, cancellationToken)).Check.MayAct;

    private static async Task<OwnerRoleCheck> CheckOwnerRoleAsync(
        IDocumentSession session, IdeaAggregate idea, BootstrapContext context, string verb,
        ILedgerChainReader chainReader, NodeKeyStore keyStore, CancellationToken cancellationToken)
    {
        if (idea.ProjectId is not { } projectId)
        {
            return OwnerRoleCheck.Failed(
                $"idea {idea.Id} belongs to no project yet, so there is no project role to check. "
                + $"Move it to one first: h9k idea move {idea.Id} <project>");
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(projectId, cancellationToken);
        if (project is null)
        {
            return OwnerRoleCheck.Failed($"project {projectId} is not known on this node.");
        }

        try
        {
            await TaskTakeCommand.AssertOwnerRoleAsync(
                session, context, project, chainReader, keyStore,
                retryCommand: $"h9k idea {verb} {idea.Id} --holder <owner> --reason \"...\"",
                action: $"{verb} another owner's idea",
                cancellationToken: cancellationToken);
            return OwnerRoleCheck.Passed;
        }
        catch (DomainException exception)
        {
            return OwnerRoleCheck.Failed(exception.Message);
        }
    }

    private static async Task<IdeaOwnerEvaluation> EvaluateAsync(
        IDocumentSession session, IdeaAggregate idea, BootstrapContext context, ILedgerChainReader chainReader,
        CancellationToken cancellationToken)
    {
        string? actingRoot = await OwnerRootFingerprintResolver.ResolveAsync(session, context.OwnerId, cancellationToken);
        if (string.IsNullOrEmpty(actingRoot))
        {
            return new IdeaOwnerEvaluation(TaskOwnerCheck.Permitted(string.Empty), null, null);
        }

        TaskOwnerFacts facts = await IdeaOwnerFactsReader.ReadAsync(session, idea, cancellationToken);
        facts = await ConfirmCreatorFromOwnFleetAsync(session, idea, facts, actingRoot, chainReader, cancellationToken);
        TaskOwnerCheck check = TaskOwnerRule.Decide(actingRoot, facts);
        if (check.MayAct)
        {
            return new IdeaOwnerEvaluation(check, null, null);
        }

        MemberLabelLookup labels = await MemberLabelling.LoadAsync(
            session, idea.ProjectId ?? Guid.Empty, actingRoot, cancellationToken);
        return new IdeaOwnerEvaluation(check, Label(labels, check.OwnerRootFingerprint), Label(labels, check.AssigneeRootFingerprint));
    }

    /// <summary>
    /// An idea that replicated to this node from another node of the acting owner's own fleet has no
    /// creator this node could verify yet (the receive gate confirms one only from a later act delivered
    /// directly from the origin), so the creator would read as unknown and an Owner-role override be the
    /// only way for a Member-role creator to decide their own idea from a second machine. When the node the
    /// idea claims to come from is one the project's ledger vouches into the acting owner's own fleet, the
    /// acting root is the creator, so it is read as that. The claim is only trusted this far because the
    /// fleet is the ledger's; a peer still judges the act it receives by its own verified record. Reads the
    /// ledger only for an unresolved creator with no assignee, and falls back to the unresolved fact
    /// whenever the fleet cannot be read.
    /// </summary>
    private static async Task<TaskOwnerFacts> ConfirmCreatorFromOwnFleetAsync(
        IDocumentSession session, IdeaAggregate idea, TaskOwnerFacts facts, string actingRoot,
        ILedgerChainReader chainReader, CancellationToken cancellationToken)
    {
        if (facts.Creator.State != OwnerRootFactState.Unresolved
            || await IdeaOwnerFactsReader.ReadClaimedOriginNodeAsync(session, idea, cancellationToken) is not { } claimedOrigin
            || idea.ProjectId is not { } projectId
            || await session.LoadAsync<ProjectDetails>(projectId, cancellationToken) is not { } project)
        {
            return facts;
        }

        LocalFleet? fleet = await LocalFleet.ReadAsync(chainReader, project.RepositoryPath, actingRoot, cancellationToken);
        return fleet is not null && fleet.NodeIds.Contains(claimedOrigin)
            ? facts with { Creator = OwnerRootFact.Known(actingRoot) }
            : facts;
    }

    /// <summary>
    /// A label is read from another member's own self-signed <c>node.yaml</c>, so it is bounded and
    /// stripped to one line before it reaches a terminal or is compared with what an operator typed
    /// (<see cref="MemberLabelResolver.RenderLimit"/>).
    /// </summary>
    private static string? Label(MemberLabelLookup labels, string? rootFingerprint) =>
        rootFingerprint is null
            ? null
            : ExternalText.OneLine(RelayedText.Truncate(labels.LabelForFingerprint(rootFingerprint), MemberLabelResolver.RenderLimit));

    private sealed record IdeaOwnerEvaluation(TaskOwnerCheck Check, string? OwnerLabel, string? AssigneeLabel);
}
