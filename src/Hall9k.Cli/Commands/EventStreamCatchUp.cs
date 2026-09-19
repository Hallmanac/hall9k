using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Extensions;
using JasperFx.Events;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The one place a CLI command asks this project's other members for history it does not hold
/// (task a56cf16e): <c>h9k task add --from-issue</c>'s own missing-stream refusal, <c>h9k task
/// pull</c>, and <c>h9k project pull</c> all queue the identical broadcast through
/// <see cref="EventCatchUpCoordinator"/> and all report the identical three outcomes, so a human
/// who has read one of these messages has read all of them.
/// <para>
/// The outcome that matters most is <see cref="RequestDisposition.NoOwnerRoot"/>. A node with no
/// owner root fingerprint has no identity to send an envelope FROM, so no request is queued at all
/// — and the refusal has to say so rather than promise the stream will turn up, which is the thing
/// that actually went wrong on 2026-09-19: a node re-ran <c>--from-issue</c> and was told, in the
/// same breath as queueing nothing, that the task would appear once catch-up brought it in.
/// </para>
/// </summary>
internal static class EventStreamCatchUp
{
    /// <summary>What asking for history came to — an in-process outcome three commands print,
    /// never persisted (AGENTS.md's own enum rule).</summary>
    internal enum RequestDisposition
    {
        /// <summary>This call queued the request; the daemon's next message sweep sends it.</summary>
        Queued,

        /// <summary>An identical request from an earlier run is still in flight. A broadcast never
        /// times out and never exhausts on its own, so this means "already on its way", never
        /// "this run's ask was suppressed" — telling a human otherwise has them re-running a
        /// command that can never queue anything new for that outcome.</summary>
        AlreadyOutstanding,

        /// <summary>Nothing was queued, and nothing can be until <c>h9k project join</c> runs here.</summary>
        NoOwnerRoot,
    }

    /// <summary>What this node already holds under a task id — an in-process outcome two commands
    /// branch on, never persisted (AGENTS.md's own enum rule).</summary>
    internal enum LocalStreamHold
    {
        /// <summary>No stream at all under this id: the one shape an events-request can actually fill.</summary>
        Absent,

        /// <summary>A stream exists, but no event on it comes from the task feature at all — some
        /// other kind of id (a project, idea, epic, run, or node), since every id in this platform
        /// is a stream id.</summary>
        NotATask,

        /// <summary>A task stream whose own <c>TaskAdded</c> is here: the whole history, nothing to ask for.</summary>
        Whole,

        /// <summary>A task stream missing its own <c>TaskAdded</c> — the post-switch-on tail an
        /// ordinary flush shipped, with the head that created the task still only on the node that
        /// produced it. See <see cref="PartiallyHeldRefusal"/> for why no ask can fix it.</summary>
        Partial,
    }

    /// <summary>
    /// What this node holds under <paramref name="taskId"/>, read from the events rather than the
    /// <c>TaskListItem</c> projection: "does this node hold this task's history" is a question
    /// about events, and a projection rebuild or an inline projection that has not caught up would
    /// answer it wrongly in both directions. A task stream always opens with its own
    /// <see cref="TaskAdded"/>, so a task stream without one is a partial stream — nothing else
    /// produces that shape.
    /// </summary>
    public static async Task<LocalStreamHold> ClassifyLocalHoldAsync(
        IQuerySession session, Guid taskId, CancellationToken cancellationToken)
    {
        if (await session.Events.FetchStreamStateAsync(taskId, cancellationToken) is null)
        {
            return LocalStreamHold.Absent;
        }

        IReadOnlyList<IEvent> held = await session.Events.FetchStreamAsync(taskId, token: cancellationToken);
        return held switch
        {
            _ when held.Any(candidate => candidate.EventType == typeof(TaskAdded)) => LocalStreamHold.Whole,
            _ when held.Any(candidate => candidate.EventType.Namespace == typeof(TaskAdded).Namespace) =>
                LocalStreamHold.Partial,
            _ => LocalStreamHold.NotATask,
        };
    }

    /// <summary>Asks every member of <paramref name="projectId"/> for one stream this node lacks.
    /// The request document and its envelope land together, in one commit, because
    /// <c>MessageOutbox.QueueAsync</c> saves the session it is handed — so a request that is
    /// recorded is always one that was actually queued, never a bookkeeping row with no envelope
    /// behind it.</summary>
    public static async Task<RequestDisposition> RequestStreamAsync(
        IDocumentSession session, Guid projectId, Guid streamId, Guid nodeId, string? ownerRootFingerprint,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ownerRootFingerprint.IsBlank())
        {
            return RequestDisposition.NoOwnerRoot;
        }

