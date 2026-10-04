using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The CLI half of "a teammate cannot end or hand away another owner's task by accident". The
/// receive gate lets any Owner-role sender's act through and drops a Member-role sender's on every
/// node but its own, so a plain command against another owner's task either forks the stream or
/// ends the owner's work fleet-wide. Every command that acts on a task asks
/// <see cref="TaskOwnerRule"/> first, with the receive gate's own ownership rule, and refuses
/// when this node's owner root may not act. Abandon, resolve, unassign and dequeue alone take the
/// deliberate override (<see cref="AuthorizeAsync"/>).
/// </summary>
internal static class TaskOwnerGuard
{
    /// <summary>
    /// Refuses unless this node's owner root may act on <paramref name="task"/>. A node whose owner
    /// has no root fingerprint yet has nothing a teammate could reach or fork, so it is let through.
    /// </summary>
    public static async Task AssertMayActAsync(
        IDocumentSession session, TaskAggregate task, BootstrapContext context, CancellationToken cancellationToken)
    {
        TaskOwnerEvaluation evaluation = await EvaluateAsync(session, task, context, cancellationToken);
        if (!evaluation.Check.MayAct)
        {
            throw new DomainBusinessRuleException(TaskOwnerRefusal.Describe(
                task.Id, evaluation.Check, evaluation.OwnerLabel, evaluation.AssigneeLabel));
        }
    }

    /// <summary>
    /// Whether this node's owner root may act on <paramref name="task"/>, the question
    /// <see cref="AssertMayActAsync"/> refuses on, for a caller choosing among several tasks before
    /// it has anything to refuse.
    /// </summary>
    public static async Task<bool> MayActAsync(
        IDocumentSession session, TaskAggregate task, BootstrapContext context, CancellationToken cancellationToken) =>
        (await EvaluateAsync(session, task, context, cancellationToken)).Check.MayAct;

    /// <summary>
    /// The guard for the three commands an Owner-role member may still run against another
    /// owner's task, by naming that owner with <paramref name="holder"/> and saying why with
    /// <paramref name="reason"/>. The Owner-role check (the one <c>h9k task take --force</c> makes)
    /// runs only once everything cheaper has already agreed, since it reads the project's ledger.
    /// Returns the own-act decision when this node's root may act, the override when it was
    /// granted, and throws otherwise.
    /// </summary>
    public static async Task<TaskOwnerOverrideDecision> AuthorizeAsync(
        IDocumentSession session, TaskAggregate task, BootstrapContext context, string verb,
        string? holder, string? reason, ILedgerChainReader chainReader, NodeKeyStore keyStore,
        CancellationToken cancellationToken)
    {
        TaskOwnerEvaluation evaluation = await EvaluateAsync(session, task, context, cancellationToken);
        TaskOwnerOverrideDecision decision = TaskOwnerOverride.Decide(
            task.Id, verb, evaluation.Check, evaluation.OwnerLabel, evaluation.AssigneeLabel, holder, reason,
            OwnerRoleCheck.NotChecked);

        if (decision.Outcome == TaskOwnerOverrideOutcome.NeedsRoleCheck)
        {
            OwnerRoleCheck roleCheck = await CheckOwnerRoleAsync(
                session, task, context, verb, chainReader, keyStore, cancellationToken);
            decision = TaskOwnerOverride.Decide(
                task.Id, verb, evaluation.Check, evaluation.OwnerLabel, evaluation.AssigneeLabel, holder, reason,
                roleCheck);
        }

        return decision.Outcome == TaskOwnerOverrideOutcome.Refused
            ? throw new DomainBusinessRuleException(decision.Message ?? string.Empty)
            : decision;
    }

    /// <summary>
    /// Refuses a command that would queue a Published task for this node's owner when another owner
    /// holds it, by its assignee: <c>h9k task start</c>, <c>h9k task work</c> and
    /// <c>h9k task publish --queue</c> each take an unassigned Published task for the operator in the
    /// same append, which is exactly what laying hold of a draft must stop. Appends nothing. A task
    /// nobody holds, or this owner holds, passes.
    /// </summary>
    /// <param name="consequence">What this command would otherwise have done, finishing the sentence the refusal starts.</param>
    public static async Task AssertNotHeldByAnotherOwnerAsync(
        IDocumentSession session, TaskAggregate task, BootstrapContext context, string? ownerRootFingerprint,
        string consequence, CancellationToken cancellationToken)
    {
        if (!TaskDecider.IsHeldByAnotherOwner(task, context.OwnerId, ownerRootFingerprint))
        {
            return;
        }

        string? holderLabel = await AssigneeLabelAsync(session, task, ownerRootFingerprint, cancellationToken);
        throw new DomainConflictException(
            $"Task {DomainId.Short(task.Id)} is assigned to {holderLabel ?? "another owner"}; {consequence} "
            + $"They can release it with h9k task unassign {DomainId.Short(task.Id)}, or an Owner-role member "
            + "can do so on their behalf with --holder and --reason.");
    }

