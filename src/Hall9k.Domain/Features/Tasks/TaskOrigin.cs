namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// Which install published the work this task is a copy of, when a second install adopted it from
/// the task record on the shared issue (task: a published task's GitHub issue carries the whole
/// task record). Null on every task written here, which is the overwhelming majority: an origin is
/// a fact about a mirror, and a task with none is local work rather than a task whose provenance
/// went unrecorded.
/// <para>
/// Every field is copied from the record verbatim, and every one of them describes the OTHER
/// install: <see cref="NodeId"/> and <see cref="TaskId"/> are ids in that install's own store and
/// mean nothing to a query here, which is exactly why they are worth keeping — they are how a
/// human standing at either machine can tell the two copies are the same work.
/// <see cref="NodeName"/> travels beside the node id because an id alone tells nobody which
/// machine it was, and the adopting install has no way to look it up.
/// </para>
/// <para>
/// <see cref="BranchName"/> is the branch the origin cuts for the work — the one thing a task on
/// this install cannot derive, since the name comes from the origin project's own branch template
/// and its own id (Decisions Log #144's cross-install stacked child is what needs it).
/// <see cref="PublishedAt"/> stamps the record, not the adoption: it says how old the copy that was
/// read is, which is the whole of what the snapshot rule (Decisions Log #60) leaves the adopting
/// install to judge by.
/// </para>
/// </summary>
public sealed record TaskOrigin(
    Guid NodeId,
    string NodeName,
    Guid TaskId,
    string? BranchName,
    DateTimeOffset PublishedAt);