        bool queued = await new EventCatchUpCoordinator().RequestStreamBroadcastAsync(
            session, projectId, streamId, nodeId, ownerRootFingerprint, now, cancellationToken);
        return queued ? RequestDisposition.Queued : RequestDisposition.AlreadyOutstanding;
    }

    /// <summary>Asks every member of <paramref name="projectId"/> for the project's whole history at
    /// or above <paramref name="sinceGlobalSequence"/> on whichever peer answers. Same
    /// one-commit guarantee as <see cref="RequestStreamAsync"/>.</summary>
    public static async Task<RequestDisposition> RequestProjectHistoryAsync(
        IDocumentSession session, Guid projectId, long sinceGlobalSequence, Guid nodeId, string? ownerRootFingerprint,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ownerRootFingerprint.IsBlank())
        {
            return RequestDisposition.NoOwnerRoot;
        }

        bool queued = await new EventCatchUpCoordinator().RequestProjectHistoryBroadcastAsync(
            session, projectId, sinceGlobalSequence, nodeId, ownerRootFingerprint, now, cancellationToken);
        return queued ? RequestDisposition.Queued : RequestDisposition.AlreadyOutstanding;
    }

    /// <summary>Why nothing was queued — the cause clause every
    /// <see cref="RequestDisposition.NoOwnerRoot"/> refusal in this codebase is built from, so the
    /// three of them cannot drift apart.</summary>
    public static string NoOwnerRootCause(string projectName) =>
        "this node has no owner root fingerprint, so it has no identity to address an events-request "
        + $"to '{projectName}'s other members from, and nothing was queued";

    /// <summary>
    /// The refusal both stream-asking commands print for a task stream this node holds only part
    /// of, which no events-request can repair (independent pre-PR review, cycle 4, adversarial
    /// lens). A replicated event is APPENDED to the local stream — Marten cannot put one in front
    /// of what is already there — so the pre-switch-on head an explicit pull would fetch would land
    /// behind the tail already here, and the stream would replay backwards:
    /// <c>TaskAggregate.Apply(TaskAdded)</c> running last resets the state and clears the
    /// acceptance criteria, leaving a published or finished task reading as newly queued.
    /// <c>EventReplicationInbox</c> refuses such a record rather than applying it, so saying so
    /// here is what keeps a human from queueing an ask that can only ever be refused on arrival.
    /// </summary>
    public static string PartiallyHeldRefusal(string taskShortId, string closing) =>
        $"Task {taskShortId}'s event stream is only partly on this node: what happened after this node "
        + "began replicating is here, but not the events that created the task. Nothing was queued, because "
        + "catch-up cannot fill that in — a replicated event is appended to the end of the local stream, and "
        + "history older than what is already here cannot be put in front of it without replaying the stream "
        + $"backwards. {closing}";

    /// <summary>The fix, and the command to type once it is done.</summary>
    public static string JoinFirst(string projectName, string retryCommand) =>
        $"Run h9k project join {projectName} first, then {retryCommand}.";

    /// <summary>
    /// The whole refusal <c>h9k task add --from-issue</c>/<c>--from-jira</c> prints when this
    /// project's ledger already carries a record for the item but the task's own event stream is
    /// not on this node — one sentence per disposition, because the three outcomes genuinely
    /// differ: two of them mean waiting is the right move, and one means waiting is futile. Pure so
    /// the wording is checkable without driving the whole command.
    /// </summary>
    public static string RecordedElsewhereRefusal(
        string reference, string taskShortId, string projectName, RequestDisposition disposition,
        bool projectEligibleForMessaging)
    {
        string head = $"{reference} is already published elsewhere as task {taskShortId}, but that task's "
            + "own event stream has not reached this node yet";
        return disposition switch
        {
            RequestDisposition.NoOwnerRoot =>
                $"{head}, and catch-up cannot bring that stream here until h9k project join has run on this "
                + $"node: {NoOwnerRootCause(projectName)}. {JoinFirst(projectName, "re-run this command")}",
            // An archived project is still resolvable and its ledger still readable, so this path
            // can queue a request for a project MessageSweepEngine never flushes — and "on its way
            // to this project's other members" would be a false promise there rather than an
            // optimistic one (independent pre-PR review, cycle 1, adversarial lens, low: the same
            // correction the two pull commands carry, since all three must read alike).
            _ when !projectEligibleForMessaging =>
                $"{head}. An events-request for that stream is queued here, but nothing leaves this node "
                + $"for '{projectName}' until it is eligible for messaging (not archived, with a repository), "
                + "so no member has been asked yet; re-run this command once the stream has landed.",
            RequestDisposition.AlreadyOutstanding =>
                $"{head} — an events-request for that stream is already on its way to this project's other "
                + "members. It will appear on this node's own board once catch-up brings that stream in; "
                + "re-run this command afterward.",
            _ =>
                $"{head} — an events-request for that stream is on its way to this project's other members "
                + "now. It will appear on this node's own board once catch-up brings that stream in; "
                + "re-run this command afterward.",
        };
    }

    /// <summary>The refusal <c>h9k task pull</c> prints when it cannot ask at all. Only
    /// <see cref="RequestDisposition.NoOwnerRoot"/> refuses there: the other two are outcomes that
    /// command reports and returns Ok on.</summary>
    public static string TaskPullBlockedRefusal(string taskIdText, string projectName) =>
        $"Task {taskIdText}'s event stream is not on this node, and catch-up cannot bring it here until "
        + $"h9k project join has run on this node: {NoOwnerRootCause(projectName)}. "
        + JoinFirst(projectName, $"h9k task pull {taskIdText}");

    /// <summary>The refusal <c>h9k project pull</c> prints when it cannot ask at all.</summary>
    public static string ProjectPullBlockedRefusal(string projectName) =>
        $"Catch-up cannot bring '{projectName}'s history to this node until h9k project join has run "
        + $"here: {NoOwnerRootCause(projectName)}. "
        + JoinFirst(projectName, $"h9k project pull {projectName} --since <global-sequence|all>");
}