    /// <summary>
    /// Refuses <c>h9k task queue</c> when another owner is the task's assignee: queueing is the
    /// assignee's act alone, so the refusal names them and the way out, which is their own hand-off.
    /// A task nobody holds, or this owner holds, passes. Appends nothing.
    /// </summary>
    public static async Task AssertAssigneeMayQueueAsync(
        IDocumentSession session, TaskAggregate task, BootstrapContext context, string? ownerRootFingerprint,
        CancellationToken cancellationToken)
    {
        if (!TaskDecider.IsHeldByAnotherOwner(task, context.OwnerId, ownerRootFingerprint))
        {
            return;
        }

        string shortId = DomainId.Short(task.Id);
        string holderLabel = await AssigneeLabelAsync(session, task, ownerRootFingerprint, cancellationToken)
            ?? "another owner";
        throw new DomainConflictException(
            $"Task {shortId} is assigned to {holderLabel}, and only its assignee queues it. {holderLabel} can hand it "
            + $"to you with h9k task assign {shortId} <member>, and then you can queue it. An Owner-role member can "
            + "hand it off for them with --holder and --reason.");
    }

    private static async Task<string?> AssigneeLabelAsync(
        IDocumentSession session, TaskAggregate task, string? ownerRootFingerprint, CancellationToken cancellationToken)
    {
        OwnerDetails? holder = task.AssigneeOwnerId is { } holderOwnerId
            ? await session.LoadAsync<OwnerDetails>(holderOwnerId, cancellationToken)
            : null;
        string? holderLabel = holder?.Name;
        if (holderLabel is null && !string.IsNullOrEmpty(ownerRootFingerprint) && !string.IsNullOrEmpty(task.AssigneeOwnerFingerprint))
        {
            MemberLabelLookup labels = await MemberLabelling.LoadAsync(
                session, task.ProjectId, ownerRootFingerprint, cancellationToken);
            holderLabel = Label(labels, task.AssigneeOwnerFingerprint);
        }

        return holderLabel;
    }

    /// <summary>Says on the terminal that an override was recorded, so it is never mistaken for the owner's own act.</summary>
    public static void AnnounceOverride(TaskOwnerOverrideDecision decision, string pastTense)
    {
        if (decision.Outcome == TaskOwnerOverrideOutcome.Override)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Recorded as an override:[/] {pastTense} on another owner's behalf, reason: {decision.Reason}");
        }
    }

    private static async Task<OwnerRoleCheck> CheckOwnerRoleAsync(
        IDocumentSession session, TaskAggregate task, BootstrapContext context, string verb,
        ILedgerChainReader chainReader, NodeKeyStore keyStore, CancellationToken cancellationToken)
    {
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (project is null)
        {
            return OwnerRoleCheck.Failed($"project {task.ProjectId} is not known on this node.");
        }

        try
        {
            await TaskTakeCommand.AssertOwnerRoleAsync(
                session, context, project, chainReader, keyStore,
                retryCommand: $"h9k task {verb} {task.Id} --holder <owner> --reason \"...\"",
                action: $"{verb} another owner's task",
                cancellationToken: cancellationToken);
            return OwnerRoleCheck.Passed;
        }
        catch (DomainException exception)
        {
            return OwnerRoleCheck.Failed(exception.Message);
        }
    }

    private static async Task<TaskOwnerEvaluation> EvaluateAsync(
        IDocumentSession session, TaskAggregate task, BootstrapContext context, CancellationToken cancellationToken)
    {
        string? actingRoot = await OwnerRootFingerprintResolver.ResolveAsync(session, context.OwnerId, cancellationToken);
        if (string.IsNullOrEmpty(actingRoot))
        {
            return new TaskOwnerEvaluation(TaskOwnerCheck.Permitted(string.Empty), null, null);
        }

        TaskOwnerFacts facts = await TaskOwnerFactsReader.ReadAsync(session, task, cancellationToken);
        TaskOwnerCheck check = TaskOwnerRule.Decide(actingRoot, facts);
        if (check.MayAct)
        {
            return new TaskOwnerEvaluation(check, null, null);
        }

        MemberLabelLookup labels = await MemberLabelling.LoadAsync(session, task.ProjectId, actingRoot, cancellationToken);
        return new TaskOwnerEvaluation(check, Label(labels, check.OwnerRootFingerprint), Label(labels, check.AssigneeRootFingerprint));
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

    private sealed record TaskOwnerEvaluation(TaskOwnerCheck Check, string? OwnerLabel, string? AssigneeLabel);
}
