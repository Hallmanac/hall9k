namespace Hall9k.Domain.Features.Tasks.Events;

/// <param name="ClearInteractiveMode">
/// True when this requeue is itself the human's explicit act of returning the task to the
/// machine — <c>h9k task release</c>'s own default (task: interactive mode becomes a recorded
/// property of the task, design ruling R6, amended 2026-09-05: a default release is an exit door
/// exactly like <c>h9k task handback</c>, since headless dispatch must not gate phase boundaries
/// for a human who walked away). False for every other <see cref="Handlers.TaskDecider.Requeue"/>
/// caller — a node's own lease expiring (<c>DispatchEngine</c>) is not a human decision about
/// interactive mode and must never turn the flag off — and for a release given
/// <c>--keep-interactive</c>, the operator's own explicit ask for a headless run that still parks
/// at each boundary.
/// </param>
public sealed record TaskRequeued(
    Guid Id,
    RequeueReason Reason,
    DateTimeOffset RequeuedAt,
    bool ClearInteractiveMode = false);
