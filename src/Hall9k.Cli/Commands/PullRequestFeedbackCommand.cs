using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The owner's decision on feedback from someone outside the project's trust-chain roster (task: a
/// changes-requested review or an unresolved review thread from a non-member never dispatches a
/// build lap; it raises a needs-you item, and only the owner's explicit go dispatches a fix lap for
/// it). Closeout holds such feedback and raises one item per review or thread on <c>h9k status</c>;
/// this command is how that item is answered.
/// <para>
/// <c>--go</c> records that the feedback may be read like a member's. The closeout monitor's next
/// poll then dispatches the one fix lap that carries it, even when the pull request's head has moved
/// since the feedback was observed. <c>--decline</c> records that nothing is to be dispatched for
/// it: a declined review stops holding a pre-approved merge, while a declined thread stays unresolved
/// on GitHub and goes on holding the merge, as any unresolved thread does. Either clears the item.
/// With no feedback named it only lists what the task holds, and never prints a word the
/// reviewer wrote.
/// </para>
/// <para>
/// Refused unless this node's owner may act on the task, the same rule every other owner act on a
/// task follows, and replicated as an owner-only act: a Member-role teammate's go is dropped by every
/// other node, since the go is what lets a stranger's text reach a session that skips permissions.
/// </para>
/// </summary>
public sealed class PullRequestFeedbackCommand : Hall9kAsyncCommand<PullRequestFeedbackCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Task { get; init; } = string.Empty;

        [CommandArgument(1, "[FEEDBACK]")]
        [Description(
            "The feedback to decide: the review's link or the thread's id exactly as h9k status prints it, or "
            + "an unambiguous fragment of either. Omit it to list the non-member feedback this task holds.")]
        public string? Feedback { get; init; }

        [CommandOption("--go")]
        [Description(
            "Give the go: read this feedback like a member's. The next closeout poll dispatches one fix lap "
            + "carrying it, even if the pull request's head has moved since it was written. Only you can say this "
            + "for a stranger's words: it is what lets them reach a session that skips permissions.")]
        public bool Go { get; init; }

        [CommandOption("--decline")]
        [Description(
            "Decline it: nothing is dispatched for this feedback. A declined review stops holding a pre-approved "
            + "merge; a declined thread stays unresolved on GitHub and keeps holding the merge until it is resolved there.")]
        public bool Decline { get; init; }

        public override ValidationResult Validate() =>
            (Go, Decline, Feedback.IsNotBlank()) switch
            {
                (true, true, _) => ValidationResult.Error("Pass at most one of --go and --decline: they are opposite answers."),
                (true, false, false) or (false, true, false) =>
                    ValidationResult.Error("Name the feedback to decide, as h9k status prints it, after the task."),
                (false, false, true) =>
                    ValidationResult.Error("Say what to do with it: --go to have a fix lap read it, or --decline to dispatch nothing."),
                _ => ValidationResult.Success(),
            };
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await DecideAsync(session, settings, cancellationToken);
    }

    /// <summary>
    /// The whole command over a session the caller supplies — internal so the owner's go and decline can
    /// be exercised against a real store without <see cref="CliStore.Open"/>'s ambient connection, the
    /// same seam <c>PullRequestReplyCommand.ReplyAsync</c> gives its own tests.
    /// </summary>
    internal static async Task<int> DecideAsync(
        IDocumentSession session, Settings settings, CancellationToken cancellationToken)
    {
        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);

        // Fenced like every other owner act on a task the closeout monitor also writes: the append
        // below carries expectedVersion, so a poll racing this command loses loudly instead of
        // deciding against a task that moved underneath it.
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (settings.Feedback.IsBlank())
        {
            foreach (string line in ComposeListing(task))
            {
                AnsiConsole.MarkupLine(line);
            }

            return ExitCodes.Ok;
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await TaskOwnerGuard.AssertMayActAsync(session, task, context, cancellationToken);

        DateTimeOffset decidedAt = DateTimeOffset.UtcNow;
        NonMemberFeedbackItem item = TaskDecider.FindUndecidedNonMemberFeedback(task, settings.Feedback);
        session.Events.Append(
            taskId, expectedVersion: fence.Version + 1,
            settings.Go
                ? TaskDecider.AdmitNonMemberFeedback(task, settings.Feedback, decidedAt, context.OwnerId)
                : TaskDecider.DeclineNonMemberFeedback(task, settings.Feedback, decidedAt, context.OwnerId));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while this was recorded — the closeout monitor likely just wrote to it. "
                + "Run h9k pr feedback again; nothing was recorded.");
        }

        await Doorbell.RingAsync($"pr-feedback:{taskId}", cancellationToken);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]{ComposeConfirmation(item, settings.Go)}[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// What the command says back. Names the reviewer, the kind and the link and nothing else: the
    /// feedback's own words are never printed, here or on <c>h9k status</c>.
    /// </summary>
    internal static string ComposeConfirmation(NonMemberFeedbackItem item, bool go)
    {
        string subject = $"{item.Kind.Noun} from @{ExternalText.OneLine(item.Reviewer)} ({ExternalText.OneLine(item.Url)})";
        return go
            ? $"Go recorded for the {subject}. The next closeout poll dispatches one fix lap that carries it."
            : item.Kind == NonMemberFeedbackKind.Thread
                ? $"Declined the {subject}. Nothing is dispatched for it; the thread stays unresolved on GitHub and keeps holding a pre-approved merge until it is resolved there."
                : $"Declined the {subject}. Nothing is dispatched for it, and it no longer holds a pre-approved merge here. "
                    + "GitHub's own review decision still counts it until it is dismissed there.";
    }

    /// <summary>One line per piece of feedback the task holds, in the order it was observed, with its status and no text of its own.</summary>
    internal static IReadOnlyList<string> ComposeListing(TaskAggregate task) =>
        ComposeListing(task.Id, task.NonMemberFeedback.Values);

    internal static IReadOnlyList<string> ComposeListing(Guid taskId, IEnumerable<NonMemberFeedbackItem> held)
    {
        IReadOnlyList<NonMemberFeedbackItem> items = [.. held.OrderBy(item => item.ObservedAt)];
        return items.Count == 0
            ? [$"[dim]Task {TaskListCommand.ShortId(taskId)} holds no feedback from outside the project's roster.[/]"]
            : [.. items.Select(item =>
                $"{item.Status.Value.EscapeMarkup()}: {NonMemberFeedbackPane.Describe(item)}")];
    }
}
